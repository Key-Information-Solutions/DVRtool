using DVRTool.Core;

namespace DVRTool.Cli;

/// <summary>
/// <c>dvrtool recording …</c> — the per-camera switches that decide which tracks reach the
/// disk: whether the recorder archives the secondary stream, and whether it records audio.
/// </summary>
/// <remarks>
/// Same house gate as <see cref="StorageCommands"/>: dry run is the default, an explicit
/// <c>--dry-run</c> beats <c>--force</c>, every applied write is read back, and what gets
/// printed is what the recorder now holds. One rule of its own — turning the secondary stream
/// off on a camera whose schedule records "motion &amp; low-res always" **deletes that
/// camera's continuous coverage**, because the low-res half of that mode *is* the secondary
/// stream. Those cameras are refused unless <c>--include-lowres-always</c> says otherwise, and
/// the dry run names every one of them first.
/// </remarks>
internal static class RecordingCommands
{
    private const string Usage = """
        dvrtool recording — which tracks reach the disk (DW Spectrum / Nx Witness, --vendor nx)

        Usage:
          dvrtool recording show [connection options]
          dvrtool recording set  [--secondary on|off] [--audio on|off]
                                 [--record-audio on|off]
                                 [--channel <n> | --all] [--include-lowres-always]
                                 [--force] [connection options]

        Subcommands:
          show   Every camera's switches: whether the recorder archives the secondary
                 (low-quality) stream alongside the primary, whether audio is captured, and
                 whether audio is barred from the archive. The RECORDING column is the
                 camera's schedule mode, because the two interact — see the warning below.
          set    Change either switch. DRY RUN by default: prints the per-camera
                 before → after and stops. --force applies it, camera by camera, reading
                 each one back. --channel <n> does one camera, --all does every camera the
                 recorder knows.

        Options:
          --secondary on|off   Archive the secondary stream, or stop archiving it. "off" is
                               the storage saver: on Nx the low-res stream is written to disk
                               alongside the primary by default.
          --audio on|off       Capture audio — the General tab's "Enable audio". Off means
                               the server does not pull the camera's audio at all.
          --record-audio on|off
                               Whether audio may be archived — the Expert tab's "Do not
                               record audio", inverted so both options read the same way.
                               "off" sets that checkbox. This is the DURABLE one: it keeps
                               audio off the disk even if somebody later enables capture,
                               so "--audio off --record-audio off" is belt and braces.
          --include-lowres-always
                               Allow --secondary off on cameras whose schedule is "motion &
                               low-res always". Without it those cameras are listed and
                               skipped. Read the warning before using it.
          --all                Every camera. Required to write more than one at a time —
                               there is deliberately no "all cameras" default.
          --force              Apply. Without it nothing is written.

        WARNING — the secondary stream is not spare capacity on every camera. A camera whose
        schedule mode is "motion & low-res always" records the primary on motion and the
        SECONDARY continuously; that low-res track is the only thing covering the gaps
        between motion events. Turning the secondary stream off on such a camera does not
        shrink its footage, it turns the camera into motion-only and leaves the quiet hours
        with no footage at all. On a camera that records continuously ("always") or on motion
        alone, the secondary stream is pure overhead and turning it off is free.

        Turning the secondary stream off does NOT disable dual streaming: the recorder keeps
        pulling and analysing the second stream, so motion detection is unaffected.

        Connection options are the same as every other command (--host/--user/--pass or
        DVR_HOST/DVR_USER/DVR_PASS, --tls, --expect-serial, …).
        """;

    /// <summary>Help paths, which run before a connection is built.</summary>
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
        if (client is not IRecordingOptionsClient recording)
        {
            Console.Error.WriteLine(
                $"error: per-camera stream and audio switches aren't implemented for " +
                $"{client.Vendor} devices.");
            return 2;
        }

