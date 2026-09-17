using System.Globalization;
using System.Xml.Linq;
using DVRTool.Core;

namespace DVRTool.Vendors.Hikvision;

/// <summary>
/// The <see cref="ICameraSettingsWriter"/> face of the Hikvision client: the encoder settings of
/// the cameras on the recorder, as opposed to the recorder's own configuration
/// (<see cref="IDeviceConfigWriter"/>) or what those cameras will write to disk
/// (<see cref="IStorageClient"/>).
/// </summary>
/// <remarks>
/// <para>
/// The document is <c>/ISAPI/Streaming/channels/{track}</c> — the same one the retention
/// bitrate write has round-tripped live since 2026-09-02. Everything here follows from three
/// facts about it:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>It self-describes its legal values.</b> The <c>/capabilities</c> sibling carries
/// <c>opt=</c> lists on resolution, frame rate and quality-control type, and <c>min</c>/
/// <c>max</c> on the bitrate elements. So both front ends offer what the camera declares and
/// nothing is guessed — which matters because a camera accepts a resolution it does not support
/// and silently ignores it. Only the read-back would ever catch that.
/// </description></item>
/// <item><description>
/// <b><c>maxFrameRate</c> 0 is a choice, not a blank.</b> It is the web UI's "Full Frame Rate",
/// and the firmware re-resolves it per resolution. Writing back the number it currently resolves
/// to would quietly pin a camera that was set to follow its own maximum, so "full" is carried as
/// its own request field and written as the literal 0.
/// </description></item>
/// <item><description>
/// <b>A channel's name is not in this document.</b> It lives in the channel list — InputProxy
/// for an IP camera behind an NVR, System/Video/inputs for an analog input on a DVR — so a
/// rename has to find out which, and aiming at the wrong one is accepted and does nothing.
/// </description></item>
/// </list>
/// <para>
/// Writes reuse the enforced read-modify-write helpers on the config partial
/// (<c>ReReadForWriteAsync</c>, <c>PutXmlAsync</c>, <c>SetChild</c>): refuse without a prior
/// read, refuse when the document moved underneath, replace only the requested fields, PUT the
/// device's own document whole, then read back and report what the recorder kept.
/// </para>
/// </remarks>
public sealed partial class HikvisionClient : ICameraSettingsWriter
{
    private static string StreamingChannelPath(int trackId) =>
        $"/ISAPI/Streaming/channels/{trackId}";

    public async Task<IReadOnlyList<ChannelEncoding>> GetEncodingAsync(
        CancellationToken ct = default)
    {
        var doc = await GetXmlAsync("/ISAPI/Streaming/channels", ct);
        var tracks = new List<ChannelEncoding>();
        if (doc.Root is null)
            return tracks;

        foreach (var sc in ElementsNamed(doc.Root, "StreamingChannel"))
        {
            if (!int.TryParse(Child(sc, "id"), out int trackId))
                continue;
            // Every track, unlike the storage read: a sub stream is a setting to edit here,
            // not a bitrate to add to a retention total.
            if (ParseStreamType(trackId) is not { } stream)
                continue;

            tracks.Add(await ParseTrackAsync(sc, trackId, stream, ct));
        }

        return tracks.OrderBy(t => t.Channel).ThenBy(t => (int)t.Stream).ToList();
    }

    private async Task<ChannelEncoding> ParseTrackAsync(XElement sc, int trackId,
        StreamType stream, CancellationToken ct)
    {
        var video = sc.Elements().FirstOrDefault(e => e.Name.LocalName == "Video");
        var audio = sc.Elements().FirstOrDefault(e => e.Name.LocalName == "Audio");

        bool channelEnabled = !string.Equals(Child(sc, "enabled"), "false",
            StringComparison.OrdinalIgnoreCase);
        bool videoEnabled = video is null || !string.Equals(Child(video, "enabled"), "false",
            StringComparison.OrdinalIgnoreCase);

        // ISAPI encodes frame rate as fps*100 (2000 = 20.0 fps). 0 is "Full Frame Rate" —
        // a real choice — and the channel's capabilities say what it resolves to here.
        double? fps = null;
        bool fullRate = false;
        if (video is not null && int.TryParse(Child(video, "maxFrameRate"), out int rawFps))
        {
            if (rawFps > 0)
                fps = rawFps / 100.0;
            else if (rawFps == 0)
            {
                fullRate = true;
                fps = FullFrameRateFps(await TryGetXmlAsync(
                    $"{StreamingChannelPath(trackId)}/capabilities", ct, null));
            }
        }

        bool? audioEnabled = audio is null
            ? null
            : Child(audio, "enabled") is { } flag
                ? string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)
                : null;

