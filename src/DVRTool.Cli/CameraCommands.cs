using System.Globalization;
using DVRTool.Core;

namespace DVRTool.Cli;

/// <summary>
/// <c>dvrtool camera …</c> — the encoder settings of the cameras on a recorder: resolution,
/// frame rate, codec, CBR/VBR, bitrate, I-frame interval, audio and the channel's name.
/// </summary>
/// <remarks>
/// <para>
/// Hikvision only. The house gates are unchanged from <see cref="StorageCommands"/> and
/// <see cref="ConfigCommands"/>: dry run is the default, an explicit <c>--dry-run</c> beats
/// <c>--force</c>, the plan printed before the write is the same pure
/// <see cref="CameraSettingsPlan"/> the GUI's confirmation dialog is built from, every applied
/// write is read back, and what gets printed afterwards is what the recorder kept.
/// </para>
/// <para>
/// A value the camera never declared is <b>refused, never clamped</b>. Clamping would hand back
/// a success for a setting nobody chose, and a camera silently ignoring a resolution it does not
/// support is the exact failure the capabilities read exists to catch.
/// </para>
/// </remarks>
internal static class CameraCommands
{
    private const string Usage = """
        dvrtool camera — per-camera encoder settings (Hikvision only)

        Usage:
          dvrtool camera show    [--channel <n>] [--stream main|sub|third] [connection options]
          dvrtool camera options  --channel <n>  [--stream main|sub|third] [connection options]
          dvrtool camera probe   [--channel <n>] [connection options]
          dvrtool camera set     [--channel <n> | --all] [--stream main|sub|third]
                                 [--resolution <WxH>] [--fps <n> | --fps full]
                                 [--codec H.264|H.265] [--quality cbr|vbr] [--kbps <k>]
                                 [--gop <n>] [--audio on|off] [--name <text>]
                                 [--ignore-pins] [--dry-run] [--force] [connection options]

        Subcommands:
          show      Every camera's encoder settings, one row per stream track. --channel
                    limits it to one camera, --stream to one track.
          options   What one track declares it will accept — the resolutions, frame rates,
                    codecs and bitrate range the camera itself lists. These are the values
                    `set` will allow; anything else is refused rather than clamped.
          probe     Read-only diagnostic: dumps the raw endpoint set behind this command
                    for one channel, with what answered and what did not. For working out
                    what a new firmware exposes before trusting a write to it.
          set       Change one camera, or every camera with --all. DRY RUN by default —
                    prints the per-camera before → after and stops. --force applies it,
                    reading each channel back and reporting what the recorder kept.

        Notes:
          --fps full sets "Full Frame Rate" (the camera follows its own maximum, which the
          firmware re-resolves whenever the resolution changes). It is a different setting
          from a number that happens to equal today's rate, so the two cannot be combined.

          --name renames the channel. It is written to whichever list holds the name — the
          IP-camera list on an NVR, the video-input list on a DVR — found by reading, never
          assumed, because a rename aimed at the wrong list is accepted and does nothing.

          A camera pinned in the retention planner (dvrtool storage pin) is held back from a
          bitrate change and reported. --ignore-pins plans as if nothing were pinned and,
          like `storage plan`, cannot be combined with --force.

        Connection options are the same as every other command (--host/--user/--pass or
        DVR_HOST/DVR_USER/DVR_PASS, --tls, --expect-serial, …).
        """;

    /// <summary>
    /// Handles help and the no-subcommand path, which must run before a connection is built —
    /// `dvrtool camera --help` cannot demand a --host.
    /// </summary>
    internal static bool TryRunHelp(string subcommand, Dictionary<string, string> opts,
        out int exitCode)
    {
        exitCode = 0;
        if (subcommand.Length > 0 && subcommand != "help" && !opts.ContainsKey("help"))
            return false;
        Console.WriteLine(Usage);
        exitCode = subcommand.Length == 0 && !opts.ContainsKey("help") ? 2 : 0;
        return true;
    }

