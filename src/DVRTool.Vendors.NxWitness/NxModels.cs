using System.Globalization;
using System.Text.Json;

namespace DVRTool.Vendors.NxWitness;

/// <summary>
/// Tolerant readers over Nx's JSON. Several replies serialize 64-bit numbers as strings, the
/// legacy <c>/api/</c> tree wraps everything in <c>reply</c>, booleans occasionally arrive
/// as <c>"true"</c>, and every id wears braces — so nothing here trusts a value's JSON kind.
/// </summary>
internal static class NxJson
{
    public static JsonElement? Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) &&
        p.ValueKind != JsonValueKind.Null
            ? p
            : null;

    public static string? Str(JsonElement e, string name) => Prop(e, name) is { } p ? AsString(p) : null;

    public static string? AsString(JsonElement p) => p.ValueKind switch
    {
        JsonValueKind.String => p.GetString(),
        JsonValueKind.Number => p.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    public static long? Int64(JsonElement e, string name) => Prop(e, name) is { } p ? AsInt64(p) : null;

    public static long? AsInt64(JsonElement p)
    {
        switch (p.ValueKind)
        {
            case JsonValueKind.Number:
                if (p.TryGetInt64(out long whole))
                    return whole;
                return p.TryGetDouble(out double real) && real >= long.MinValue && real <= long.MaxValue
                    ? (long)real
                    : null;
            case JsonValueKind.String:
                string text = (p.GetString() ?? "").Trim();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out whole))
                    return whole;
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out real) &&
                       real >= long.MinValue && real <= long.MaxValue
                    ? (long)real
                    : null;
            default:
                return null;
        }
    }

    public static int? Int32(JsonElement e, string name) =>
        Int64(e, name) is long v && v >= int.MinValue && v <= int.MaxValue ? (int)v : null;

    public static double? Double(JsonElement e, string name) => Prop(e, name) is { } p ? AsDouble(p) : null;

    public static double? AsDouble(JsonElement p) => p.ValueKind switch
    {
        JsonValueKind.Number => p.TryGetDouble(out double d) ? d : null,
        JsonValueKind.String => double.TryParse(p.GetString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double s) ? s : null,
        _ => null,
    };

    /// <summary>JSON booleans, and the "true"/"false"/"1"/"0" strings Nx's property bag uses.</summary>
    public static bool? Bool(JsonElement e, string name) => Prop(e, name) is { } p
        ? p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => (p.GetString() ?? "").Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" => true,
                "false" or "0" or "no" => false,
                _ => null,
            },
            JsonValueKind.Number => p.TryGetInt64(out long n) ? n != 0 : null,
            _ => null,
        }
        : null;

    /// <summary><c>{11111111-…}</c> → <c>11111111-…</c>: the form the REST paths and the identity pin use.</summary>
    public static string StripBraces(string? id) => (id ?? "").Trim().TrimStart('{').TrimEnd('}');

    /// <summary>"1920x1080" → (1920, 1080); Nx's "*" (unknown) and anything unparseable → (0, 0).</summary>
    public static (int Width, int Height) ParseResolution(string? text)
    {
        var parts = (text ?? "").Trim().Split(['x', 'X'], StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h))
            return (w, h);
        return (0, 0);
    }

    /// <summary>The message in an Nx error body (<c>errorString</c>, else <c>errorId</c>), or "".</summary>
    public static string ErrorString(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return Str(root, "errorString") ?? Str(root, "errorId") ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}

/// <summary>One stream a camera offers, as Nx's <c>mediaStreams</c> lists it (encoderIndex 0 primary, 1 secondary).</summary>
internal sealed record NxMediaStream(int EncoderIndex, string Codec, int Width, int Height);

