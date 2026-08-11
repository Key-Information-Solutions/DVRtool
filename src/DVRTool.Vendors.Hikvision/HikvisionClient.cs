using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text;
using System.Xml.Linq;
using DVRTool.Core;

namespace DVRTool.Vendors.Hikvision;

/// <summary>
/// Hikvision ISAPI client (also covers OEMs like LT Security). Uses the NVR's HTTP
/// interface with digest auth to enumerate channels, search on-disk recordings, and
/// download footage, plus RTSP URIs for live view and timeline playback.
///
/// ISAPI conventions used here:
///  - track/stream id = channel*100 + stream (ch1 main = 101, ch1 sub = 102)
///  - times are written "yyyy-MM-ddTHH:mm:ssZ" but the device treats them as its own
///    local wall-clock time, not UTC. We preserve that behavior.
/// </summary>
public sealed class HikvisionClient : INvrClient, IUserManagementClient
{
    private const string IsapiTimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const string RtspTimeFormat = "yyyyMMdd'T'HHmmss'Z'";

    private readonly HttpClient _http;
    private readonly HttpClient _downloadHttp;
    private readonly bool _ownsDownloadHttp;

    public Vendor Vendor => Vendor.Hikvision;
    public NvrConnection Connection { get; }

    public HikvisionClient(NvrConnection connection)
    {
        Connection = connection;
        _http = NvrHttp.Create(connection);
        // Downloads can run for many minutes; rely on CancellationToken instead of Timeout.
        _downloadHttp = NvrHttp.Create(connection, Timeout.InfiniteTimeSpan);
        _ownsDownloadHttp = true;
    }

    /// <summary>Test seam: routes all traffic through the supplied handler.</summary>
    public HikvisionClient(NvrConnection connection, HttpMessageHandler handler)
    {
        Connection = connection;
        _http = NvrHttp.Create(connection, handler: handler);
        _downloadHttp = _http;
        _ownsDownloadHttp = false;
    }

