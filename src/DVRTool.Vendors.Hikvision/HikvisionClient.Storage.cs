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

    public async Task<DateTime?> FindOldestRecordingAsync(int channel,
        CancellationToken ct = default)
    {
        int trackId = TrackId(channel, StreamType.Main);
        string searchId = Guid.NewGuid().ToString("D").ToUpperInvariant();

        // Matches come back oldest-first, so one result over an everything window is the
        // earliest recording still on disk. "searchResultPostion" (sic) is Hikvision's own
        // spelling — required verbatim.
        string body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <CMSearchDescription>
              <searchID>{searchId}</searchID>
              <trackIDList><trackID>{trackId}</trackID></trackIDList>
              <timeSpanList>
                <timeSpan>
                  <startTime>2000-01-01T00:00:00Z</startTime>
                  <endTime>2038-01-01T00:00:00Z</endTime>
                </timeSpan>
              </timeSpanList>
              <maxResults>1</maxResults>
              <searchResultPostion>0</searchResultPostion>
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

        var first = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "searchMatchItem");
        var timeSpan = first?.Descendants().FirstOrDefault(e => e.Name.LocalName == "timeSpan");
        string? start = timeSpan is null ? null : Child(timeSpan, "startTime");
        return string.IsNullOrWhiteSpace(start) ? null : ParseIsapiTime(start);
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