    internal static async Task<int> RunAsync(INvrClient client, string subcommand,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        if (client is not ICameraSettingsClient camera)
        {
            Console.Error.WriteLine(
                $"error: camera settings aren't implemented for {client.Vendor} devices.");
            return 2;
        }

        return subcommand switch
        {
            "show" => await ShowAsync(client, camera, opts, ct),
            "options" => await OptionsAsync(camera, opts, ct),
            "probe" => await ProbeAsync(client, camera, opts, ct),
            "set" => await SetAsync(client, camera, opts, ct),
            _ => UnknownSubcommand(subcommand),
        };
    }

    private static int UnknownSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"error: unknown camera subcommand '{subcommand}'\n");
        Console.WriteLine(Usage);
        return 2;
    }

    // ----- show -----

    private static async Task<int> ShowAsync(INvrClient client, ICameraSettingsClient camera,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        var tracks = await camera.GetEncodingAsync(ct);
        var names = await ChannelNamesAsync(client, ct);

        if (ParseChannel(opts) is int only)
            tracks = tracks.Where(t => t.Channel == only).ToList();
        if (opts.ContainsKey("stream"))
        {
            var stream = ParseStream(opts);
            tracks = tracks.Where(t => t.Stream == stream).ToList();
        }

        if (tracks.Count == 0)
        {
            Console.WriteLine("No matching stream tracks — check --channel and --stream.");
            return 0;
        }

        Console.WriteLine($"{"CH",3}  {"NAME",-20}  {"STREAM",-6}  {"RESOLUTION",-11}  " +
            $"{"FPS",-11}  {"CODEC",-6}  {"MODE",-4}  {"KBPS",7}  {"GOP",4}  AUDIO");
        foreach (var t in tracks)
        {
            Console.WriteLine(
                $"{t.Channel,3}  {Fit(names.GetValueOrDefault(t.Channel, ""), 20),-20}  " +
                $"{t.StreamText,-6}  {Fit(t.Resolution, 11),-11}  {Fit(t.FrameRateText, 11),-11}  " +
                $"{Fit(t.CodecType, 6),-6}  {Fit(t.QualityControlType, 4),-4}  " +
                $"{t.BitrateKbps?.ToString() ?? "?",7}  {t.GovLength?.ToString() ?? "?",4}  " +
                $"{Describe(t.AudioEnabled)}");
        }

        int off = tracks.Count(t => !t.Enabled);
        if (off > 0)
            Console.WriteLine($"\n{off} track(s) are disabled on the recorder.");
        return 0;
    }

    // ----- options -----

    private static async Task<int> OptionsAsync(ICameraSettingsClient camera,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        if (ParseChannel(opts) is not int channel)
        {
            Console.Error.WriteLine("error: --channel <n> is required for `camera options`.");
            return 2;
        }
        var stream = ParseStream(opts);

        var declared = await camera.GetEncodingOptionsAsync(channel, stream, ct);
        if (declared is null)
        {
            // Not the same as "declares nothing", and the difference decides whether a front
            // end offers a list or a free entry.
            Console.WriteLine(
                $"ch{channel} ({stream.ToString().ToLowerInvariant()}): this recorder answers " +
                "no capabilities document for the channel, so nothing can be bounded. Values " +
                "will be sent as typed and the read-back is the only check.");
            return 0;
        }
        if (declared.IsEmpty)
        {
            Console.WriteLine(
                $"ch{channel} ({stream.ToString().ToLowerInvariant()}): the capabilities " +
                "document exists but declares no limits at all — every field is free entry.");
            return 0;
        }

        Console.WriteLine($"ch{channel} ({stream.ToString().ToLowerInvariant()}) accepts:");
        Report("resolutions", declared.Resolutions.Select(r => r.ToString()));
        Report("frame rates",
            declared.FrameRates.Select(f => f.ToString("0.##", CultureInfo.InvariantCulture))
                .Concat(declared.SupportsFullFrameRate ? ["full"] : []));
        Report("codecs", declared.Codecs);
        Report("quality modes", declared.QualityControlTypes);
        if (declared.Bitrate is { } rate)
            Console.WriteLine($"  bitrate        {rate.MinKbps}–{rate.MaxKbps} kbps");
        if (declared.GovLength is { } gov)
            Console.WriteLine($"  I-frame gap    {gov.Text}");
        return 0;

        static void Report(string label, IEnumerable<string> values)
        {
            var list = values.ToList();
            if (list.Count > 0)
                Console.WriteLine($"  {label,-14} {string.Join(", ", list)}");
        }
    }

    // ----- probe -----

    private static async Task<int> ProbeAsync(INvrClient client, ICameraSettingsClient camera,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        int channel = ParseChannel(opts) ?? 1;
        Console.WriteLine($"Probing ch{channel} — read-only, nothing is written.\n");

        var tracks = await camera.GetEncodingAsync(ct);
        var mine = tracks.Where(t => t.Channel == channel).ToList();
        Console.WriteLine($"/ISAPI/Streaming/channels — {tracks.Count} track(s) total, " +
            $"{mine.Count} on ch{channel}: " +
            (mine.Count == 0 ? "none" : string.Join(", ", mine.Select(t => t.StreamText))));

        foreach (var track in mine)
        {
            var declared = await camera.GetEncodingOptionsAsync(channel, track.Stream, ct);
            string verdict = declared is null ? "no document (403/404)"
                : declared.IsEmpty ? "answers, declares nothing"
                : $"resolutions {declared.Resolutions.Count}, rates {declared.FrameRates.Count}" +
                  $"{(declared.SupportsFullFrameRate ? " (+full)" : "")}, " +
                  $"codecs {declared.Codecs.Count}, " +
                  $"bitrate {(declared.Bitrate is { } r ? $"{r.MinKbps}–{r.MaxKbps}" : "—")}, " +
                  $"GOP {(declared.GovLength is { } g ? g.Text : "—")}";
            Console.WriteLine(
                $"  …/{track.TrackId}/capabilities — {verdict}");
            Console.WriteLine(
                $"  …/{track.TrackId} — {track.Resolution} {track.FrameRateText} " +
                $"{track.CodecType} {track.QualityControlType} " +
                $"{track.BitrateKbps?.ToString() ?? "?"} kbps, GOP " +
                $"{track.GovLength?.ToString() ?? "—"}, audio {Describe(track.AudioEnabled)}" +
                $"{(track.SmartCodec is { Length: > 0 } sc ? $", smartCodec {sc}" : "")}");
        }

        var identity = await camera.GetChannelIdentityAsync(channel, ct);
        Console.WriteLine(identity is null
            ? $"\nname — neither the IP-camera list nor the video-input list carries one for " +
              $"ch{channel}. A rename would be refused."
            : $"\nname — '{identity.Name}', from the " +
              (identity.Source == ChannelNameSource.InputProxy
                  ? "IP-camera list (/ISAPI/ContentMgmt/InputProxy/channels)"
                  : "video-input list (/ISAPI/System/Video/inputs/channels)"));
        return 0;
    }

    // ----- set -----

    private static async Task<int> SetAsync(INvrClient client, ICameraSettingsClient camera,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        if (camera is not ICameraSettingsWriter writer)
        {
            Console.Error.WriteLine(
                $"error: camera settings are read-only for {client.Vendor} devices.");
            return 2;
        }

        bool all = opts.ContainsKey("all");
        int? channel = ParseChannel(opts);
        if (all == (channel is not null))
        {
            Console.Error.WriteLine(
                "error: name the cameras to change — either --channel <n> or --all.");
            return 2;
        }

        var stream = ParseStream(opts);
        EncodingSettings requested;
        try
        {
            requested = ParseSettings(opts);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        string? newName = opts.GetValueOrDefault("name");
        if (requested.IsEmpty && string.IsNullOrWhiteSpace(newName))
        {
            Console.Error.WriteLine(
                "error: nothing to change — pass at least one of --resolution, --fps, " +
                "--codec, --quality, --kbps, --gop, --audio or --name.");
            return 2;
        }
        if (newName is not null && all)
        {
            // Every camera sharing one name is never what somebody meant, and it would undo
            // the labelling the rest of the product reads back.
            Console.Error.WriteLine(
                "error: --name renames one channel; it cannot be combined with --all.");
            return 2;
        }

        // Dry run is the default and an explicit --dry-run wins over --force, the same gate
        // every other write in this CLI uses.
        bool force = opts.ContainsKey("force") && !opts.ContainsKey("dry-run");
        bool ignorePins = opts.ContainsKey("ignore-pins");
        if (ignorePins && force)
        {
            Console.Error.WriteLine("error: --ignore-pins is a dry-run view; it cannot be " +
                "combined with --force. Unpin what should change, then set it again.");
            return 2;
        }

        var tracks = (await writer.GetEncodingAsync(ct))
            .Where(t => t.Stream == stream && (all || t.Channel == channel))
            .ToList();
        if (tracks.Count == 0)
        {
            Console.Error.WriteLine(all
                ? $"error: this recorder reports no {stream.ToString().ToLowerInvariant()} " +
                  "stream tracks at all."
                : $"error: ch{channel} has no {stream.ToString().ToLowerInvariant()} stream.");
            return 2;
        }

        var names = await ChannelNamesAsync(client, ct);

        // What each camera declares it will accept, so a refusal names the camera's own list
        // rather than a rule of ours.
        var declared = new Dictionary<int, EncodingOptions>();
        foreach (var track in tracks)
        {
            if (await writer.GetEncodingOptionsAsync(track.Channel, track.Stream, ct) is { } o)
                declared[track.TrackId] = o;
        }

        // Keyed exactly as the retention planner keys it — host:port plus the serial the
        // identity check already pinned — so the two features cannot disagree about which
        // cameras an operator protected.
        string address = DeviceIdentityGuard.AddressOf(client.Connection);
        var pins = ChannelPinStore.Default.Get(address,
            DeviceIdentityStore.Default.Pinned(address)?.Serial);
        var plan = CameraSettingsPlan.Build(tracks, requested, declared, names,
            ignorePins ? null : pins, ignorePins);

        Console.WriteLine(plan.Summary);
        if (ignorePins)
            Console.WriteLine("(--ignore-pins: planned as if nothing were pinned.)");
        Console.WriteLine();
        foreach (var one in plan.Channels)
            Console.WriteLine($"  {one.Describe()}");

        foreach (var held in plan.HeldBack)
            Console.Error.WriteLine($"warning: ch{held.Channel} was not planned — {held.Problem}");

        bool renames = newName is not null;
        if (plan.WriteCount == 0 && !renames)
        {
            Console.WriteLine("\nNothing to write.");
            return 0;
        }

        if (!force)
        {
            Console.WriteLine($"\nDRY RUN — nothing was written ({plan.WriteCount} channel(s) " +
                (renames ? "plus a rename " : "") + "would change). Re-run with --force to apply.");
            return 0;
        }

        Console.WriteLine($"\nApplying to {plan.WriteCount} channel(s) …");
        var failures = new List<string>();
        int changed = 0, rejected = 0;

        foreach (var one in plan.Writes)
        {
            try
            {
                var result = await writer.SetEncodingAsync(one.Channel, one.Stream, requested, ct);
                if (result.Rejected)
                {
                    rejected++;
                    Console.Error.WriteLine($"  ch{one.Channel}: {result.Note}");
                }
                else
                {
                    changed++;
                    Console.WriteLine($"  ch{one.Channel}: written and verified by read-back.");
                }
            }
            catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
            {
                // One camera refusing must not abandon the rest of the batch — but it is
                // reported, never counted as done.
                failures.Add($"ch{one.Channel}: {ex.Message}");
                Console.Error.WriteLine($"  ch{one.Channel}: FAILED — {ex.Message}");
            }
        }

        if (newName is not null && channel is int renameChannel)
        {
            try
            {
                var result = await writer.SetChannelNameAsync(renameChannel, newName, ct);
                if (result.Rejected)
                {
                    rejected++;
                    Console.Error.WriteLine($"  ch{renameChannel}: {result.Note}");
                }
                else if (result.Changed)
                {
                    changed++;
                    Console.WriteLine($"  ch{renameChannel}: renamed to '{result.Name}'.");
                }
                else
                {
                    Console.WriteLine($"  ch{renameChannel}: {result.Note}");
                }
            }
            catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
            {
                failures.Add($"ch{renameChannel} rename: {ex.Message}");
                Console.Error.WriteLine($"  ch{renameChannel}: rename FAILED — {ex.Message}");
            }
        }

        Console.WriteLine($"\n{changed} applied, {rejected} kept by the recorder, " +
            $"{failures.Count} failed.");
        // A recorder that holds its own value is not an error, but it is never a success
        // either: the caller has to be able to tell from the exit code.
        return failures.Count > 0 || rejected > 0 ? 1 : 0;
    }

    // ----- parsing -----

    private static EncodingSettings ParseSettings(Dictionary<string, string> opts)
    {
        Resolution? resolution = null;
        if (opts.GetValueOrDefault("resolution") is { Length: > 0 } res)
            resolution = Resolution.Parse(res)
                ?? throw new ArgumentException(
                    $"can't read resolution '{res}' — write it as WIDTHxHEIGHT, e.g. 1920x1080");

        double? fps = null;
        bool? fullRate = null;
        if (opts.GetValueOrDefault("fps") is { Length: > 0 } rate)
        {
            if (string.Equals(rate, "full", StringComparison.OrdinalIgnoreCase))
                fullRate = true;
            else if (double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture,
                         out double parsed) && parsed > 0)
                fps = parsed;
            else
                throw new ArgumentException(
                    $"can't read frame rate '{rate}' — give a number, or 'full' for Full " +
                    "Frame Rate");
        }

        string? quality = opts.GetValueOrDefault("quality");
        if (quality is { Length: > 0 })
        {
            quality = quality.ToLowerInvariant() switch
            {
                "cbr" => "CBR",
                "vbr" => "VBR",
                _ => throw new ArgumentException(
                    $"unknown quality mode '{quality}' (use cbr or vbr)"),
            };
        }

        int? kbps = ParseInt(opts, "kbps");
        if (kbps is <= 0)
            throw new ArgumentException("--kbps must be greater than zero");

        int? gop = ParseInt(opts, "gop");
        if (gop is <= 0)
            throw new ArgumentException("--gop must be greater than zero");

        bool? audio = opts.GetValueOrDefault("audio")?.ToLowerInvariant() switch
        {
            null => null,
            "on" or "true" or "1" => true,
            "off" or "false" or "0" => false,
            var other => throw new ArgumentException(
                $"unknown audio setting '{other}' (use on or off)"),
        };

        return new EncodingSettings(
            Resolution: resolution,
            FrameRateFps: fps,
            FullFrameRate: fullRate,
            CodecType: opts.GetValueOrDefault("codec") is { Length: > 0 } c ? c : null,
            QualityControlType: quality is { Length: > 0 } ? quality : null,
            BitrateKbps: kbps,
            GovLength: gop,
            AudioEnabled: audio);
    }

    private static int? ParseInt(Dictionary<string, string> opts, string name)
    {
        if (opts.GetValueOrDefault(name) is not { Length: > 0 } text)
            return null;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            : throw new ArgumentException($"--{name} must be a whole number (got '{text}')");
    }

    private static int? ParseChannel(Dictionary<string, string> opts) =>
        opts.GetValueOrDefault("channel") is { Length: > 0 } text &&
        int.TryParse(text, out int channel)
            ? channel
            : null;

    private static StreamType ParseStream(Dictionary<string, string> opts) =>
        opts.GetValueOrDefault("stream", "main").ToLowerInvariant() switch
        {
            "main" or "0" => StreamType.Main,
            "sub" or "1" => StreamType.Sub,
            "third" or "2" => StreamType.Third,
            var s => throw new ArgumentException(
                $"unknown stream '{s}' (use main, sub or third)"),
        };

    /// <summary>
    /// Channel names for the report. Best effort: a recorder that will not list its channels
    /// still has editable encoder settings, and a blank name column is better than a refusal.
    /// </summary>
    private static async Task<Dictionary<int, string>> ChannelNamesAsync(INvrClient client,
        CancellationToken ct)
    {
        try
        {
            return (await client.GetChannelsAsync(ct))
                .ToDictionary(c => c.Id, c => c.Name);
        }
        catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
        {
            return [];
        }
    }

    private static string Describe(bool? value) =>
        value is null ? "?" : value.Value ? "on" : "off";

    private static string Fit(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "…";
}
