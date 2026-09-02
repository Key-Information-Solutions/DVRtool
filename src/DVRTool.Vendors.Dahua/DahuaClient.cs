using System.Globalization;
using DVRTool.Core;

namespace DVRTool.Vendors.Dahua;

/// <summary>
/// Dahua CGI client (also covers Amcrest and most Dahua OEMs). Uses the HTTP CGI API
/// (digest auth) for device info, channel names, recording search (mediaFileFind) and
/// time-range download (loadfile), plus RTSP for live view and playback-by-time.
///
/// Channel numbering: the public API uses 1-based display channels. Dahua's CGI is
/// inconsistent: config tables are 0-based; loadfile.cgi and RTSP URLs are 1-based.
/// mediaFileFind's condition.Channel is 1-based, as the official doc says ("start from
/// 1"), and the response's items[i].Channel is 0-based ("input − 1") — both settled live
/// on 2026-09-02 against a DH-NVR608H-128-4KS3/I (4.000.0000000.6.R): Channel=0 is
/// rejected with 400 Bad Request, Channel=1 answers display channel 1 with items whose
/// Channel=0. Field clients that send display−1 (python-amcrest) search one channel low
/// on this firmware. So: send the display number, map result+1.
///
/// Downloads arrive as .dav (DHAV container); remux with <see cref="Remux"/>.
/// </summary>
public sealed partial class DahuaClient : INvrClient, IUserManagementClient
{
    private const string CgiTimeFormat = "yyyy-MM-dd HH:mm:ss";
    private const string RtspTimeFormat = "yyyy_MM_dd_HH_mm_ss";

    private readonly HttpClient _http;
    private readonly HttpClient _downloadHttp;
    private readonly bool _ownsDownloadHttp;

    public Vendor Vendor => Vendor.Dahua;
    public NvrConnection Connection { get; }

    public DahuaClient(NvrConnection connection)
    {
        Connection = connection;
        _http = NvrHttp.Create(connection);
        _downloadHttp = NvrHttp.Create(connection, Timeout.InfiniteTimeSpan);
        _ownsDownloadHttp = true;
    }

    /// <summary>Test seam: routes all traffic through the supplied handler.</summary>
    public DahuaClient(NvrConnection connection, HttpMessageHandler handler)
    {
        Connection = connection;
        _http = NvrHttp.Create(connection, handler: handler);
        _downloadHttp = _http;
        _ownsDownloadHttp = false;
    }

    public async Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        var system = ParseKeyValues(await GetTextAsync(
            "/cgi-bin/magicBox.cgi?action=getSystemInfo", ct));
        string name = "";
        string firmware = "";
        try
        {
            name = ParseKeyValues(await GetTextAsync(
                "/cgi-bin/magicBox.cgi?action=getMachineName", ct)).GetValueOrDefault("name", "");
            firmware = ParseKeyValues(await GetTextAsync(
                "/cgi-bin/magicBox.cgi?action=getSoftwareVersion", ct)).GetValueOrDefault("version", "");
        }
        catch (NvrException)
        {
            // Optional endpoints; keep whatever getSystemInfo gave us.
        }