/// <summary>One cell of the weekly recording schedule — a day-of-week hour range and what it records.</summary>
/// <param name="DayOfWeek">1–7, Monday–Sunday (Qt's numbering).</param>
/// <param name="RecordingType">Normalized: always | metadataonly | never | metadataandlowquality.</param>
/// <param name="StreamQuality">Normalized: lowest | low | normal | high | highest | preset.</param>
/// <param name="MetadataTypes">
/// What "metadata" means for a metadata cell, normalized and lower-case: "motion", "objects",
/// "motion|objects" — or "" when the cell names none (Nx 4.x cells, which mean motion).
/// </param>
internal sealed record NxScheduleTask(
    int DayOfWeek,
    int StartTime,
    int EndTime,
    string RecordingType,
    string StreamQuality,
    double Fps,
    int BitrateKbps,
    string MetadataTypes = "")
{
    /// <summary>The cell records something; a "never" cell is schedule white space.</summary>
    public bool Records => RecordingType != "never";

    /// <summary>The cell names an explicit bitrate rather than a quality Nx converts to one.</summary>
    public bool IsPreset => StreamQuality == "preset";

    public static NxScheduleTask? Parse(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object)
            return null;
        return new NxScheduleTask(
            NxJson.Int32(t, "dayOfWeek") ?? 0,
            NxJson.Int32(t, "startTime") ?? 0,
            NxJson.Int32(t, "endTime") ?? 86_400,
            NormalizeRecordingType(NxJson.Str(t, "recordingType")),
            NxBitrate.NormalizeQuality(NxJson.Str(t, "streamQuality")),
            NxJson.Double(t, "fps") ?? 0,
            NxJson.Int32(t, "bitrateKbps") ?? 0,
            NormalizeMetadataTypes(NxJson.Str(t, "metadataTypes")));
    }

    /// <summary>"motion|objects" as Nx spells it, lower-cased; "none" and absent both read as "".</summary>
    internal static string NormalizeMetadataTypes(string? raw)
    {
        string r = (raw ?? "").Trim().ToLowerInvariant();
        return r == "none" ? "" : r;
    }

    /// <summary>
    /// REST v3 spells them <c>always | metadataOnly | never | metadataAndLowQuality</c>; the
    /// legacy API <c>RT_Always | RT_MotionOnly | RT_Never | RT_MotionAndLowQuality</c>.
    /// </summary>
    internal static string NormalizeRecordingType(string? raw)
    {
        string r = (raw ?? "").Trim().ToLowerInvariant();
        if (r.StartsWith("rt_", StringComparison.Ordinal))
            r = r[3..];
        return r switch
        {
            "motiononly" => "metadataonly",
            "motionandlowquality" => "metadataandlowquality",
            "" => "always",
            _ => r,
        };
    }
}

