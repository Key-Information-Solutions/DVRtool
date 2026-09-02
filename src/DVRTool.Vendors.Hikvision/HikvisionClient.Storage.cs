using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DVRTool.Core;

namespace DVRTool.Vendors.Hikvision;

/// <summary>
/// The <see cref="IStorageClient"/> face of the ISAPI client: disk inventory, oldest
/// recording still on disk, and recording-bitrate reads/writes.
/// </summary>
/// <remarks>
/// See <c>docs/hikvision-storage.md</c> before changing endpoints or units here — notably:
/// disk capacity/free space come back in decimal megabytes and free space is permanently 0
/// on a recorder in overwrite mode; the storage capabilities' hddList size is the firmware
/// ceiling, not the chassis bay count; and recording search returns matches oldest-first,
/// which is what makes the single-result oldest-recording probe cheap.
/// </remarks>
public sealed partial class HikvisionClient : IStorageClient
{
    public async Task<StorageInfo> GetStorageInfoAsync(CancellationToken ct = default)
    {
        var doc = await GetXmlAsync("/ISAPI/ContentMgmt/Storage", ct);
        var hdds = new List<HddInfo>();
        if (doc.Root is not null)
        {
            foreach (var hdd in ElementsNamed(doc.Root, "hdd"))
            {
                if (!int.TryParse(Child(hdd, "id"), out int id))
                    continue;
                hdds.Add(new HddInfo(
                    id,
                    Child(hdd, "hddName") ?? $"hdd{id}",
                    Child(hdd, "hddType") ?? "",
                    Child(hdd, "status") ?? "",
                    Child(hdd, "property") ?? "",
                    ParseLong(Child(hdd, "capacity")),
                    ParseLong(Child(hdd, "freeSpace")),
                    Child(hdd, "hddSerialNumber") ?? "",
                    Child(hdd, "hddModel") ?? ""));
            }
        }
        string? workMode = Descendant(doc.Root, "workMode");

        // Optional enrichment: what the firmware says it could hold. Model-dependent, and
        // a failure here must not take down the inventory that already parsed.
        int? maxSupported = null;
        var caps = await TryGetXmlAsync("/ISAPI/ContentMgmt/Storage/capabilities", ct, null);
        var capsHddList = caps?.Root?.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "hddList");
        if (capsHddList?.Attribute("size")?.Value is { } size &&
            int.TryParse(size, out int n) && n > 0)
            maxSupported = n;

