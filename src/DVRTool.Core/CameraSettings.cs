namespace DVRTool.Core;

/// <summary>
/// The encoder settings of one stream track, as the recorder holds them.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a sibling of <see cref="CameraStream"/> rather than a replacement.
/// <see cref="CameraStream"/> answers "what will this camera write to disk", exists to feed
/// retention math, and carries only main-stream tracks for exactly that reason. This record
/// answers "what is this track configured to do", covers every track, and is what a write is
/// planned against. Merging them would drag sub-stream bitrates into retention totals, which
/// is the mistake <c>GetMainStreamsAsync</c>'s track filter exists to prevent.
/// </para>
/// </remarks>
/// <param name="FrameRateFps">
/// Decoded from ISAPI's fps×100 encoding (2000 → 20.0), or null when the track states none.
/// </param>
/// <param name="FrameRateIsFull">
/// The track is set to "Full Frame Rate" — ISAPI sends <c>maxFrameRate</c> 0, which is a
/// legitimate choice and not "unknown". <paramref name="FrameRateFps"/> is then the rate that
/// choice resolves to on this camera, read from its capabilities, and writing it back as a
/// number would silently pin a camera that was set to follow its own maximum.
/// </param>
/// <param name="GovLength">
/// The I-frame interval (Hikvision's <c>GovLength</c>), in frames. Null when the track does not
/// state one.
/// </param>
/// <param name="SmartCodec">
/// H.264+/H.265+ — the vendor's own long-GOP mode, carried verbatim because its spelling and
/// its very presence vary by firmware.
/// </param>
public sealed record ChannelEncoding(
    int Channel,
    int TrackId,
    StreamType Stream,
    bool Enabled,
    string CodecType,
    int Width,
    int Height,
    double? FrameRateFps,
    string QualityControlType,
    int? VbrUpperCapKbps,
    int? ConstantBitrateKbps,
    int? FixedQuality,
    bool FrameRateIsFull = false,
    int? GovLength = null,
    bool? AudioEnabled = null,
    string? SmartCodec = null)
{
    public bool IsVbr =>
        string.Equals(QualityControlType, "VBR", StringComparison.OrdinalIgnoreCase);

    /// <summary>The rate this track is configured at — the VBR ceiling, or the CBR constant.</summary>
    public int? BitrateKbps => IsVbr
        ? VbrUpperCapKbps
        : ConstantBitrateKbps ?? VbrUpperCapKbps;

    public string Resolution => Width > 0 || Height > 0 ? $"{Width}x{Height}" : "";

    /// <summary>"20.0", or "30.0 (full)" when the track follows the camera's own maximum.</summary>
    public string FrameRateText => FrameRateFps is double fps
        ? FrameRateIsFull ? $"{fps:F1} (full)" : fps.ToString("F1")
        : "";

    /// <summary>"main", "sub", "third" — how an operator names the track.</summary>
    public string StreamText => Stream switch
    {
        StreamType.Main => "main",
        StreamType.Sub => "sub",
        StreamType.Third => "third",
        _ => Stream.ToString().ToLowerInvariant(),
    };
}

/// <summary>One resolution a channel declares it will encode at.</summary>
public sealed record Resolution(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";

    /// <summary>Parses "1920x1080" (or "1920X1080"); null when the text is not a resolution.</summary>
    public static Resolution? Parse(string? text)
    {
        if (text is null)
            return null;
        int x = text.IndexOfAny(['x', 'X']);
        if (x <= 0 || x == text.Length - 1)
            return null;
        return int.TryParse(text[..x], out int w) && int.TryParse(text[(x + 1)..], out int h) &&
               w > 0 && h > 0
            ? new Resolution(w, h)
            : null;
    }
}

/// <summary>
/// What one track says it will accept — the device's own <c>opt=</c> lists and <c>min</c>/
/// <c>max</c> bounds, never ours.
/// </summary>
/// <remarks>
/// <para>
/// An empty list means <b>the device declared nothing</b>, which is a different fact from "this
/// field accepts nothing" and must render differently — the same "n/a is not ?" rule
/// <see cref="ConfigScope"/> already enforces. A front end offers a free entry where the device
/// declared no list and a fixed choice where it did; it never invents the list, because a
/// resolution a camera does not support is accepted and silently ignored, and only the
/// read-back would ever catch it.
/// </para>
/// </remarks>
public sealed record EncodingOptions
{
    public IReadOnlyList<Resolution> Resolutions { get; init; } = [];

