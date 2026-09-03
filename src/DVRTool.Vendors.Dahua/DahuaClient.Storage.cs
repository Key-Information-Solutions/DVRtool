using System.Globalization;
using DVRTool.Core;

namespace DVRTool.Vendors.Dahua;

/// <summary>
/// The <see cref="IStorageClient"/> face of the Dahua CGI client: disk inventory, the
/// recording (main) stream of every channel, oldest footage still on disk, and the
/// recording-bitrate write.
/// </summary>
/// <remarks>
/// See <c>docs/dahua-storage.md</c> before changing endpoints or units here. Dahua reports
/// disk sizes in bytes (converted to the decimal megabytes the Core model carries), encodes
/// each channel's recording stream as <c>Encode[ch].MainFormat[n]</c> where n is the
/// General/Motion/Alarm record type, and numbers its config tables from 0 while the public
/// API — like the rest of this client — speaks 1-based display channels.
/// </remarks>
public sealed partial class DahuaClient : IStorageClient
{
    private const string EncodeConfigPath = "/cgi-bin/configManager.cgi?action=getConfig&name=Encode";
    private const string RecordConfigPath = "/cgi-bin/configManager.cgi?action=getConfig&name=Record";

    /// <summary>MainFormat record types: 0 General (schedule), 1 Motion, 2 Alarm.</summary>
    private static readonly int[] RecordTypes = [0, 1, 2];

    public async Task<StorageInfo> GetStorageInfoAsync(CancellationToken ct = default)
    {
        // list[0].State=Success
        // list[0].Detail[0].Type=ReadWrite
        // list[0].Detail[0].TotalBytes=7999997870080
        // list[0].Detail[0].UsedBytes=7999997870080
        // list[0].Detail[0].IsError=false
        // (Name/Path/Media/Model/SerialNumber appear on some firmware only.)
        var kv = ParseKeyValues(await GetTextAsync(
            "/cgi-bin/storageDevice.cgi?action=getDeviceAllInfo", ct));

        // The documented shape is list[N].…; some firmware answers list.info[N].… instead.
        string listPrefix = kv.Keys.Any(k => k.StartsWith("list.info[", StringComparison.Ordinal))
            ? "list.info["
            : "list[";

        var hdds = new List<HddInfo>();
        foreach (int i in IndexesInOrder(kv.Keys, listPrefix))
        {
            string row = $"{listPrefix}{i}]";
            string P(string field) => kv.GetValueOrDefault($"{row}.{field}", "");
            string name = P("Name");
            if (name.Length == 0)
                name = $"disk{i + 1}";

            // A disk may be split into several partitions (Detail[j]); the bay is the sum.
            long total = 0, used = 0;
            bool anyError = false;
            var types = new List<string>();
            foreach (int j in IndexesInOrder(kv.Keys, $"{row}.Detail["))
            {
                string D(string field) => kv.GetValueOrDefault($"{row}.Detail[{j}].{field}", "");
                total += ParseLong(D("TotalBytes"));
                used += ParseLong(D("UsedBytes"));
                anyError |= string.Equals(D("IsError"), "true", StringComparison.OrdinalIgnoreCase);
                if (D("Type") is { Length: > 0 } type && !types.Contains(type))
                    types.Add(type);
            }

            hdds.Add(new HddInfo(
                Id: BayNumber(name, hdds.Count + 1),
                Name: name,
                HddType: P("Media") is { Length: > 0 } media ? media : P("Type"),
                Status: MapDiskStatus(P("State"), anyError, total),
                Property: string.Join("+", types),
                CapacityMB: total / 1_000_000,
                FreeSpaceMB: Math.Max(0, total - used) / 1_000_000,
                SerialNumber: P("SerialNumber") is { Length: > 0 } sn ? sn : P("Serial"),
                Model: P("Model")));
        }

        return new StorageInfo(hdds, WorkMode: null, MaxSupportedHdds: null);
    }

    public Task<IReadOnlyList<CameraStream>> GetMainStreamsAsync(
        CancellationToken ct = default) => ReadMainStreamsAsync(withSchedules: true, ct);