    public async Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        var doc = await GetXmlAsync("/ISAPI/System/deviceInfo", ct);
        return new DeviceInfo(
            Descendant(doc.Root, "deviceName") ?? "",
            Descendant(doc.Root, "model") ?? "",
            Descendant(doc.Root, "serialNumber") ?? "",
            Descendant(doc.Root, "firmwareVersion") ?? "");
    }

    public async Task<IReadOnlyList<Channel>> GetChannelsAsync(CancellationToken ct = default)
    {
        var channels = new Dictionary<int, Channel>();
        var failures = new List<Exception>();

        // IP cameras attached to an NVR.
        var proxies = await TryGetXmlAsync("/ISAPI/ContentMgmt/InputProxy/channels", ct, failures);
        if (proxies?.Root is not null)
        {
            foreach (var ch in ElementsNamed(proxies.Root, "InputProxyChannel"))
            {
                if (int.TryParse(Child(ch, "id"), out int id))
                    channels[id] = new Channel(id, Child(ch, "name") ?? $"Channel {id}");
            }
        }

        // Analog inputs on DVRs / hybrids.
        var analog = await TryGetXmlAsync("/ISAPI/System/Video/inputs/channels", ct, failures);
        if (analog?.Root is not null)
        {
            foreach (var ch in ElementsNamed(analog.Root, "VideoInputChannel"))
            {
                if (int.TryParse(Child(ch, "id"), out int id) && !channels.ContainsKey(id))
                    channels[id] = new Channel(id, Child(ch, "name") ?? $"Channel {id}");
            }
        }

        // Best-effort online status for IP channels (optional enrichment — failures ignored).
        var status = await TryGetXmlAsync("/ISAPI/ContentMgmt/InputProxy/channels/status", ct, null);
        if (status?.Root is not null)
        {
            foreach (var ch in ElementsNamed(status.Root, "InputProxyChannelStatus"))
            {
                if (int.TryParse(Child(ch, "id"), out int id) &&
                    channels.TryGetValue(id, out var existing))
                {
                    bool online = string.Equals(Child(ch, "online"), "true", StringComparison.OrdinalIgnoreCase);
                    channels[id] = existing with { Online = online };
                }
            }
        }

        // An empty list with real failures behind it (transport error, non-ISAPI
        // device) must not masquerade as "this NVR has no channels".
        if (channels.Count == 0 && failures.Count > 0)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();

        return channels.Values.OrderBy(c => c.Id).ToList();
    }

    public async Task<IReadOnlyList<RecordingSegment>> SearchAsync(
        int channel, DateTime start, DateTime end, CancellationToken ct = default)
    {
        var results = new List<RecordingSegment>();
        int trackId = TrackId(channel, StreamType.Main);
        string searchId = Guid.NewGuid().ToString("D").ToUpperInvariant();
        int position = 0;

        // Page through results. Guard against firmware that never says anything but MORE.
        bool hasMore = false;
        for (int page = 0; page < 200; page++)
        {
            ct.ThrowIfCancellationRequested();

            // "searchResultPostion" (sic) is Hikvision's own spelling — required verbatim.
            string body = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <CMSearchDescription>
                  <searchID>{searchId}</searchID>
                  <trackIDList><trackID>{trackId}</trackID></trackIDList>
                  <timeSpanList>
                    <timeSpan>
                      <startTime>{FormatIsapiTime(start)}</startTime>
                      <endTime>{FormatIsapiTime(end)}</endTime>
                    </timeSpan>
                  </timeSpanList>
                  <maxResults>40</maxResults>
                  <searchResultPostion>{position}</searchResultPostion>
                  <metadataList>
                    <metadataDescriptor>//recordType.meta.std-cgi.com</metadataDescriptor>
                  </metadataList>
                </CMSearchDescription>
                """;

            using var content = new StringContent(body, Encoding.UTF8, "application/xml");
            using var resp = await _http.PostAsync("/ISAPI/ContentMgmt/search", content, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new NvrException(
                    $"ISAPI search failed: {(int)resp.StatusCode} {resp.ReasonPhrase}",
                    text, (int)resp.StatusCode);

            XDocument doc;
            try
            {
                doc = XDocument.Parse(text);
            }
            catch (System.Xml.XmlException)
            {
                throw new NvrException(
                    "ISAPI search returned a non-XML response (web login page? wrong port?)", text);
            }
            string statusStr = Descendant(doc.Root, "responseStatusStrg") ?? "";
            int matches = int.TryParse(Descendant(doc.Root, "numOfMatches"), out int m) ? m : 0;

            foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "searchMatchItem"))
            {
                var timeSpan = item.Descendants().FirstOrDefault(e => e.Name.LocalName == "timeSpan");
                if (timeSpan is null)
                    continue;

                DateTime segStart = ParseIsapiTime(Child(timeSpan, "startTime"));
                DateTime segEnd = ParseIsapiTime(Child(timeSpan, "endTime"));
                string? playbackUri = item.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "playbackURI")?.Value;
                string? recordTypeDescriptor = item.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "metadataDescriptor")?.Value;

                results.Add(new RecordingSegment
                {
                    Channel = channel,
                    Start = segStart,
                    End = segEnd,
                    NativeId = playbackUri,
                    Type = MapRecordType(recordTypeDescriptor),
                });
            }

            hasMore = statusStr.Equals("MORE", StringComparison.OrdinalIgnoreCase) && matches > 0;
            if (!hasMore)
                break;
            position += matches;
        }

        // Returning a silently partial list would make un-listed footage look
        // like it doesn't exist; fail loudly instead.
        if (hasMore)
            throw new NvrException(
                $"Recording search hit the pagination safety cap at {results.Count} segments " +
                "with more results pending; narrow the time window and retry.");

        return results;
    }

    public Uri GetLiveUri(int channel, StreamType stream = StreamType.Main,
        bool includeCredentials = false)
    {
        return new Uri(
            $"rtsp://{CredentialPrefix(includeCredentials)}{Connection.Host}:{Connection.RtspPort}" +
            $"/Streaming/Channels/{TrackId(channel, stream)}");
    }

    public Uri GetPlaybackUri(int channel, DateTime start, DateTime end,
        StreamType stream = StreamType.Main, bool includeCredentials = false)
    {
        return new Uri(
            $"rtsp://{CredentialPrefix(includeCredentials)}{Connection.Host}:{Connection.RtspPort}" +
            $"/Streaming/tracks/{TrackId(channel, stream)}" +
            $"?starttime={FormatRtspTime(start)}&endtime={FormatRtspTime(end)}");
    }

    public Task DownloadAsync(int channel, DateTime start, DateTime end, string destinationPath,
        IProgress<long>? bytesProgress = null, CancellationToken ct = default)
    {
        // A playbackURI we build ourselves; the NVR clips it to what actually exists.
        // NOTE: time-only download URIs (no name=/size=) are a firmware-dependent
        // extension, not spec-guaranteed — DownloadSegmentAsync prefers the real
        // opaque search URI; this path is only for arbitrary ranges. Live-test item.
        string playbackUri =
            $"rtsp://{Connection.Host}:{Connection.RtspPort}" +
            $"/Streaming/tracks/{TrackId(channel, StreamType.Main)}" +
            $"?starttime={FormatRtspTime(start)}&endtime={FormatRtspTime(end)}";
        return DownloadByPlaybackUriAsync(playbackUri, destinationPath, bytesProgress, ct);
    }

    public Task DownloadSegmentAsync(RecordingSegment segment, string destinationPath,
        IProgress<long>? bytesProgress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(segment.NativeId))
            return DownloadAsync(segment.Channel, segment.Start, segment.End,
                destinationPath, bytesProgress, ct);
        return DownloadByPlaybackUriAsync(segment.NativeId, destinationPath, bytesProgress, ct);
    }

    private async Task DownloadByPlaybackUriAsync(string playbackUri, string destinationPath,
        IProgress<long>? bytesProgress, CancellationToken ct)
    {
        string body =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            $"<downloadRequest><playbackURI>{SecurityElement.Escape(playbackUri)}</playbackURI></downloadRequest>";

        // Firmware quirk: classic ISAPI wants GET with an XML body; some newer builds
        // only accept POST. Try GET first (matches Hikvision's own tooling), then POST.
        var errors = new List<string>();
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            using var request = new HttpRequestMessage(method, "/ISAPI/ContentMgmt/download")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
            };

            using var resp = await _downloadHttp.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            string mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!resp.IsSuccessStatusCode || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                // XML back means an ISAPI error payload, not video.
                string errText = await resp.Content.ReadAsStringAsync(ct);
                errors.Add($"{method} → {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(errText, 300)}");
                continue;
            }

            await using var source = await resp.Content.ReadAsStreamAsync(ct);
            long total = await AtomicDownload.WriteAsync(source, destinationPath, bytesProgress, ct);
            if (total == 0)
            {
                // Firmware that ignores GET-with-body can answer 200 with an empty
                // stream; treat it as a per-method failure so POST still gets tried.
                errors.Add($"{method} → {(int)resp.StatusCode} but empty stream");
                continue;
            }
            return;
        }

        throw new NvrException(
            "ISAPI download failed via both GET and POST (an empty stream usually means " +
            "no footage in that range):\n  " + string.Join("\n  ", errors));
    }

    public async Task<IReadOnlyList<NvrUser>> GetUsersAsync(CancellationToken ct = default)
    {
        var doc = await GetXmlAsync("/ISAPI/Security/users", ct);
        var users = new List<NvrUser>();
        if (doc.Root is null)
            return users;

        foreach (var user in ElementsNamed(doc.Root, "User"))
        {
            string? name = Child(user, "userName");
            if (string.IsNullOrEmpty(name))
                continue;

            string level = Child(user, "userLevel") ?? "";
            bool reserved =
                string.Equals(name, "admin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Descendant(user, "inherent"), "true", StringComparison.OrdinalIgnoreCase);

            // ISAPI has no memo field, and passwords are never returned.
            users.Add(new NvrUser(Child(user, "id") ?? "", name, MapUserRole(level), level, reserved));
        }

        return users;
    }

    // ----- helpers -----

    private static int TrackId(int channel, StreamType stream) => channel * 100 + (int)stream + 1;

    private string CredentialPrefix(bool include) => include
        ? $"{Uri.EscapeDataString(Connection.Username)}:{Uri.EscapeDataString(Connection.Password)}@"
        : "";

    internal static string FormatIsapiTime(DateTime t) =>
        t.ToString(IsapiTimeFormat, CultureInfo.InvariantCulture);

    internal static string FormatRtspTime(DateTime t) =>
        t.ToString(RtspTimeFormat, CultureInfo.InvariantCulture);

    internal static DateTime ParseIsapiTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return default;
        if (DateTime.TryParseExact(value, IsapiTimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exact))
            return DateTime.SpecifyKind(exact, DateTimeKind.Unspecified);
        // Some firmware returns offsets like 2026-07-21T08:00:00+02:00 — keep the wall clock.
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dto))
            return DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified);
        throw new NvrException($"Unrecognized ISAPI time value: '{value}'");
    }

    private static RecordingType MapRecordType(string? metadataDescriptor)
    {
        if (string.IsNullOrEmpty(metadataDescriptor))
            return RecordingType.Unknown;
        string tail = metadataDescriptor[(metadataDescriptor.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return tail switch
        {
            "timing" => RecordingType.Continuous,
            "motion" => RecordingType.Motion,
            "alarm" => RecordingType.Alarm,
            "manual" => RecordingType.Manual,
            "allevent" => RecordingType.Event,
            _ => RecordingType.Unknown,
        };
    }

    private static UserRole MapUserRole(string userLevel) => userLevel.Trim().ToLowerInvariant() switch
    {
        "administrator" => UserRole.Admin,
        "operator" => UserRole.Operator,
        // Some firmware localizes the third tier as "User" or "Guest".
        "viewer" or "user" or "guest" => UserRole.Viewer,
        _ => UserRole.Custom,
    };

    private async Task<XDocument> GetXmlAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new NvrException($"GET {path} → {(int)resp.StatusCode} {resp.ReasonPhrase}",
                text, (int)resp.StatusCode);
        try
        {
            return XDocument.Parse(text);
        }
        catch (System.Xml.XmlException)
        {
            throw new NvrException(
                $"GET {path} returned a non-XML response (web login page? wrong port?)", text);
        }
    }

    /// <summary>
    /// Probe an optional endpoint. "Not supported on this model" statuses map to null;
    /// 401 always propagates (each silent retry burns attempts toward the NVR's
    /// illegal-login lockout); other failures are recorded in <paramref name="failures"/>
    /// (or swallowed when it is null) so the caller can decide whether an empty
    /// aggregate result is trustworthy.
    /// </summary>
    private async Task<XDocument?> TryGetXmlAsync(string path, CancellationToken ct,
        List<Exception>? failures)
    {
        try
        {
            return await GetXmlAsync(path, ct);
        }
        catch (NvrException ex) when (ex.StatusCode is 400 or 403 or 404 or 405 or 501)
        {
            return null; // endpoint not supported by this device
        }
        catch (NvrException ex) when (ex.StatusCode is not 401)
        {
            failures?.Add(ex);
            return null;
        }
        catch (HttpRequestException ex)
        {
            failures?.Add(ex);
            return null;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            failures?.Add(ex); // per-request timeout, not a user cancel
            return null;
        }
    }

    private static IEnumerable<XElement> ElementsNamed(XElement root, string localName) =>
        root.Descendants().Where(e => e.Name.LocalName == localName);

    private static string? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static string? Descendant(XElement? root, string localName) =>
        root?.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        _http.Dispose();
        if (_ownsDownloadHttp)
            _downloadHttp.Dispose();
    }
}