        return new ChannelEncoding(
            Channel: trackId / 100,
            TrackId: trackId,
            Stream: stream,
            Enabled: channelEnabled && videoEnabled,
            CodecType: video is null ? "" : Child(video, "videoCodecType") ?? "",
            Width: ParseInt(video is null ? null : Child(video, "videoResolutionWidth")),
            Height: ParseInt(video is null ? null : Child(video, "videoResolutionHeight")),
            FrameRateFps: fps,
            QualityControlType: video is null ? "" : Child(video, "videoQualityControlType") ?? "",
            VbrUpperCapKbps: TryParseInt(video, "vbrUpperCap"),
            ConstantBitrateKbps: TryParseInt(video, "constantBitRate"),
            FixedQuality: TryParseInt(video, "fixedQuality"),
            FrameRateIsFull: fullRate,
            GovLength: TryParseInt(video, "GovLength"),
            AudioEnabled: audioEnabled,
            SmartCodec: video is null ? null : Child(video, "smartCodec"));
    }

    /// <summary>
    /// Which stream a track id names. Hikvision numbers them <c>channel*100 + stream + 1</c>, so
    /// <c>x01</c> is main and <c>x02</c> is sub. A track outside the three we model is skipped
    /// rather than guessed at — some firmware lists transcoding pseudo-tracks.
    /// </summary>
    internal static StreamType? ParseStreamType(int trackId) => (trackId % 100) switch
    {
        1 => StreamType.Main,
        2 => StreamType.Sub,
        3 => StreamType.Third,
        _ => null,
    };

    public async Task<EncodingOptions?> GetEncodingOptionsAsync(int channel, StreamType stream,
        CancellationToken ct = default)
    {
        int trackId = TrackId(channel, stream);
        var doc = await TryGetXmlAsync($"{StreamingChannelPath(trackId)}/capabilities", ct, null);
        if (doc?.Root is null)
            return null;

        var video = doc.Root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Video");

        // Resolutions come as two parallel opt= lists — one of widths, one of heights — so they
        // are zipped positionally. A firmware that lists different lengths is not guessed at.
        var widths = OptInts(video, "videoResolutionWidth");
        var heights = OptInts(video, "videoResolutionHeight");
        var resolutions = widths.Count > 0 && widths.Count == heights.Count
            ? widths.Zip(heights, (w, h) => new Core.Resolution(w, h)).ToList()
            : [];

        var rawRates = OptInts(video, "maxFrameRate");
        return new EncodingOptions
        {
            Resolutions = resolutions,
            FrameRates = rawRates.Where(r => r > 0).Select(r => r / 100.0).ToList(),
            SupportsFullFrameRate = rawRates.Contains(0),
            Codecs = OptStrings(video, "videoCodecType"),
            QualityControlTypes = OptStrings(video, "videoQualityControlType"),
            Bitrate = ReadBitrateRange(doc.Root),
            GovLength = ReadElementRange(video, "GovLength"),
        };
    }

    /// <summary>The <c>opt="a,b,c"</c> list on one element, or empty when it declares none.</summary>
    private static IReadOnlyList<string> OptStrings(XElement? parent, string localName)
    {
        var el = parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
        string? opt = el?.Attribute("opt")?.Value;
        if (string.IsNullOrWhiteSpace(opt))
            return [];
        return opt.Split(',', StringSplitOptions.RemoveEmptyEntries |
                              StringSplitOptions.TrimEntries).ToList();
    }

    private static IReadOnlyList<int> OptInts(XElement? parent, string localName) =>
        OptStrings(parent, localName)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int v) ? (int?)v : null)
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .ToList();

    /// <summary>
    /// The writable bitrate bounds, from whichever of the two rate elements declares them. Shared
    /// with <see cref="GetBitrateRangeAsync"/> so the storage path and this one cannot disagree
    /// about what a camera will accept.
    /// </summary>
    private static BitrateRange? ReadBitrateRange(XElement root)
    {
        foreach (string name in new[] { "vbrUpperCap", "constantBitRate" })
        {
            var el = root.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
            if (el?.Attribute("min")?.Value is { } minText &&
                el.Attribute("max")?.Value is { } maxText &&
                int.TryParse(minText, out int min) && int.TryParse(maxText, out int max) &&
                min > 0 && max >= min)
                return new BitrateRange(min, max);
        }
        return null;
    }

    /// <summary>
    /// A numeric element's declared bounds. The default attribute is read under <b>both</b>
    /// spellings: one capabilities document on this firmware writes <c>def=</c> on some ids and
    /// <c>default=</c> on others, and reading only one loses it silently.
    /// </summary>
    private static ValueRange? ReadElementRange(XElement? parent, string localName)
    {
        var el = parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
        if (el is null)
            return null;
        if (el.Attribute("min")?.Value is not { } minText ||
            el.Attribute("max")?.Value is not { } maxText ||
            !int.TryParse(minText, out int min) || !int.TryParse(maxText, out int max) ||
            max < min)
            return null;

        int? def = null;
        foreach (string spelling in new[] { "def", "default" })
        {
            if (el.Attribute(spelling)?.Value is { } text && int.TryParse(text, out int d))
            {
                def = d;
                break;
            }
        }
        return new ValueRange(min, max, def);
    }

    public async Task<ChannelIdentity?> GetChannelIdentityAsync(int channel,
        CancellationToken ct = default)
    {
        // The IP-camera list first: on an NVR that is where the name an operator sees lives.
        var proxy = await TryGetXmlAsync(
            $"/ISAPI/ContentMgmt/InputProxy/channels/{channel}", ct, null);
        if (proxy?.Root is not null && Descendant(proxy.Root, "name") is { } proxyName)
            return new ChannelIdentity(channel, proxyName, ChannelNameSource.InputProxy);

        var input = await TryGetXmlAsync(
            $"/ISAPI/System/Video/inputs/channels/{channel}", ct, null);
        if (input?.Root is not null && Descendant(input.Root, "name") is { } inputName)
            return new ChannelIdentity(channel, inputName, ChannelNameSource.VideoInput);

        return null;
    }

    public async Task<ChannelChange> SetEncodingAsync(int channel, StreamType stream,
        EncodingSettings requested, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (requested.FrameRateFps is not null && requested.FullFrameRate is true)
            throw new ArgumentException(
                "a frame rate and Full Frame Rate are different choices — ask for one",
                nameof(requested));

        int trackId = TrackId(channel, stream);
        string path = StreamingChannelPath(trackId);
        string label = $"ch{channel} ({stream.ToString().ToLowerInvariant()})";

        var before = await ReadTrackAsync(trackId, stream, ct);
        if (requested.IsEmpty)
            return new ChannelChange(channel, "", before, before, Changed: false,
                "nothing was asked for");

        // Re-read immediately before the write and refuse if the device's copy moved. Schedules
        // on a live site were seen changing under an operator mid-batch; an encoder document
        // edited from the recorder's own web UI is the same hazard, and clobbering it silently
        // is worse than refusing.
        var doc = await ReReadForWriteAsync(path, $"{label} encoder settings", ct);
        var video = doc.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Video")
            ?? throw new NvrException($"{label}: GET {path} returned no <Video> element");

        ApplyEncoding(doc, video, requested, label);

        await PutXmlAsync(path, doc, ct);

        // Read back what actually stuck: a camera may snap a value to its own steps, and what
        // gets reported has to be what the recorder holds, not what was asked for.
        var after = await ReadTrackAsync(trackId, stream, ct);
        string note = DescribeShortfall(after, requested);
        return new ChannelChange(channel, "", before, after,
            Changed: !Equivalent(before, after), note)
        {
            Rejected = note.Length > 0,
        };
    }

    /// <summary>
    /// Replaces the requested fields in the device's own document. Only fields the document
    /// already carries are written: a camera that states no <c>GovLength</c> does not gain one
    /// because we asked, since inventing an element the firmware does not expect is how a whole
    /// PUT gets rejected for one field.
    /// </summary>
    private static void ApplyEncoding(XDocument doc, XElement video, EncodingSettings requested,
        string label)
    {
        var missing = new List<string>();

        if (requested.Resolution is { } res)
        {
            Require(video, "videoResolutionWidth", res.Width, "resolution", missing);
            Require(video, "videoResolutionHeight", res.Height, "resolution", missing);
        }

        if (requested.FullFrameRate is true)
            Require(video, "maxFrameRate", 0, "Full Frame Rate", missing);
        else if (requested.FrameRateFps is { } fps)
            Require(video, "maxFrameRate", (int)Math.Round(fps * 100), "frame rate", missing);

        if (requested.CodecType is { Length: > 0 } codec)
            RequireText(video, "videoCodecType", codec, "codec", missing);

        if (requested.QualityControlType is { Length: > 0 } quality)
            RequireText(video, "videoQualityControlType", quality, "quality mode", missing);

        if (requested.BitrateKbps is { } kbps)
        {
            // Both rate fields where the document carries them: vbrUpperCap governs VBR and
            // constantBitRate governs CBR, and setting both keeps the channel consistent
            // whichever mode it is in — including a write that switches the mode in the same PUT.
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
                missing.Add("bitrate (neither vbrUpperCap nor constantBitRate is in its document)");
        }

        if (requested.GovLength is { } gov)
            Require(video, "GovLength", gov, "I-frame interval", missing);

        if (requested.AudioEnabled is { } audio)
        {
            var audioEl = doc.Root?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Audio");
            var enabled = audioEl?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "enabled");
            if (enabled is null)
                missing.Add("audio (this channel's document has no Audio/enabled)");
            else
                enabled.Value = audio ? "true" : "false";
        }

        if (missing.Count > 0)
            throw new NvrException(
                $"{label}: this channel does not expose {string.Join("; ", missing)} — " +
                "nothing was written");
    }

    private static void Require(XElement video, string localName, int value, string what,
        List<string> missing)
    {
        var el = video.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
        if (el is null)
            missing.Add($"{what} (<{localName}> is not in its document)");
        else
            el.Value = value.ToString(CultureInfo.InvariantCulture);
    }

    private static void RequireText(XElement video, string localName, string value, string what,
        List<string> missing)
    {
        var el = video.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
        if (el is null)
            missing.Add($"{what} (<{localName}> is not in its document)");
        else
            el.Value = value;
    }

    /// <summary>
    /// What the recorder did <em>not</em> do, in the operator's words. Empty when it kept
    /// everything that was asked. This is the whole point of the read-back: ISAPI accepts fields
    /// it does not honour, under HTTP 200 and <c>statusCode</c> 1.
    /// </summary>
    private static string DescribeShortfall(ChannelEncoding after, EncodingSettings requested)
    {
        var kept = new List<string>();

        if (requested.Resolution is { } res && (after.Width != res.Width || after.Height != res.Height))
            kept.Add($"resolution is {after.Resolution}, not {res}");

        if (requested.FullFrameRate is true && !after.FrameRateIsFull)
            kept.Add($"frame rate is {after.FrameRateText}, not full");
        else if (requested.FrameRateFps is { } fps &&
                 (after.FrameRateIsFull || after.FrameRateFps is null ||
                  Math.Abs(after.FrameRateFps.Value - fps) >= 0.001))
            kept.Add($"frame rate is {after.FrameRateText}, not {fps:F1}");

        if (requested.CodecType is { Length: > 0 } codec &&
            !string.Equals(after.CodecType, codec, StringComparison.OrdinalIgnoreCase))
            kept.Add($"codec is {after.CodecType}, not {codec}");

        if (requested.QualityControlType is { Length: > 0 } quality &&
            !string.Equals(after.QualityControlType, quality, StringComparison.OrdinalIgnoreCase))
            kept.Add($"quality mode is {after.QualityControlType}, not {quality}");

        if (requested.BitrateKbps is { } kbps && after.BitrateKbps != kbps)
            kept.Add($"bitrate is {after.BitrateKbps?.ToString() ?? "?"} kbps, not {kbps}");

        if (requested.GovLength is { } gov && after.GovLength != gov)
            kept.Add($"I-frame interval is {after.GovLength?.ToString() ?? "?"}, not {gov}");

        if (requested.AudioEnabled is { } audio && after.AudioEnabled != audio)
            kept.Add($"audio is {(after.AudioEnabled is null ? "?" : after.AudioEnabled.Value ? "on" : "off")}, " +
                $"not {(audio ? "on" : "off")}");

        return kept.Count == 0
            ? ""
            : "the recorder kept its own values: " + string.Join("; ", kept);
    }

    private static bool Equivalent(ChannelEncoding a, ChannelEncoding b) =>
        a.Width == b.Width && a.Height == b.Height &&
        a.FrameRateIsFull == b.FrameRateIsFull &&
        Nullable.Equals(a.FrameRateFps, b.FrameRateFps) &&
        string.Equals(a.CodecType, b.CodecType, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.QualityControlType, b.QualityControlType, StringComparison.OrdinalIgnoreCase) &&
        a.VbrUpperCapKbps == b.VbrUpperCapKbps &&
        a.ConstantBitrateKbps == b.ConstantBitrateKbps &&
        a.GovLength == b.GovLength &&
        a.AudioEnabled == b.AudioEnabled;

    /// <summary>
    /// Reads one track, remembering the raw document so the write's re-read has something to
    /// compare against. Every write path goes through this — that is what makes
    /// <c>ReReadForWriteAsync</c>'s "never read this path" refusal real rather than decorative.
    /// </summary>
    private async Task<ChannelEncoding> ReadTrackAsync(int trackId, StreamType stream,
        CancellationToken ct)
    {
        string path = StreamingChannelPath(trackId);
        var doc = await GetXmlAndRememberAsync(path, ct);
        var sc = doc.Root
            ?? throw new NvrException($"GET {path} returned an empty document");
        return await ParseTrackAsync(sc, trackId, stream, ct);
    }

    public async Task<ChannelChange> SetChannelNameAsync(int channel, string name,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var identity = await GetChannelIdentityAsync(channel, ct)
            ?? throw new NvrException(
                $"ch{channel}: neither the IP-camera list nor the video-input list carries a " +
                "name for this channel, so there is nothing to rename");

        if (string.Equals(identity.Name, name, StringComparison.Ordinal))
            return new ChannelChange(channel, identity.Name, null, null, Changed: false,
                "already named that");

        string path = identity.Source == ChannelNameSource.InputProxy
            ? $"/ISAPI/ContentMgmt/InputProxy/channels/{channel}"
            : $"/ISAPI/System/Video/inputs/channels/{channel}";

        // The read has to land in _configDocs before the guarded re-read will accept the path.
        await GetXmlAndRememberAsync(path, ct);
        var doc = await ReReadForWriteAsync(path, $"ch{channel}'s name", ct);
        var root = doc.Root
            ?? throw new NvrException($"GET {path} returned an empty document");

        var nameEl = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "name")
            ?? throw new NvrException(
                $"ch{channel}: {path} carries no <name> element to write");
        nameEl.Value = name;

        await PutXmlAsync(path, doc, ct);

        var after = await GetChannelIdentityAsync(channel, ct);
        bool kept = string.Equals(after?.Name, name, StringComparison.Ordinal);
        return new ChannelChange(channel, after?.Name ?? name, null, null, Changed: kept,
            kept ? "" : $"the recorder kept the name '{after?.Name ?? "?"}'")
        {
            Rejected = !kept,
        };
    }
}