    /// <summary>
    /// The streams, with or without the Record table that says when each channel records.
    /// The write path's read-back only wants the bitrate and skips the table — 375 KB on a
    /// 128-channel unit.
    /// </summary>
    private async Task<IReadOnlyList<CameraStream>> ReadMainStreamsAsync(bool withSchedules,
        CancellationToken ct)
    {
        // table.Encode[0].MainFormat[0].Video.BitRate=4096
        // table.Encode[0].MainFormat[0].Video.BitRateControl=VBR
        // table.Encode[0].MainFormat[0].Video.Compression=H.265
        // table.Encode[0].MainFormat[0].Video.FPS=15
        // table.Encode[0].MainFormat[0].Video.Width=2688 / Height=1520 / resolution=2688x1520
        // table.Encode[0].MainFormat[0].VideoEnable=true
        // MainFormat[0] is the General (schedule) record stream; [1] Motion, [2] Alarm — the
        // recorder switches between them by what triggered the recording, so the worst-case
        // bitrate is the highest of the three. The other fields are described from [0].
        // An NVR only lists channels that have a camera bound (51 rows on a 128-channel unit).
        var kv = ParseKeyValues(await GetTextAsync(EncodeConfigPath, ct));

        // VideoEnable gates the stream; whether the schedule actually records it is a
        // separate table — table.RecordMode[ch].Mode: 0 auto (schedule), 1 manual, 2 stop.
        // A channel set to stop writes nothing, so it must not count toward retention.
        // Optional: an NVR that lacks the table just reports every stream as it stands.
        var recordMode = new Dictionary<string, string>();
        try
        {
            recordMode = ParseKeyValues(await GetTextAsync(
                "/cgi-bin/configManager.cgi?action=getConfig&name=RecordMode", ct));
        }
        catch (NvrException)
        {
        }

        // The weekly schedule: table.Record[ch].TimeSection[day][n]="mask hh:mm:ss-hh:mm:ss",
        // day 0–6 Sunday–Saturday plus a holiday row at 7. Optional, like RecordMode.
        Dictionary<string, string>? record = null;
        if (withSchedules)
        {
            try
            {
                record = ParseKeyValues(await GetTextAsync(RecordConfigPath, ct));
            }
            catch (NvrException)
            {
            }
        }

        var streams = new List<CameraStream>();
        foreach (int index in IndexesInOrder(kv.Keys, "table.Encode["))
        {
            string prefix = $"table.Encode[{index}].MainFormat[0]";
            string V(string field) => kv.GetValueOrDefault($"{prefix}.Video.{field}", "");
            if (!kv.ContainsKey($"{prefix}.Video.BitRate"))
                continue;

            bool enabled = !string.Equals(kv.GetValueOrDefault($"{prefix}.VideoEnable", "true"),
                "false", StringComparison.OrdinalIgnoreCase);
            string? mode = recordMode.GetValueOrDefault($"table.RecordMode[{index}].Mode");
            if (mode == "2")
                enabled = false;
            var schedule = record is null ? null : ParseRecordSchedule(record, index, mode);
            string control = V("BitRateControl");
            int? bitrate = TryParseInt(V("BitRate"));
            foreach (int recType in RecordTypes.Skip(1))
            {
                int? other = TryParseInt(kv.GetValueOrDefault(
                    $"table.Encode[{index}].MainFormat[{recType}].Video.BitRate", ""));
                if (other is int o && (bitrate is null || o > bitrate))
                    bitrate = o;
            }
            bool isVbr = string.Equals(control, "VBR", StringComparison.OrdinalIgnoreCase);
            (int width, int height) = ParseResolution(V("Width"), V("Height"), V("resolution"));

            streams.Add(new CameraStream(
                Channel: index + 1,
                TrackId: index,
                Enabled: enabled,
                CodecType: V("Compression"),
                Width: width,
                Height: height,
                FrameRateFps: TryParseDouble(V("FPS")),
                QualityControlType: control.ToUpperInvariant(),
                VbrUpperCapKbps: isVbr ? bitrate : null,
                ConstantBitrateKbps: isVbr ? null : bitrate,
                FixedQuality: TryParseInt(V("Quality")),
                Schedule: schedule));
        }
        return streams.OrderBy(s => s.Channel).ToList();
    }