        return new DeviceInfo(
            name,
            ModelFrom(system),
            system.GetValueOrDefault("serialNumber", ""),
            firmware);
    }

    /// <summary>
    /// The market model. Cameras put it in <c>deviceType</c>; NVRs answer a bare number
    /// there (<c>deviceType=31</c> on a DH-NVR608H) and carry the model string in
    /// <c>updateSerial</c> (<c>DH-NVR608H-128-4KS3/I</c>), so prefer that whenever
    /// <c>deviceType</c> has no letters.
    /// </summary>
    internal static string ModelFrom(Dictionary<string, string> systemInfo)
    {
        string deviceType = systemInfo.GetValueOrDefault("deviceType", "");
        string updateSerial = systemInfo.GetValueOrDefault("updateSerial", "");
        bool typeIsNumeric = deviceType.Length > 0 && deviceType.All(char.IsDigit);
        return typeIsNumeric && updateSerial.Length > 0 ? updateSerial : deviceType;
    }

    public async Task<IReadOnlyList<Channel>> GetChannelsAsync(CancellationToken ct = default)
    {
        // table.ChannelTitle[0].Name=Front Door  → display channel 1
        var kv = ParseKeyValues(await GetTextAsync(
            "/cgi-bin/configManager.cgi?action=getConfig&name=ChannelTitle", ct));
        var channels = new List<Channel>();
        foreach (var (key, value) in kv)
        {
            if (!key.StartsWith("table.ChannelTitle[", StringComparison.Ordinal) ||
                !key.EndsWith("].Name", StringComparison.Ordinal))
                continue;
            int open = key.IndexOf('[') + 1;
            int close = key.IndexOf(']', open);
            if (int.TryParse(key[open..close], out int index))
                channels.Add(new Channel(index + 1, value));
        }
        return channels.OrderBy(c => c.Id).ToList();
    }

    public async Task<IReadOnlyList<RecordingSegment>> SearchAsync(
        int channel, DateTime start, DateTime end, CancellationToken ct = default)
    {
        // mediaFileFind is a stateful finder object: create → findFile → findNextFile* → close → destroy.
        string createText = await GetTextAsync("/cgi-bin/mediaFileFind.cgi?action=factory.create", ct);
        string finder = ParseKeyValues(createText).GetValueOrDefault("result")
            ?? throw new NvrException("mediaFileFind factory.create returned no object id", createText);

        var results = new List<RecordingSegment>();
        try
        {
            // condition.Channel is 1-based (display ch1 = 1); see the class remarks.
            string condition =
                $"/cgi-bin/mediaFileFind.cgi?action=findFile&object={finder}" +
                $"&condition.Channel={channel}" +
                $"&condition.StartTime={Uri.EscapeDataString(FormatCgiTime(start))}" +
                $"&condition.EndTime={Uri.EscapeDataString(FormatCgiTime(end))}" +
                "&condition.Types[0]=dav";

            string findResponse;
            try
            {
                findResponse = await GetTextAsync(condition, ct);
            }
            catch (NvrException ex) when (ex.StatusCode is 400)
            {
                // 400/Error is how the recorder says "nothing to find": a window with no
                // recordings, or a channel with no camera bound (verified live).
                return results;
            }
            if (!findResponse.Contains("OK", StringComparison.OrdinalIgnoreCase))
                return results;

            // The only documented terminal condition is found=0 — a short non-empty
            // page does NOT mean end-of-results (spec: "no more than fileCount").
            const int pageSize = 100;  // documented max for findNextFile count
            const int maxPages = 1000; // guard against firmware that never reports found=0
            int pageNum = 0;
            for (; pageNum < maxPages; pageNum++)
            {
                ct.ThrowIfCancellationRequested();
                string page;
                try
                {
                    page = await GetTextAsync(
                        $"/cgi-bin/mediaFileFind.cgi?action=findNextFile&object={finder}&count={pageSize}", ct);
                }
                catch (NvrException) when (pageNum > 0)
                {
                    // Some firmware answers the call after the last batch with an HTTP
                    // error instead of found=0; keep what we already parsed.
                    break;
                }
                var kv = ParseKeyValues(page);
                int found = int.TryParse(kv.GetValueOrDefault("found"), out int f) ? f : 0;
                if (found == 0)
                    break;

                for (int i = 0; i < found; i++)
                {
                    string P(string field) => kv.GetValueOrDefault($"items[{i}].{field}", "");
                    if (!TryParseCgiTime(P("StartTime"), out var segStart) ||
                        !TryParseCgiTime(P("EndTime"), out var segEnd))
                        continue;

                    int deviceChannel = int.TryParse(P("Channel"), out int c) ? c : channel - 1;
                    results.Add(new RecordingSegment
                    {
                        Channel = deviceChannel + 1,
                        Start = segStart,
                        End = segEnd,
                        NativeId = P("FilePath"),
                        SizeBytes = long.TryParse(P("Length"), out long len) ? len : null,
                        Type = MapRecordType(kv, i),
                    });
                }
            }

            if (pageNum == maxPages)
                throw new NvrException(
                    $"Recording search hit the pagination safety cap at {results.Count} segments " +
                    "with more results pending; narrow the time window and retry.");
        }
        finally
        {
            // Always release the finder object on the device — even (especially)
            // when the search was canceled, so cleanup runs on its own short-lived
            // token, never the caller's (which may already be canceled). Separate
            // try blocks so destroy is attempted even if close fails.
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await GetTextAsync($"/cgi-bin/mediaFileFind.cgi?action=close&object={finder}",
                    cleanupCts.Token);
            }
            catch (Exception)
            {
                // Best effort.
            }
            try
            {
                await GetTextAsync($"/cgi-bin/mediaFileFind.cgi?action=destroy&object={finder}",
                    cleanupCts.Token);
            }
            catch (Exception)
            {
                // Best effort; the device reclaims stale finders eventually.
            }
        }

        return results;
    }

    public Uri GetLiveUri(int channel, StreamType stream = StreamType.Main,
        bool includeCredentials = false)
    {
        return new Uri(
            $"rtsp://{CredentialPrefix(includeCredentials)}{Connection.Host}:{Connection.RtspPort}" +
            $"/cam/realmonitor?channel={channel}&subtype={(int)stream}");
    }

    public Uri GetPlaybackUri(int channel, DateTime start, DateTime end,
        StreamType stream = StreamType.Main, bool includeCredentials = false)
    {
        return new Uri(
            $"rtsp://{CredentialPrefix(includeCredentials)}{Connection.Host}:{Connection.RtspPort}" +
            $"/cam/playback?channel={channel}" +
            $"&starttime={FormatRtspTime(start)}&endtime={FormatRtspTime(end)}");
    }

    public async Task DownloadAsync(int channel, DateTime start, DateTime end,
        string destinationPath, IProgress<long>? bytesProgress = null, CancellationToken ct = default)
    {
        // loadfile.cgi streams a .dav for the requested span straight off the NVR disk.
        // (Preferred over RPC_Loadfile, which returns empty bodies on some firmware.)
        string path =
            $"/cgi-bin/loadfile.cgi?action=startLoad&channel={channel}" +
            $"&startTime={Uri.EscapeDataString(FormatCgiTime(start))}" +
            $"&endTime={Uri.EscapeDataString(FormatCgiTime(end))}" +
            "&subtype=0";

        using var resp = await _downloadHttp.GetAsync(
            path, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string errText = await resp.Content.ReadAsStringAsync(ct);
            throw new NvrException(
                $"loadfile.cgi failed: {(int)resp.StatusCode} {resp.ReasonPhrase}",
                errText, (int)resp.StatusCode);
        }

        await using var source = await resp.Content.ReadAsStreamAsync(ct);
        long total = await AtomicDownload.WriteAsync(source, destinationPath, bytesProgress, ct);
        if (total == 0)
            throw new NvrException("loadfile.cgi returned an empty stream — no footage in that range?");
    }

    public Task DownloadSegmentAsync(RecordingSegment segment, string destinationPath,
        IProgress<long>? bytesProgress = null, CancellationToken ct = default)
    {
        // Time-range download of exactly this segment's span; avoids RPC_Loadfile quirks.
        return DownloadAsync(segment.Channel, segment.Start, segment.End,
            destinationPath, bytesProgress, ct);
    }

    public async Task<IReadOnlyList<NvrUser>> GetUsersAsync(CancellationToken ct = default)
    {
        // users[0].Name=admin / users[0].Group=admin / users[0].Reserved=true / ...
        // Password is always masked ("******"); Sharable and AuthorityList[*] are ignored.
        string text = await GetTextAsync("/cgi-bin/userManager.cgi?action=getUserInfoAll", ct);
        var kv = ParseKeyValues(text);

        var users = new List<NvrUser>();
        foreach (int index in UserIndexesInOrder(text))
        {
            string Field(string name) => kv.GetValueOrDefault($"users[{index}].{name}", "");
            string userName = Field("Name");
            if (userName.Length == 0)
                continue;

            string group = Field("Group");
            string id = Field("ID");
            users.Add(new NvrUser(
                id.Length > 0 ? id : userName,
                userName,
                MapUserRole(group),
                group,
                bool.TryParse(Field("Reserved"), out bool reserved) && reserved,
                kv.GetValueOrDefault($"users[{index}].Memo")));
        }
        return users;
    }

    // ----- helpers -----

    private string CredentialPrefix(bool include) => include
        ? $"{Uri.EscapeDataString(Connection.Username)}:{Uri.EscapeDataString(Connection.Password)}@"
        : "";

    internal static string FormatCgiTime(DateTime t) =>
        t.ToString(CgiTimeFormat, CultureInfo.InvariantCulture);

    internal static string FormatRtspTime(DateTime t) =>
        t.ToString(RtspTimeFormat, CultureInfo.InvariantCulture);

    // Inbound parsing is lenient: the spec's own findNextFile examples use
    // non-zero-padded timestamps ("2011-1-1 12:00:00"); single-letter specifiers
    // accept both padded and non-padded digits. Outbound stays padded (universal).
    private static readonly string[] CgiParseFormats =
    [
        CgiTimeFormat,
        "yyyy-M-d H:m:s",
    ];

    internal static bool TryParseCgiTime(string? value, out DateTime result)
    {
        bool ok = DateTime.TryParseExact(value, CgiParseFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out result);
        result = DateTime.SpecifyKind(result, DateTimeKind.Unspecified);
        return ok;
    }

    private static RecordingType MapRecordType(Dictionary<string, string> kv, int index)
    {
        // Two domains per the spec: Events = {AlarmLocal, VideoMotion, ...} names the
        // trigger; Flags = {Timing, Manual, Marker, Event, ...} names the record mode.
        // Manual recordings arrive with NO Events entry and Flags[0]=Manual.
        string ev = kv.GetValueOrDefault($"items[{index}].Events[0]", "");
        switch (ev)
        {
            case "VideoMotion" or "SmartMotionHuman" or "SmartMotionVehicle":
                return RecordingType.Motion;
            case "AlarmLocal" or "Alarm":
                return RecordingType.Alarm;
            case "":
                break; // no triggering event — classify by Flags
            default:
                return RecordingType.Event;
        }
        return kv.GetValueOrDefault($"items[{index}].Flags[0]", "") switch
        {
            "Manual" => RecordingType.Manual,
            "Timing" => RecordingType.Continuous,
            "Event" => RecordingType.Event,
            "" => RecordingType.Unknown,
            _ => RecordingType.Continuous, // Marker / Mosaic / Cutout
        };
    }

    private const string UserKeyPrefix = "users[";

    // The parsed map is unordered and getUserInfoAll may skip indices after a
    // deletion, so take the index sequence off the raw body: accounts come back
    // in the order the device lists them in its own UI.
    private static IEnumerable<int> UserIndexesInOrder(string text)
    {
        var seen = new HashSet<int>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(UserKeyPrefix, StringComparison.Ordinal))
                continue;
            int close = line.IndexOf(']', UserKeyPrefix.Length);
            if (close < 0 || !int.TryParse(line[UserKeyPrefix.Length..close], out int index))
                continue;
            if (seen.Add(index))
                yield return index;
        }
    }

    private static UserRole MapUserRole(string group) => group.ToLowerInvariant() switch
    {
        "admin" => UserRole.Admin,
        "user" => UserRole.Operator,
        _ => UserRole.Custom,
    };

    /// <summary>Parses Dahua's "key=value" line format into a dictionary.</summary>
    internal static Dictionary<string, string> ParseKeyValues(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            dict[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return dict;
    }

    private async Task<string> GetTextAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new NvrException($"GET {path} → {(int)resp.StatusCode} {resp.ReasonPhrase}",
                text, (int)resp.StatusCode);
        return text;
    }

    public void Dispose()
    {
        _http.Dispose();
        if (_ownsDownloadHttp)
            _downloadHttp.Dispose();
    }
}