        return new StorageInfo(hdds.OrderBy(h => h.Id).ToList(), workMode, maxSupported);
    }

    public async Task<IReadOnlyList<CameraStream>> GetMainStreamsAsync(
        CancellationToken ct = default)
    {
        var doc = await GetXmlAsync("/ISAPI/Streaming/channels", ct);
        var streams = new List<CameraStream>();
        if (doc.Root is null)
            return streams;

        foreach (var sc in ElementsNamed(doc.Root, "StreamingChannel"))
        {
            if (!int.TryParse(Child(sc, "id"), out int trackId))
                continue;
            // Recordings are the main stream (x01); sub/third streams would flatter
            // every retention estimate.
            if (trackId % 100 != 1)
                continue;

            var video = sc.Elements().FirstOrDefault(e => e.Name.LocalName == "Video");
            bool channelEnabled = !string.Equals(Child(sc, "enabled"), "false",
                StringComparison.OrdinalIgnoreCase);
            bool videoEnabled = video is null || !string.Equals(Child(video, "enabled"), "false",
                StringComparison.OrdinalIgnoreCase);

            // ISAPI encodes frame rate as fps*100 (2000 = 20.0 fps).
            double? fps = null;
            if (video is not null && int.TryParse(Child(video, "maxFrameRate"), out int rawFps) &&
                rawFps > 0)
                fps = rawFps / 100.0;

            streams.Add(new CameraStream(
                Channel: trackId / 100,
                TrackId: trackId,
                Enabled: channelEnabled && videoEnabled,
                CodecType: video is null ? "" : Child(video, "videoCodecType") ?? "",
                Width: ParseInt(video is null ? null : Child(video, "videoResolutionWidth")),
                Height: ParseInt(video is null ? null : Child(video, "videoResolutionHeight")),
                FrameRateFps: fps,
                QualityControlType: video is null
                    ? ""
                    : Child(video, "videoQualityControlType") ?? "",
                VbrUpperCapKbps: TryParseInt(video, "vbrUpperCap"),
                ConstantBitrateKbps: TryParseInt(video, "constantBitRate"),
                FixedQuality: TryParseInt(video, "fixedQuality")));
        }

        return streams.OrderBy(s => s.Channel).ToList();
    }

    /// <summary>Test seam: "now" for the calendar fallback's month walk.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    // Calendar fallback bounds: how many months back the walk may go in total, how many
    // empty months after the earliest footage found are read before concluding the disks
    // hold nothing older (offline spells and motion-only cameras leave gaps), and how many
    // empty months from today mean the camera has no recordings at all.
    private const int CalendarMaxMonths = 120;
    private const int CalendarGapMonths = 6;
    private const int CalendarEmptyMonthsBeforeGivingUp = 36;

    public async Task<DateTime?> FindOldestRecordingAsync(int channel,
        CancellationToken ct = default)
    {
        int trackId = TrackId(channel, StreamType.Main);

        // Matches come back oldest-first, so one result over an everything window is the
        // earliest recording still on disk — one cheap POST on most firmware.
        try
        {
            return await SearchFirstMatchAsync(trackId,
                "2000-01-01T00:00:00Z", "2038-01-01T00:00:00Z", ct);
        }
        catch (NvrException ex) when (ex.StatusCode is int code && code != 401)
        {
            // Some firmware cannot do the everything window: DS-7716NI-I4/16P V4.61.030
            // (Site E) answers it 500 "Tag 13 is invalid (two root tags)" every time, and
            // any window wide enough to make it enumerate thousands of segments fails about
            // half the time. Its own playback-calendar endpoint is fast and deterministic, so
            // walk that to the earliest recorded day and search just that day for the exact
            // start. 401 is left alone: retries burn attempts toward the login lockout.
            try
            {
                return await FindOldestByCalendarAsync(trackId, ct);
            }
            catch (NvrException fallback)
            {
                throw new NvrException(
                    $"{ex.Message}; the calendar fallback also failed: {fallback.Message}",
                    fallback.ResponseBody ?? ex.ResponseBody,
                    fallback.StatusCode ?? ex.StatusCode, ex);
            }
        }
    }

    /// <summary>
    /// One recording search returning the earliest match's start, or null when the window
    /// holds no recordings. Times are ISAPI strings (device wall clock dressed as UTC).
    /// </summary>
    private async Task<DateTime?> SearchFirstMatchAsync(int trackId, string startTime,
        string endTime, CancellationToken ct)
    {
        string searchId = Guid.NewGuid().ToString("D").ToUpperInvariant();

        // "searchResultPostion" (sic) is Hikvision's own spelling — required verbatim.
        string body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <CMSearchDescription>
              <searchID>{searchId}</searchID>
              <trackIDList><trackID>{trackId}</trackID></trackIDList>
              <timeSpanList>
                <timeSpan>
                  <startTime>{startTime}</startTime>
                  <endTime>{endTime}</endTime>
                </timeSpan>
              </timeSpanList>
              <maxResults>1</maxResults>
              <searchResultPostion>0</searchResultPostion>
              <metadataList>
                <metadataDescriptor>//recordType.meta.std-cgi.com</metadataDescriptor>
              </metadataList>
            </CMSearchDescription>
            """;

        var doc = await PostXmlAsync("/ISAPI/ContentMgmt/search", body, "ISAPI search failed", ct);
        var first = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "searchMatchItem");
        var timeSpan = first?.Descendants().FirstOrDefault(e => e.Name.LocalName == "timeSpan");
        string? start = timeSpan is null ? null : Child(timeSpan, "startTime");
        return string.IsNullOrWhiteSpace(start) ? null : ParseIsapiTime(start);
    }

    /// <summary>
    /// Oldest recording via the playback calendar: walk months backwards from today reading
    /// which days hold footage, stop after <see cref="CalendarGapMonths"/> empty months past
    /// the earliest found (or <see cref="CalendarEmptyMonthsBeforeGivingUp"/> with nothing
    /// found), then search that one day for the exact first segment. Falls back to the day's
    /// midnight if the calendar says the day recorded but the search finds no segment.
    /// </summary>
    private async Task<DateTime?> FindOldestByCalendarAsync(int trackId, CancellationToken ct)
    {
        var now = Clock();
        var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        DateTime? earliestDay = null;
        int emptyRun = 0;
        for (int i = 0; i < CalendarMaxMonths; i++)
        {
            ct.ThrowIfCancellationRequested();
            var days = await GetRecordedDaysAsync(trackId, month.Year, month.Month, ct);
            if (days.Count > 0)
            {
                earliestDay = month.AddDays(days.Min() - 1);
                emptyRun = 0;
            }
            else if (++emptyRun >= (earliestDay is null
                         ? CalendarEmptyMonthsBeforeGivingUp
                         : CalendarGapMonths))
            {
                break;
            }
            month = month.AddMonths(-1);
        }
        if (earliestDay is not DateTime day)
            return null;

        // A single day's worth of segments is cheap even on the firmware that choked on
        // the wide window; one retry covers its intermittent 500s.
        string start = FormatIsapiTime(day);
        string end = FormatIsapiTime(day.AddDays(1));
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await SearchFirstMatchAsync(trackId, start, end, ct) ?? day;
            }
            catch (NvrException ex) when (attempt == 0 && ex.StatusCode is >= 500)
            {
            }
        }
    }

    /// <summary>
    /// Days of one month that hold footage for a track — the web UI's playback calendar
    /// (<c>POST /ISAPI/ContentMgmt/record/tracks/{track}/dailyDistribution</c>).
    /// </summary>
    private async Task<IReadOnlyList<int>> GetRecordedDaysAsync(int trackId, int year,
        int month, CancellationToken ct)
    {
        string path = $"/ISAPI/ContentMgmt/record/tracks/{trackId}/dailyDistribution";
        string body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <trackDailyParam>
              <year>{year}</year>
              <monthOfYear>{month}</monthOfYear>
            </trackDailyParam>
            """;
        var doc = await PostXmlAsync(path, body, $"POST {path} failed", ct);
        var days = new List<int>();
        foreach (var day in doc.Descendants().Where(e => e.Name.LocalName == "day"))
        {
            if (!string.Equals(Child(day, "record"), "true", StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(Child(day, "dayOfMonth"), out int d) && d >= 1 &&
                d <= DateTime.DaysInMonth(year, month))
                days.Add(d);
        }
        return days;
    }

    private async Task<XDocument> PostXmlAsync(string path, string body, string failurePrefix,
        CancellationToken ct)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/xml");
        using var resp = await _http.PostAsync(path, content, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new NvrException(
                $"{failurePrefix}: {(int)resp.StatusCode} {resp.ReasonPhrase}",
                text, (int)resp.StatusCode);
        try
        {
            return XDocument.Parse(text);
        }
        catch (System.Xml.XmlException)
        {
            throw new NvrException(
                $"{failurePrefix}: non-XML response (web login page? wrong port?)", text);
        }
    }

    public async Task<BitrateRange?> GetBitrateRangeAsync(int channel,
        CancellationToken ct = default)
    {
        int trackId = TrackId(channel, StreamType.Main);
        var doc = await TryGetXmlAsync($"/ISAPI/Streaming/channels/{trackId}/capabilities",
            ct, null);
        if (doc?.Root is null)
            return null;

        // The bounds ride as min/max attributes on the bitrate element itself
        // (vbrUpperCap on VBR-capable firmware; constantBitRate is the CBR twin).
        foreach (string name in new[] { "vbrUpperCap", "constantBitRate" })
        {
            var el = doc.Root.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
            if (el?.Attribute("min")?.Value is { } minText &&
                el.Attribute("max")?.Value is { } maxText &&
                int.TryParse(minText, out int min) && int.TryParse(maxText, out int max) &&
                min > 0 && max >= min)
                return new BitrateRange(min, max);
        }
        return null;
    }

    public async Task<int> SetMaxBitrateAsync(int channel, int kbps,
        CancellationToken ct = default)
    {
        if (kbps <= 0)
            throw new ArgumentOutOfRangeException(nameof(kbps));

        int trackId = TrackId(channel, StreamType.Main);
        string path = $"/ISAPI/Streaming/channels/{trackId}";

        // Read-modify-write of the channel's own document: ISAPI PUT wants the full
        // StreamingChannel back, and round-tripping what the device sent keeps every
        // field (and the document's namespace) exactly as the firmware expects it.
        var doc = await GetXmlAsync(path, ct);
        var video = doc.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Video")
            ?? throw new NvrException($"channel {channel}: GET {path} returned no <Video> element");

        // Both fields where present: vbrUpperCap governs VBR recording, constantBitRate
        // governs CBR — setting both keeps the channel consistent whichever mode it is in.
        bool wroteAny = false;
        foreach (string name in new[] { "vbrUpperCap", "constantBitRate" })
        {
            var el = video.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            if (el is null)
                continue;
            el.Value = kbps.ToString(CultureInfo.InvariantCulture);
            wroteAny = true;
        }
        if (!wroteAny)
            throw new NvrException(
                $"channel {channel}: no writable bitrate field (neither vbrUpperCap nor " +
                "constantBitRate) in its streaming config");

        using var content = new StringContent(doc.ToString(SaveOptions.DisableFormatting),
            Encoding.UTF8, "application/xml");
        using var resp = await _http.PutAsync(path, content, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new NvrException(
                $"PUT {path} → {(int)resp.StatusCode} {resp.ReasonPhrase}",
                text, (int)resp.StatusCode);

        // ISAPI answers a ResponseStatus document; statusCode 1 is OK. Anything else is a
        // rejection even under HTTP 200.
        if (TryParseStatusCode(text) is { } status && status != 1)
            throw new NvrException(
                $"channel {channel}: the device rejected the bitrate write (statusCode " +
                $"{status})", text);

        // Read back what actually stuck: NVR-managed cameras may snap the value to their
        // own steps, and the caller should report the real number, not the requested one.
        var verify = await GetXmlAsync(path, ct);
        var verifyVideo = verify.Root?.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Video");
        bool isVbr = string.Equals(
            verifyVideo is null ? null : Child(verifyVideo, "videoQualityControlType"),
            "VBR", StringComparison.OrdinalIgnoreCase);
        int? readBack = TryParseInt(verifyVideo, isVbr ? "vbrUpperCap" : "constantBitRate")
            ?? TryParseInt(verifyVideo, "vbrUpperCap");
        return readBack
            ?? throw new NvrException(
                $"channel {channel}: the write was accepted but the read-back shows no " +
                "bitrate field — treat the channel's setting as unknown");
    }

    private static int? TryParseStatusCode(string responseBody)
    {
        try
        {
            var doc = XDocument.Parse(responseBody);
            string? code = doc.Root?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "statusCode")?.Value;
            return int.TryParse(code, out int status) ? status : null;
        }
        catch (System.Xml.XmlException)
        {
            return null; // some firmware answers 200 with an empty body; HTTP status decides
        }
    }

    private static long ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : 0;

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private static int? TryParseInt(XElement? parent, string localName)
    {
        if (parent is null)
            return null;
        string? text = parent.Elements()
            .FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int v)
            ? v
            : null;
    }
}