    /// <summary>The declared rates in fps, already divided out of ISAPI's ×100 encoding.</summary>
    public IReadOnlyList<double> FrameRates { get; init; } = [];

    /// <summary>True when the device lists 0 — "Full Frame Rate" — among its rates.</summary>
    public bool SupportsFullFrameRate { get; init; }

    public IReadOnlyList<string> Codecs { get; init; } = [];
    public IReadOnlyList<string> QualityControlTypes { get; init; } = [];
    public BitrateRange? Bitrate { get; init; }
    public ValueRange? GovLength { get; init; }

    /// <summary>The device declared nothing at all — treat every field as free entry.</summary>
    public bool IsEmpty =>
        Resolutions.Count == 0 && FrameRates.Count == 0 && Codecs.Count == 0 &&
        QualityControlTypes.Count == 0 && Bitrate is null && GovLength is null;

    /// <summary>
    /// Whether a value is allowed, given that an <em>empty</em> list means "the device did not
    /// say" and therefore permits anything. Never narrows on our own authority.
    /// </summary>
    public bool Allows(Resolution r) => Resolutions.Count == 0 || Resolutions.Contains(r);

    public bool Allows(double fps) =>
        FrameRates.Count == 0 || FrameRates.Any(f => Math.Abs(f - fps) < 0.001);

    public bool AllowsCodec(string codec) => Codecs.Count == 0 ||
        Codecs.Any(c => string.Equals(c, codec, StringComparison.OrdinalIgnoreCase));