    /// <summary>
    /// One channel's weekly schedule from the Record table, or null when the table has no row
    /// for it. Verified on Site B (2026-09-02): <c>table.Record[ch].TimeSection[day][n]=
    /// "mask hh:mm:ss-hh:mm:ss"</c>, day 0–6 Sunday–Saturday and a holiday row at 7 (not read),
    /// a whole day written as 00:00:00-23:59:59, six sections per day of which the unused ones
    /// carry mask 0. The mask's bits are the record types that apply
    /// (<see cref="DescribeRecordMask"/>). <c>table.Record[ch].Enable</c> reads false on every
    /// recording channel and means nothing here; the switch is RecordMode — 1 (manual) forces
    /// continuous recording over the schedule, 2 (stop) switches the channel off.
    /// </summary>
    internal static RecordingSchedule? ParseRecordSchedule(Dictionary<string, string> record,
        int index, string? recordMode)
    {
        string prefix = $"table.Record[{index}].TimeSection[";
        var rows = record.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (rows.Count == 0)
            return null;
        switch (recordMode)
        {
            case "2":
                return RecordingSchedule.Off;
            case "1":
                return RecordingSchedule.Manual;
        }

        var spans = new List<RecordingSpan>();
        foreach (var (key, value) in rows)
        {
            // key: table.Record[0].TimeSection[3][1]
            string rest = key[prefix.Length..];
            int close = rest.IndexOf(']');
            if (close <= 0 || !int.TryParse(rest[..close], out int day) || day is < 0 or > 6)
                continue;
            if (!TryParseTimeSection(value, out int mask, out var start, out var end) ||
                mask == 0 || end <= start)
                continue;
            var (mode, triggers) = DescribeRecordMask(mask);
            spans.Add(new RecordingSpan((DayOfWeek)day, start, end, mode, triggers));
        }
        return new RecordingSchedule(RecordingState.Scheduled,
            spans.OrderBy(s => ((int)s.Day + 6) % 7).ThenBy(s => s.Start).ToList());
    }