        return subcommand switch
        {
            "show" => await ShowAsync(client, recording, ct),
            "set" => await SetAsync(client, recording, opts, ct),
            _ => UnknownSubcommand(subcommand),
        };
    }

    private static int UnknownSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"error: unknown recording subcommand '{subcommand}'\n");
        Console.WriteLine(Usage);
        return 2;
    }

    // ----- show -----

    private static async Task<int> ShowAsync(
        INvrClient client, IRecordingOptionsClient recording, CancellationToken ct)
    {
        var rows = await recording.GetRecordingOptionsAsync(ct);
        var modes = await ReadSchedulesAsync(client, ct);

        Console.WriteLine(
            $"{"CH",3}  {"CAMERA",-34}  {"SECONDARY",-12}  {"CAPTURE",-10}  {"ARCHIVE",-8}  RECORDING");
        foreach (var r in rows)
        {
            string secondary = r.RecordSecondary switch
            {
                true => "recorded",
                false => "not recorded",
                null => "—",
            };
            string audio = (r.AudioEnabled, r.AudioSupported) switch
            {
                (true, _) => "on",
                (false, true) => "off",
                (false, false) => "off (none)",
                (null, _) => "—",
            };
            string archive = r.AudioRecordingBlocked switch
            {
                true => "barred",
                false => "allowed",
                null => "—",
            };
            modes.TryGetValue(r.Channel, out string? mode);
            Console.WriteLine(
                $"{r.Channel,3}  {Truncate(r.Name, 34),-34}  {secondary,-12}  {audio,-10}  " +
                $"{archive,-8}  {mode ?? ""}");
        }

        int barred = rows.Count(r => r.AudioRecordingBlocked is true);
        int archiving = rows.Count(r => r.RecordSecondary is true);
        int lowres = rows.Count(r => r.RecordSecondary is true && IsLowResAlways(modes, r.Channel));
        Console.WriteLine();
        Console.WriteLine(
            $"{rows.Count} cameras; {archiving} archiving the secondary stream; " +
            $"{barred} with audio barred from the archive.");
        if (lowres > 0)
            Console.WriteLine(
                $"{lowres} of those record \"motion & low-res always\" — their secondary stream " +
                "is their continuous coverage, not overhead. See `dvrtool recording --help`.");
        return 0;
    }

    // ----- set -----

    private static async Task<int> SetAsync(INvrClient client, IRecordingOptionsClient recording,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        if (!TryReadSwitch(opts, "secondary", out bool? secondary) ||
            !TryReadSwitch(opts, "audio", out bool? audio) ||
            !TryReadSwitch(opts, "record-audio", out bool? recordAudio))
            return 2;
        if (secondary is null && audio is null && recordAudio is null)
        {
            Console.Error.WriteLine(
                "error: nothing to set — give --secondary on|off, --audio on|off, " +
                "--record-audio on|off, or any combination.");
            return 2;
        }

        // The CLI speaks in "may audio be recorded"; the device property is the negative.
        bool? blockAudio = recordAudio is bool allowed ? !allowed : null;

        bool all = opts.ContainsKey("all");
        bool hasChannel = opts.TryGetValue("channel", out string? channelText);
        if (all == hasChannel)
        {
            Console.Error.WriteLine(
                "error: give either --channel <n> for one camera or --all for every camera.");
            return 2;
        }

        // Dry run is the default, and an explicit --dry-run wins over --force when both are
        // given — the same gate the storage writes use.
        bool force = opts.ContainsKey("force") && !opts.ContainsKey("dry-run");
        bool includeLowRes = opts.ContainsKey("include-lowres-always");

        var rows = await recording.GetRecordingOptionsAsync(ct);
        if (hasChannel)
        {
            if (!int.TryParse(channelText, out int channel) ||
                rows.All(r => r.Channel != channel))
            {
                Console.Error.WriteLine(
                    $"error: --channel must be one of the {rows.Count} channels this recorder has.");
                return 2;
            }
            rows = rows.Where(r => r.Channel == channel).ToList();
        }

        var modes = await ReadSchedulesAsync(client, ct);

        // Split the work three ways before touching anything: what would change, what is
        // already where it was asked to be, and what is being held back by the low-res rule.
        var planned = new List<CameraRecordingOptions>();
        var unchanged = new List<CameraRecordingOptions>();
        var withheld = new List<CameraRecordingOptions>();
        foreach (var r in rows)
        {
            bool wantsSecondaryOff = secondary is false && r.RecordSecondary is true;
            if (wantsSecondaryOff && IsLowResAlways(modes, r.Channel) && !includeLowRes)
            {
                withheld.Add(r);
                continue;
            }
            bool changes =
                (secondary is bool s && r.RecordSecondary is bool cur && cur != s) ||
                (audio is bool a && r.AudioEnabled is bool now && now != a) ||
                (blockAudio is bool b && (r.AudioRecordingBlocked ?? false) != b);
            (changes ? planned : unchanged).Add(r);
        }

        if (withheld.Count > 0)
        {
            Console.WriteLine(
                $"HELD BACK — {withheld.Count} camera(s) record \"motion & low-res always\". Their");
            Console.WriteLine(
                "secondary stream IS their continuous coverage: turning it off makes them");
            Console.WriteLine(
                "motion-only and leaves the quiet hours with no footage. Pass");
            Console.WriteLine(
                "--include-lowres-always to change them anyway.");
            foreach (var r in withheld)
                Console.WriteLine($"  {r.Channel,3}  {r.Name}");
            Console.WriteLine();
        }

        if (planned.Count == 0)
        {
            Console.WriteLine(unchanged.Count > 0
                ? $"Nothing to do: all {unchanged.Count} selected camera(s) are already set that way."
                : "Nothing to do.");
            return 0;
        }

        Console.WriteLine($"{"CH",3}  {"CAMERA",-34}  {"NOW",-26}  → WANTED");
        foreach (var r in planned)
            Console.WriteLine(
                $"{r.Channel,3}  {Truncate(r.Name, 34),-34}  {r.Summary,-26}  → {Wanted(secondary, audio, blockAudio)}");
        if (unchanged.Count > 0)
            Console.WriteLine($"\n({unchanged.Count} more already set that way — left alone.)");

        if (!force)
        {
            Console.WriteLine(
                $"\nDRY RUN — nothing was written ({planned.Count} camera(s) would change). " +
                "Re-run with --force to apply.");
            return 0;
        }

        Console.WriteLine();
        int applied = 0, failed = 0;
        foreach (var r in planned)
        {
            try
            {
                var change = await recording.SetRecordingOptionsAsync(
                    r.Channel, secondary, audio, blockAudio, ct);
                string note = change.Note.Length > 0 ? $"  ({change.Note})" : "";
                if (change.Rejected)
                {
                    failed++;
                    Console.Error.WriteLine(
                        $"{r.Channel,3}  {Truncate(r.Name, 34),-34}  REJECTED{note}");
                }
                else
                {
                    if (change.Changed)
                        applied++;
                    Console.WriteLine(
                        $"{r.Channel,3}  {Truncate(r.Name, 34),-34}  {change.After.Summary}{note}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                Console.Error.WriteLine(
                    $"{r.Channel,3}  {Truncate(r.Name, 34),-34}  FAILED: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{applied} camera(s) changed, {failed} failed.");
        if (withheld.Count > 0)
            Console.WriteLine($"{withheld.Count} held back by the low-res rule (see above).");
        return failed > 0 ? 1 : 0;
    }

    // ----- helpers -----

    /// <summary>
    /// The schedule mode per channel, for the RECORDING column and the low-res rule. Read
    /// through <see cref="IStorageClient"/>, which already parses the schedule; a recorder
    /// that does not implement it simply gets no modes and no low-res rule, rather than a
    /// failure — the switches themselves are still writable.
    /// </summary>
    private static async Task<Dictionary<int, string>> ReadSchedulesAsync(
        INvrClient client, CancellationToken ct)
    {
        if (client is not IStorageClient storage)
            return [];
        try
        {
            var streams = await storage.GetMainStreamsAsync(ct);
            return streams
                .Where(s => s.Schedule is not null)
                .ToDictionary(s => s.Channel, s => s.Schedule!.Summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether this camera's schedule leans on the secondary stream for continuous coverage.
    /// Matched on the mode text the vendor layer produces — Nx's combined cell is
    /// "Motion &amp; low-res always" — and deliberately generous: an unrecognised mode is
    /// treated as safe to change, but anything mentioning low-res is not.
    /// </summary>
    private static bool IsLowResAlways(Dictionary<int, string> modes, int channel) =>
        modes.TryGetValue(channel, out string? mode) &&
        mode.Contains("low-res", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads an <c>on</c>/<c>off</c> option; absent leaves the switch alone.</summary>
    private static bool TryReadSwitch(
        Dictionary<string, string> opts, string name, out bool? value)
    {
        value = null;
        if (!opts.TryGetValue(name, out string? text))
            return true;
        switch (text.Trim().ToLowerInvariant())
        {
            case "on" or "true" or "yes" or "1":
                value = true;
                return true;
            case "off" or "false" or "no" or "0":
                value = false;
                return true;
            default:
                Console.Error.WriteLine($"error: --{name} takes 'on' or 'off', not '{text}'.");
                return false;
        }
    }

    private static string Wanted(bool? secondary, bool? audio, bool? blockAudio)
    {
        var parts = new List<string>(3);
        if (secondary is bool s)
            parts.Add(s ? "records secondary" : "no secondary");
        if (audio is bool a)
            parts.Add(a ? "capture on" : "capture off");
        if (blockAudio is bool b)
            parts.Add(b ? "audio barred" : "audio allowed");
        return string.Join(", ", parts);
    }

    private static string Truncate(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "…";
}