    public bool AllowsQuality(string quality) => QualityControlTypes.Count == 0 ||
        QualityControlTypes.Any(q => string.Equals(q, quality, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What an encoder write was asked to do. Every field nullable and null means "leave it alone",
/// the same convention as <see cref="TimeSettings"/> and <see cref="NtpSettings"/>.
/// </summary>
/// <param name="FullFrameRate">
/// Set the track to follow the camera's own maximum (ISAPI <c>maxFrameRate</c> 0). Distinct from
/// <paramref name="FrameRateFps"/>, and the two are mutually exclusive: "full" is a choice the
/// firmware re-resolves per resolution, and writing today's resolved number instead would pin it.
/// </param>
public sealed record EncodingSettings(
    Resolution? Resolution = null,
    double? FrameRateFps = null,
    bool? FullFrameRate = null,
    string? CodecType = null,
    string? QualityControlType = null,
    int? BitrateKbps = null,
    int? GovLength = null,
    bool? AudioEnabled = null)
{
    /// <summary>Nothing was asked for — the whole request is a no-op.</summary>
    public bool IsEmpty =>
        Resolution is null && FrameRateFps is null && FullFrameRate is null &&
        CodecType is null && QualityControlType is null && BitrateKbps is null &&
        GovLength is null && AudioEnabled is null;
}

/// <summary>
/// A channel's name, as the recorder labels it.
/// </summary>
/// <param name="Source">
/// Which document the name came from and would be written back to — the endpoint set differs
/// between an IP channel and an analog input, and writing to the wrong one is accepted and
/// silently does nothing.
/// </param>
public sealed record ChannelIdentity(int Channel, string Name, ChannelNameSource Source);

/// <summary>Where a channel's name lives on this recorder.</summary>
public enum ChannelNameSource
{
    /// <summary>Not established — nothing may be written until a read says which it is.</summary>
    Unknown,

    /// <summary>An IP camera behind an NVR: <c>/ISAPI/ContentMgmt/InputProxy/channels/{id}</c>.</summary>
    InputProxy,

    /// <summary>An analog input on a DVR or hybrid: <c>/ISAPI/System/Video/inputs/channels/{id}</c>.</summary>
    VideoInput,
}

/// <summary>
/// What the recorder held before a camera write, what it holds after, and whether anything
/// moved. An exact mirror of <see cref="ConfigChange"/> and <see cref="RecordingOptionChange"/>,
/// for the same reason: a recorder that accepts a field it does not honour is the failure mode
/// worth catching, and only the read-back catches it.
/// </summary>
/// <param name="Note">
/// Why nothing changed, or what the recorder did instead of what was asked. Empty on a plain
/// successful change.
/// </param>
public sealed record ChannelChange(
    int Channel,
    string Name,
    ChannelEncoding? Before,
    ChannelEncoding? After,
    bool Changed,
    string Note = "")
{
    /// <summary>A change the recorder refused or silently dropped: asked, but not applied.</summary>
    public bool Rejected { get; init; }
}

/// <summary>What a planned channel write will do, once the device's own limits are applied.</summary>
public enum ChannelPlanStatus
{
    /// <summary>The track already holds every requested value.</summary>
    NoChange,

    /// <summary>The write will be attempted.</summary>
    WillChange,

    /// <summary>A requested value is outside what this track declared. Refused, never clamped.</summary>
    OutOfRange,

    /// <summary>The track could not be read, so there is nothing to plan against.</summary>
    Unreadable,

    /// <summary>
    /// The operator pinned this camera's rate and the write would move it. Held back rather
    /// than applied — a pin is a promise the retention planner already keeps.
    /// </summary>
    Pinned,
}

/// <summary>One channel's planned write.</summary>
/// <param name="Fields">
/// The fields that will actually move, each already worded for an operator
/// ("fps 20.0 → 15.0"). Empty when nothing moves.
/// </param>
/// <param name="Problem">
/// Why this channel will not be written, in the operator's words. Empty when it will be.
/// </param>
public sealed record PlannedChannel(
    int Channel,
    string Name,
    StreamType Stream,
    ChannelPlanStatus Status,
    IReadOnlyList<string> Fields,
    string Problem = "")
{
    public bool Writes => Status == ChannelPlanStatus.WillChange;

    /// <summary>"ch3 Front Door (main): fps 20.0 → 15.0, bitrate 4096 → 2048 kbps".</summary>
    public string Describe()
    {
        string head = $"ch{Channel}" + (Name.Length > 0 ? $" {Name}" : "") +
            $" ({Stream.ToString().ToLowerInvariant()})";
        if (Problem.Length > 0)
            return $"{head}: {Problem}";
        return Fields.Count == 0
            ? $"{head}: already set"
            : $"{head}: {string.Join(", ", Fields)}";
    }
}

/// <summary>
/// A camera-settings write, decided before anything is sent.
/// </summary>
/// <remarks>
/// <para>
/// Pure and in Core on purpose: it is the one piece of arithmetic both the CLI's dry run and the
/// GUI's confirmation dialog describe the write from, exactly as <c>UserAddPlan</c> and
/// <c>BitratePlan</c> are. Two front ends deciding separately what a write will do is how they
/// come to disagree, and the dialog that describes the write is the last thing standing between
/// an operator and a fleet of cameras set to the wrong resolution.
/// </para>
/// </remarks>
public sealed record CameraSettingsPlan(
    IReadOnlyList<PlannedChannel> Channels,
    EncodingSettings Requested)
{
    public IReadOnlyList<PlannedChannel> Writes =>
        Channels.Where(c => c.Writes).ToList();

    public int WriteCount => Channels.Count(c => c.Writes);

    /// <summary>Channels that will not be written and why — never silently dropped.</summary>
    public IReadOnlyList<PlannedChannel> HeldBack =>
        Channels.Where(c => c.Status is ChannelPlanStatus.OutOfRange or
            ChannelPlanStatus.Unreadable or ChannelPlanStatus.Pinned).ToList();

    /// <summary>"4 channels will change, 2 held back, 10 already set".</summary>
    public string Summary
    {
        get
        {
            int held = HeldBack.Count;
            int same = Channels.Count(c => c.Status == ChannelPlanStatus.NoChange);
            var parts = new List<string> { $"{WriteCount} channel(s) will change" };
            if (held > 0)
                parts.Add($"{held} held back");
            if (same > 0)
                parts.Add($"{same} already set");
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Plans a write across the given tracks.
    /// </summary>
    /// <param name="current">The tracks as they were just read.</param>
    /// <param name="requested">What to change; null fields are left alone.</param>
    /// <param name="options">
    /// Per-channel declared limits, keyed by track id. A channel with no entry is planned without
    /// bounds — the device declared none, which permits anything rather than nothing.
    /// </param>
    /// <param name="names">Channel names for the report, keyed by channel.</param>
    /// <param name="pins">
    /// The operator's pinned rates. A pin is only consulted when the write would move the
    /// <em>bitrate</em>: pinning a camera protects its rate from the retention planner, and
    /// nothing else about it.
    /// </param>
    /// <param name="ignorePins">
    /// Write pinned channels anyway. The caller is responsible for refusing to pair this with a
    /// real write without a second confirmation, exactly as <c>plan --ignore-pins</c> does.
    /// </param>
    public static CameraSettingsPlan Build(
        IReadOnlyList<ChannelEncoding> current,
        EncodingSettings requested,
        IReadOnlyDictionary<int, EncodingOptions>? options = null,
        IReadOnlyDictionary<int, string>? names = null,
        ChannelPinSet? pins = null,
        bool ignorePins = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(requested);

        var planned = new List<PlannedChannel>();
        foreach (var track in current)
        {
            string name = names?.GetValueOrDefault(track.Channel) ?? "";
            var limits = options is not null &&
                         options.TryGetValue(track.TrackId, out var found) ? found : null;

            planned.Add(PlanOne(track, name, requested, limits, pins, ignorePins));
        }
        return new CameraSettingsPlan(planned, requested);
    }

    private static PlannedChannel PlanOne(
        ChannelEncoding track, string name, EncodingSettings requested,
        EncodingOptions? limits, ChannelPinSet? pins, bool ignorePins)
    {
        var fields = new List<string>();
        var refusals = new List<string>();

        if (requested.Resolution is { } res)
        {
            if (limits is not null && !limits.Allows(res))
                refusals.Add($"{res} is not one of the resolutions this camera declares " +
                    $"({string.Join(", ", limits.Resolutions)})");
            else if (res.Width != track.Width || res.Height != track.Height)
                fields.Add($"resolution {track.Resolution} → {res}");
        }

        if (requested.FullFrameRate is true)
        {
            if (limits is not null && limits.FrameRates.Count > 0 && !limits.SupportsFullFrameRate)
                refusals.Add("this camera does not offer Full Frame Rate");
            else if (!track.FrameRateIsFull)
                fields.Add($"fps {track.FrameRateText} → full");
        }
        else if (requested.FrameRateFps is { } fps)
        {
            if (limits is not null && !limits.Allows(fps))
                refusals.Add($"{fps:F1} fps is not one of the rates this camera declares " +
                    $"({string.Join(", ", limits.FrameRates.Select(f => f.ToString("F1")))})");
            else if (track.FrameRateIsFull ||
                     track.FrameRateFps is null ||
                     Math.Abs(track.FrameRateFps.Value - fps) >= 0.001)
                fields.Add($"fps {track.FrameRateText} → {fps:F1}");
        }

        if (requested.CodecType is { Length: > 0 } codec)
        {
            if (limits is not null && !limits.AllowsCodec(codec))
                refusals.Add($"{codec} is not one of the codecs this camera declares " +
                    $"({string.Join(", ", limits.Codecs)})");
            else if (!string.Equals(codec, track.CodecType, StringComparison.OrdinalIgnoreCase))
                fields.Add($"codec {track.CodecType} → {codec}");
        }

        if (requested.QualityControlType is { Length: > 0 } quality)
        {
            if (limits is not null && !limits.AllowsQuality(quality))
                refusals.Add($"{quality} is not one of the modes this camera declares " +
                    $"({string.Join(", ", limits.QualityControlTypes)})");
            else if (!string.Equals(quality, track.QualityControlType,
                         StringComparison.OrdinalIgnoreCase))
                fields.Add($"quality {track.QualityControlType} → {quality}");
        }

        bool movesBitrate = false;
        if (requested.BitrateKbps is { } kbps)
        {
            if (limits?.Bitrate is { } range && (kbps < range.MinKbps || kbps > range.MaxKbps))
                refusals.Add($"{kbps} kbps is outside the range this camera declares " +
                    $"({range.MinKbps}–{range.MaxKbps})");
            else if (track.BitrateKbps != kbps)
            {
                movesBitrate = true;
                fields.Add($"bitrate {track.BitrateKbps?.ToString() ?? "?"} → {kbps} kbps");
            }
        }

        if (requested.GovLength is { } gov)
        {
            if (limits?.GovLength is { } range && !range.Contains(gov))
                refusals.Add($"I-frame interval {gov} is outside the range this camera " +
                    $"declares ({range.Text})");
            else if (track.GovLength != gov)
                fields.Add($"I-frame interval {track.GovLength?.ToString() ?? "?"} → {gov}");
        }

        if (requested.AudioEnabled is { } audio && track.AudioEnabled != audio)
            fields.Add($"audio {Describe(track.AudioEnabled)} → {(audio ? "on" : "off")}");

        if (refusals.Count > 0)
            return new PlannedChannel(track.Channel, name, track.Stream,
                ChannelPlanStatus.OutOfRange, [], string.Join("; ", refusals));

        // A pin protects the camera's rate from being decided for it. It bites only on a write
        // that actually moves the bitrate — changing a pinned camera's resolution is not what a
        // pin was a promise about.
        if (movesBitrate && !ignorePins && pins?.For(track.Channel) is { } pin)
            return new PlannedChannel(track.Channel, name, track.Stream,
                ChannelPlanStatus.Pinned, [],
                $"pinned ({pin.Describe()}) — held back rather than re-rated");

        return new PlannedChannel(track.Channel, name, track.Stream,
            fields.Count > 0 ? ChannelPlanStatus.WillChange : ChannelPlanStatus.NoChange,
            fields);
    }

    private static string Describe(bool? value) =>
        value is null ? "?" : value.Value ? "on" : "off";
}

/// <summary>
/// Opt-in capability: reading the encoder settings of the cameras on a recorder, as opposed to
/// the recorder's own configuration (<see cref="IDeviceConfigClient"/>) or what they will write
/// to disk (<see cref="IStorageClient"/>).
/// </summary>
/// <remarks>
/// Hikvision only today. Dahua's encoder table is readable and its write is specced but has
/// never been fired, and Nx sets a camera's rate through its own schedule cells rather than a
/// per-track document — so neither implements this, which is the point of the interface being
/// opt-in.
/// </remarks>
public interface ICameraSettingsClient
{
    /// <summary>
    /// Every stream track of every camera — main, sub and third — ordered by channel then
    /// stream. Unlike <see cref="IStorageClient.GetMainStreamsAsync"/> this deliberately keeps
    /// the sub streams: they are settings to edit here, not bitrates to add to a retention total.
    /// </summary>
    Task<IReadOnlyList<ChannelEncoding>> GetEncodingAsync(CancellationToken ct = default);

    /// <summary>
    /// What one track declares it will accept, or null when the device answers no capabilities
    /// document at all. An <see cref="EncodingOptions.IsEmpty"/> result is a different fact: the
    /// document exists and declares nothing.
    /// </summary>
    Task<EncodingOptions?> GetEncodingOptionsAsync(int channel, StreamType stream,
        CancellationToken ct = default);

    /// <summary>
    /// A channel's name and which document it lives in, or null when neither answers. The source
    /// is part of the answer because it is what a write has to be aimed at.
    /// </summary>
    Task<ChannelIdentity?> GetChannelIdentityAsync(int channel, CancellationToken ct = default);
}

/// <summary>
/// The write half. Kept a separate interface for the same reason
/// <see cref="IDeviceConfigWriter"/> is: a vendor can ship the read tier with no write path
/// existing at all.
/// </summary>
/// <remarks>
/// Read-modify-write is mandatory and enforced. The channel document carries fields that differ
/// by firmware and by camera model, so a hand-built minimal PUT drops them on every save.
/// Implementations re-read the document immediately before writing, refuse when it moved
/// underneath them, replace only the requested fields, PUT it whole, then read back and report
/// what the recorder kept.
/// </remarks>
public interface ICameraSettingsWriter : ICameraSettingsClient
{
    Task<ChannelChange> SetEncodingAsync(int channel, StreamType stream,
        EncodingSettings requested, CancellationToken ct = default);

    /// <summary>Renames a channel, writing to whichever document holds its name.</summary>
    Task<ChannelChange> SetChannelNameAsync(int channel, string name,
        CancellationToken ct = default);
}