    /// <summary>"39 00:00:00-23:59:59" → mask 39, 00:00–24:00 (Dahua writes a whole day as 23:59:59).</summary>
    internal static bool TryParseTimeSection(string? value, out int mask, out TimeSpan start,
        out TimeSpan end)
    {
        mask = 0;
        start = end = TimeSpan.Zero;
        var parts = (value ?? "").Trim().Split(' ', 2,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out mask))
            return false;
        var range = parts[1].Split('-', 2);
        if (range.Length != 2 ||
            !RecordingSchedule.TryParseTimeOfDay(range[0], out start) ||
            !RecordingSchedule.TryParseTimeOfDay(range[1], out end))
            return false;
        if (end == new TimeSpan(23, 59, 59))
            end = RecordingSchedule.EndOfDay;
        return true;
    }

    /// <summary>
    /// The record types in a schedule mask, joined "|" the way the recorder means them: any of
    /// them starts a recording. Bits 0–4 and 6 are documented — regular (continuous), motion,
    /// alarm, card, intelligent (IVS), POS; bit 5 is the remaining type the web UI offers,
    /// "MD&amp;Alarm", inferred from Site B's mask 39 = the four classic types. The
    /// words are the web UI's own checkbox labels ("Intel", "MD&amp;Alarm") except for General,
    /// which reads "Continuous" like every other vendor's. Bits the firmware adds later stay
    /// visible as "bit N".
    /// </summary>
    internal static (string Mode, RecordingTrigger Triggers) DescribeRecordMask(int mask)
    {
        var names = new List<string>();
        var triggers = RecordingTrigger.None;
        for (int bit = 0; bit < 31; bit++)
        {
            if ((mask & (1 << bit)) == 0)
                continue;
            var (name, trigger) = bit switch
            {
                0 => ("Continuous", RecordingTrigger.Continuous),
                1 => ("Motion", RecordingTrigger.Motion),
                2 => ("Alarm", RecordingTrigger.Alarm),
                3 => ("Card", RecordingTrigger.Other),
                4 => ("Intel", RecordingTrigger.Analytics),
                5 => ("MD&Alarm", RecordingTrigger.Motion | RecordingTrigger.Alarm),
                6 => ("POS", RecordingTrigger.Pos),
                _ => ($"bit {bit}", RecordingTrigger.Other),
            };
            names.Add(name);
            triggers |= trigger;
        }
        return (string.Join(" | ", names), triggers);
    }

    public async Task<DateTime?> FindOldestRecordingAsync(int channel,
        CancellationToken ct = default)
    {
        // mediaFileFind lists segments oldest-first (verified across pages on a
        // DH-NVR608H; condition.Order is ignored), so the first item of an everything-window
        // search is the oldest recording still on disk. The wide window costs ~5 s on the
        // first call per channel and well under a second after that.
        // One retry on a transport failure: a 51-camera pass on Site B saw the
        // recorder stop answering for one request after ~33 back-to-back wide searches
        // (HttpClient timeout, no HTTP status) and then carry on normally.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var first = await SearchFirstAsync(channel,
                    new DateTime(2000, 1, 1), new DateTime(2038, 1, 1), ct);
                return first?.Start;
            }
            catch (Exception ex) when (attempt == 0 && ex is not NvrException &&
                                        NvrException.IsPerDeviceFailure(ex, ct))
            {
            }
        }
    }

    /// <summary>
    /// One finder round trip returning the first segment the device lists for the window,
    /// or null when the window holds nothing. Same lifecycle as <see cref="SearchAsync"/>
    /// but stops after the first page of one.
    /// </summary>
    private async Task<RecordingSegment?> SearchFirstAsync(int channel, DateTime start,
        DateTime end, CancellationToken ct)
    {
        string createText = await GetTextAsync("/cgi-bin/mediaFileFind.cgi?action=factory.create", ct);
        string finder = ParseKeyValues(createText).GetValueOrDefault("result")
            ?? throw new NvrException("mediaFileFind factory.create returned no object id", createText);
        try
        {
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
                return null; // no recordings, or no camera on this channel (verified live)
            }
            if (!findResponse.Contains("OK", StringComparison.OrdinalIgnoreCase))
                return null;

            var kv = ParseKeyValues(await GetTextAsync(
                $"/cgi-bin/mediaFileFind.cgi?action=findNextFile&object={finder}&count=1", ct));
            if (!int.TryParse(kv.GetValueOrDefault("found"), out int found) || found == 0)
                return null;
            if (!TryParseCgiTime(kv.GetValueOrDefault("items[0].StartTime"), out var segStart) ||
                !TryParseCgiTime(kv.GetValueOrDefault("items[0].EndTime"), out var segEnd))
                return null;
            return new RecordingSegment
            {
                Channel = channel,
                Start = segStart,
                End = segEnd,
                NativeId = kv.GetValueOrDefault("items[0].FilePath"),
            };
        }
        finally
        {
            await ReleaseFinderAsync(finder);
        }
    }

    private async Task ReleaseFinderAsync(string finder)
    {
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await GetTextAsync($"/cgi-bin/mediaFileFind.cgi?action=close&object={finder}",
                cleanupCts.Token);
        }
        catch (Exception)
        {
        }
        try
        {
            await GetTextAsync($"/cgi-bin/mediaFileFind.cgi?action=destroy&object={finder}",
                cleanupCts.Token);
        }
        catch (Exception)
        {
        }
    }

    public async Task<BitrateRange?> GetBitrateRangeAsync(int channel,
        CancellationToken ct = default)
    {
        // encode.cgi?action=getConfigCaps answers caps[N].MainFormat[0].Video.BitRateOptions=
        // "min,max" (kbps). On the NVR firmware probed (DH-NVR608H-128-4KS3, 4.000.0000000.6)
        // the channel parameter is ignored and caps[N] is every channel, 0-based — read the
        // caller's own row. Older/camera firmware answers a single headMain.Video.… block.
        string text;
        try
        {
            text = await GetTextAsync(
                $"/cgi-bin/encode.cgi?action=getConfigCaps&channel={channel}", ct);
        }
        catch (NvrException)
        {
            return null;
        }
        var kv = ParseKeyValues(text);
        return ParseBitrateRange(kv, channel);
    }

    internal static BitrateRange? ParseBitrateRange(Dictionary<string, string> caps, int channel)
    {
        const string field = ".MainFormat[0].Video.BitRateOptions";
        if (caps.TryGetValue($"caps[{channel - 1}]{field}", out var own) &&
            TryParseBitrateOptions(own, out var range))
            return range;
        if (caps.TryGetValue("headMain.Video.BitRateOptions", out var head) &&
            TryParseBitrateOptions(head, out range))
            return range;
        // A single-row answer that honoured the channel parameter.
        var rows = caps.Where(kv => kv.Key.EndsWith(field, StringComparison.Ordinal)).ToList();
        if (rows.Count == 1 && TryParseBitrateOptions(rows[0].Value, out range))
            return range;
        return null;
    }

    internal static bool TryParseBitrateOptions(string value, out BitrateRange range)
    {
        range = default!;
        var numbers = new List<int>();
        foreach (var part in value.Split(new[] { ',', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0)
                numbers.Add(n);
        }
        if (numbers.Count < 2)
            return false;
        range = new BitrateRange(numbers.Min(), numbers.Max());
        return true;
    }

    public async Task<int> SetMaxBitrateAsync(int channel, int kbps,
        CancellationToken ct = default)
    {
        if (kbps <= 0)
            throw new ArgumentOutOfRangeException(nameof(kbps));

        int index = channel - 1;

        // Write every record type the channel has (General/Motion/Alarm): a cap on the
        // schedule stream alone would leave motion- or alarm-triggered footage recording at
        // the old rate, and the retention math would be wrong the moment anything moved.
        // setConfig takes several keys in one call (documented), so this is one round trip.
        var current = ParseKeyValues(await GetTextAsync(EncodeConfigPath, ct));
        var keys = RecordTypes
            .Select(t => $"Encode[{index}].MainFormat[{t}].Video.BitRate")
            .Where(k => current.ContainsKey("table." + k))
            .ToList();
        if (keys.Count == 0)
            throw new NvrException(
                $"channel {channel}: no main-stream bitrate field in the Encode config " +
                "(no camera bound to this channel?)");

        string path = "/cgi-bin/configManager.cgi?action=setConfig&" +
            string.Join("&", keys.Select(k => $"{k}={kbps}"));
        string reply = await GetTextAsync(path, ct);
        if (!reply.Contains("OK", StringComparison.OrdinalIgnoreCase))
            throw new NvrException(
                $"channel {channel}: the device rejected the bitrate write", reply);

        var readBack = (await ReadMainStreamsAsync(withSchedules: false, ct))
            .FirstOrDefault(s => s.Channel == channel);
        return readBack?.MaxBitrateKbps
            ?? throw new NvrException(
                $"channel {channel}: the write was accepted but the read-back shows no " +
                "bitrate — treat the channel's setting as unknown");
    }

    // ----- helpers -----

    /// <summary>Distinct <c>prefix[N]</c> indexes among the keys, in first-seen order.</summary>
    private static IEnumerable<int> IndexesInOrder(IEnumerable<string> keys, string prefix)
    {
        var seen = new SortedSet<int>();
        foreach (var key in keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            int close = key.IndexOf(']', prefix.Length);
            if (close > 0 && int.TryParse(key[prefix.Length..close], out int index))
                seen.Add(index);
        }
        return seen;
    }

    /// <summary>/dev/sda → 1, /dev/sdb → 2; anything else keeps its list position.</summary>
    internal static int BayNumber(string deviceName, int fallback)
    {
        const string prefix = "/dev/sd";
        if (deviceName.StartsWith(prefix, StringComparison.Ordinal) &&
            deviceName.Length > prefix.Length)
        {
            char letter = char.ToLowerInvariant(deviceName[prefix.Length]);
            if (letter is >= 'a' and <= 'z')
                return letter - 'a' + 1;
        }
        return fallback;
    }

    private static string MapDiskStatus(string state, bool anyError, long totalBytes)
    {
        if (anyError)
            return "error";
        if (state.Length == 0 && totalBytes == 0)
            return "notexist";
        return string.Equals(state, "Success", StringComparison.OrdinalIgnoreCase)
            ? "ok"
            : state.ToLowerInvariant();
    }

    private static (int Width, int Height) ParseResolution(string width, string height,
        string resolution)
    {
        int w = TryParseInt(width) ?? 0;
        int h = TryParseInt(height) ?? 0;
        if ((w == 0 || h == 0) && resolution.Contains('x'))
        {
            var parts = resolution.Split('x');
            if (parts.Length == 2)
            {
                w = TryParseInt(parts[0]) ?? w;
                h = TryParseInt(parts[1]) ?? h;
            }
        }
        return (w, h);
    }

    /// <summary>
    /// Byte counts arrive as floats on real firmware ("2495680086016.000000" on a
    /// DH-NVR608H-128-4KS3), integers in the spec — accept both, truncating.
    /// </summary>
    private static long ParseLong(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) &&
        v >= 0 && v <= long.MaxValue
            ? (long)v
            : 0;

    private static int? TryParseInt(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : null;

    private static double? TryParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0
            ? v
            : null;
}