/// <summary>A camera as this client needs it: identity, the recording schedule, and the streams it offers.</summary>
/// <param name="Id">The device GUID without braces — what the REST paths, RTSP and <c>/media/</c> take.</param>
/// <param name="KeepCameraProfile">
/// The Expert setting "Keep camera stream and profile settings" (<c>options.isControlEnabled</c>
/// false — verified live; the older spelling <c>controlEnabled</c> is accepted too): Nx then
/// stores schedule quality and bitrate but never sends them to the camera.
/// </param>
/// <param name="PrimaryMinKbps">
/// The primary stream's bitrate floor and ceiling from <c>mediaCapabilities.streamCapabilities</c>
/// (192–10666 kbps on a 2560×1440 DW unit, live) — Nx's own bounds for the schedule slider.
/// </param>
/// <param name="MaxArchiveDays">
/// <c>schedule.maxArchiveDays</c> when positive: the recorder deletes this camera's footage
/// past that age whatever the disks hold. Nx stores a disabled cap as a negative number.
/// </param>
internal sealed record NxCamera(
    string Id,
    string Name,
    string PhysicalId,
    string Mac,
    string Url,
    string ServerId,
    string DeviceType,
    string Status,
    bool ScheduleEnabled,
    IReadOnlyList<NxScheduleTask> Tasks,
    bool DontRecordPrimary,
    bool DontRecordSecondary,
    bool DualStreamingDisabled,
    bool KeepCameraProfile,
    bool AudioEnabled,
    bool AudioSupported,
    bool DontRecordAudio,
    NxMediaStream? Primary,
    NxMediaStream? Secondary,
    int? PrimaryMinKbps = null,
    int? PrimaryMaxKbps = null,
    int? MaxArchiveDays = null)
{
    /// <summary>An I/O module has a schedule and no video; it is not a camera.</summary>
    public bool IsIoModule =>
        DeviceType.Replace("_", "", StringComparison.Ordinal)
            .Equals("IOModule", StringComparison.OrdinalIgnoreCase);

    public bool? Online => Status.ToLowerInvariant() switch
    {
        "online" or "recording" => true,
        "offline" or "unauthorized" or "incompatible" or "mismatchedcertificate" => false,
        _ => null,
    };

    /// <summary>The schedule cells that record, in schedule order.</summary>
    public IReadOnlyList<NxScheduleTask> RecordingTasks => Tasks.Where(t => t.Records).ToList();

    public static NxCamera? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        string id = NxJson.StripBraces(NxJson.Str(e, "id"));
        if (id.Length == 0)
            return null;

        var schedule = NxJson.Prop(e, "schedule");
        // A device with no schedule object records nothing; a schedule without isEnabled is on.
        bool scheduleEnabled = schedule is { } s && (NxJson.Bool(s, "isEnabled") ?? true);
        var tasks = new List<NxScheduleTask>();
        if (schedule is { } sc && NxJson.Prop(sc, "tasks") is { ValueKind: JsonValueKind.Array } arr)
        {
            foreach (var t in arr.EnumerateArray())
                if (NxScheduleTask.Parse(t) is { } task)
                    tasks.Add(task);
        }

        // Two bags of settings: `options` (typed, always present) and `parameters`, the
        // resource property bag that lists only what has been set, as strings. The "don't
        // record the primary/secondary stream" switches are properties, so a camera that
        // records both streams (the default) simply has no such key — absent means false.
        var options = NxJson.Prop(e, "options");
        var parameters = NxJson.Prop(e, "parameters");
        bool Opt(string name, bool fallback) =>
            (options is { } o ? NxJson.Bool(o, name) : null)
            ?? (parameters is { } p ? NxJson.Bool(p, name) : null)
            ?? fallback;

        // Some capability keys in `parameters` are 1/0 numbers, not booleans; null means the
        // key is absent, which for a capability is "the server has not said", not "no".
        bool? Flag(string name)
        {
            if (Opt2(name) is bool b)
                return b;
            foreach (var bag in new[] { options, parameters })
                if (bag is { } g && NxJson.Int32(g, name) is int n)
                    return n != 0;
            return null;

            bool? Opt2(string key) =>
                (options is { } o ? NxJson.Bool(o, key) : null)
                ?? (parameters is { } p ? NxJson.Bool(p, key) : null);
        }

        var (primary, secondary) = ParseMediaStreams(NxJson.Prop(e, "mediaStreams"));

        // mediaCapabilities.streamCapabilities.primary: Nx's own min/max for the schedule's
        // bitrate slider on this camera — the planner's writable range.
        var primaryCaps = NxJson.Prop(e, "mediaCapabilities") is { } caps &&
                          NxJson.Prop(caps, "streamCapabilities") is { } streams
            ? NxJson.Prop(streams, "primary")
            : null;
        int? primaryMin = primaryCaps is { } pc ? Positive(NxJson.Int32(pc, "minBitrateKbps")) : null;
        int? primaryMax = primaryCaps is { } pc2 ? Positive(NxJson.Int32(pc2, "maxBitrateKbps")) : null;

        // A disabled cap is stored negative (-30 = "off, 30 remembered"); only a positive one binds.
        int? maxArchiveDays = null;
        if (schedule is { } sch)
        {
            maxArchiveDays = Positive(NxJson.Int32(sch, "maxArchiveDays"));
            if (maxArchiveDays is null && Positive(NxJson.Int32(sch, "maxArchivePeriodS")) is int seconds)
                maxArchiveDays = Math.Max(1, (int)Math.Round(seconds / 86_400.0));
        }

        return new NxCamera(
            id,
            NxJson.Str(e, "name") ?? id,
            NxJson.Str(e, "physicalId") ?? "",
            NxJson.Str(e, "mac") ?? "",
            NxJson.Str(e, "url") ?? "",
            NxJson.StripBraces(NxJson.Str(e, "serverId")),
            NxJson.Str(e, "deviceType") ?? "",
            NxJson.Str(e, "status") ?? "",
            scheduleEnabled,
            tasks,
            DontRecordPrimary: Opt("dontRecordPrimaryStream", false),
            DontRecordSecondary: Opt("dontRecordSecondaryStream", false),
            DualStreamingDisabled: Opt("isDualStreamingDisabled", false),
            KeepCameraProfile: !(Opt("controlEnabled", true) && Opt("isControlEnabled", true)),
            // Audio has TWO switches, and they are not the same one. `options.isAudioEnabled`
            // is the General tab's "Enable audio" — whether the server pulls audio at all.
            // `parameters.dontRecordAudio` is the Expert tab's "Do not record audio" — a
            // property, so absent on a default camera, which keeps audio off the disk even if
            // somebody later enables it. `parameters.isAudioSupported` is 1/0 rather than a
            // bool, so it is read as a number too; `forcedIsAudioSupported` is the operator's
            // override for a camera whose ONVIF answer was wrong and wins over the probe.
            AudioEnabled: Opt("isAudioEnabled", false),
            AudioSupported: Flag("forcedIsAudioSupported") ?? Flag("isAudioSupported") ?? false,
            DontRecordAudio: Flag("dontRecordAudio") ?? false,
            primary,
            secondary,
            PrimaryMinKbps: primaryMin,
            PrimaryMaxKbps: primaryMax,
            MaxArchiveDays: maxArchiveDays);
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;

    /// <summary>
    /// <c>mediaStreams</c> is a bare array of <c>{"encoderIndex", "codec", "resolution", …}</c>
    /// in REST v3 (verified live; it carries a third entry with <c>encoderIndex</c> −1 and
    /// codec 0 — the server's transcoding pseudo-stream, ignored here), wrapped as
    /// <c>{"streams": […]}</c> in older shapes, and the same JSON-encoded <em>as a string</em>
    /// in the legacy API — all three are accepted.
    /// </summary>
    internal static (NxMediaStream? Primary, NxMediaStream? Secondary) ParseMediaStreams(
        JsonElement? mediaStreams)
    {
        if (mediaStreams is not { } ms)
            return (null, null);

        JsonDocument? parsed = null;
        try
        {
            if (ms.ValueKind == JsonValueKind.String)
            {
                string text = (ms.GetString() ?? "").Trim();
                if (text.Length == 0)
                    return (null, null);
                try
                {
                    parsed = JsonDocument.Parse(text);
                }
                catch (JsonException)
                {
                    return (null, null);
                }
                ms = parsed.RootElement;
            }

            JsonElement streams = ms.ValueKind == JsonValueKind.Array
                ? ms
                : NxJson.Prop(ms, "streams") ?? default;
            if (streams.ValueKind != JsonValueKind.Array)
                return (null, null);

            NxMediaStream? primary = null, secondary = null;
            foreach (var s in streams.EnumerateArray())
            {
                int index = NxJson.Int32(s, "encoderIndex") ?? 0;
                var (w, h) = NxJson.ParseResolution(NxJson.Str(s, "resolution"));
                var stream = new NxMediaStream(index, CodecName(NxJson.Prop(s, "codec")), w, h);
                if (index == 0)
                    primary ??= stream;
                else if (index == 1)
                    secondary ??= stream;
            }
            return (primary, secondary);
        }
        finally
        {
            parsed?.Dispose();
        }
    }

    /// <summary>
    /// Nx names a stream's codec by FFmpeg's <c>AVCodecID</c> (27 H.264, 173 H.265, 7 MJPEG);
    /// some builds send the name instead. Either way the result is the display name the
    /// other vendors' drivers produce, so the Storage grids read the same.
    /// </summary>
    internal static string CodecName(JsonElement? codec)
    {
        if (codec is not { } c)
            return "";
        if (c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out long id))
        {
            return id switch
            {
                27 => "H.264",
                173 or 1211250229 => "H.265",
                7 or 8 => "MJPEG",
                12 => "MPEG-4",
                2 => "MPEG-2",
                226 => "AV1",
                _ => $"codec {id}",
            };
        }
        string raw = (NxJson.AsString(c) ?? "").Trim();
        return raw.Replace(".", "", StringComparison.Ordinal).ToUpperInvariant() switch
        {
            "H264" or "AVC" => "H.264",
            "H265" or "HEVC" => "H.265",
            "MJPEG" or "JPEG" => "MJPEG",
            _ => raw,
        };
    }
}

/// <summary>One recorded period of the footage list; a negative duration is Nx's "still recording".</summary>
internal sealed record NxFootagePeriod(long StartMs, long DurationMs)
{
    public bool OpenEnded => DurationMs < 0;
}

/// <summary>One storage volume's sizes and runtime state, as the legacy space call reports them.</summary>
internal sealed record NxStorageSpace(
    long? TotalBytes, long? FreeBytes, long? ReservedBytes, bool? Online, bool? UsedForWriting,
    string Type);
