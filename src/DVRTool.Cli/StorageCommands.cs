using System.Globalization;
using DVRTool.Core;

namespace DVRTool.Cli;

/// <summary>
/// <c>dvrtool storage …</c> — disk inventory, retention reads and the bitrate planner.
/// </summary>
/// <remarks>
/// Reads are plain; the one write (<c>plan --force</c>) follows the house gate: dry-run is
/// the default and wins over <c>--force</c> when both are given, every applied write is
/// read back, and the numbers reported are what the device now says — not what was asked.
/// Identity was already verified by Program before this runs.
/// </remarks>
internal static class StorageCommands
{
    private const string Usage = """
        dvrtool storage — disks, retention and bitrate planning (Hikvision, Dahua, and
        DW Spectrum / Nx Witness with --vendor nx)

        Usage:
          dvrtool storage disks       [connection options]
          dvrtool storage retention   [--channel <n>] [connection options]
          dvrtool storage plan --days <n> [--force] [connection options]
          dvrtool storage set --channel <n> --kbps <k> [--force] [connection options]

        Subcommands:
          disks       Disk inventory: per-bay model/serial/status/capacity, work mode,
                      and how many disks the firmware supports.
          retention   Per-camera recording settings and the oldest footage still on
                      disk — the "how many days are we actually holding" report.
                      --channel limits it to one camera.
          plan        "We need X days": compute the uniform per-camera max bitrate that
                      fits --days into the installed disks. DRY RUN by default — prints
                      the per-camera before → after and what the plan actually achieves.
                      --force applies it camera by camera, reading each value back.
          set         Set one camera's max recording bitrate. DRY RUN by default;
                      --force writes it and reports what the device kept.

        Estimates are worst-case on purpose: they assume every camera records at its
        configured maximum around the clock. VBR + smart codecs usually do better, so
        real retention lands at or above the estimate. On Nx the "max" is what the
        busiest schedule cell asks for, and the totals include the secondary stream
        Nx archives alongside the main one (marked + in the table).

        Connection options are the same as every other command (--host/--user/--pass or
        DVR_HOST/DVR_USER/DVR_PASS, --tls, --expect-serial, …).
        """;

    /// <summary>
    /// Handles the help/no-subcommand paths, which must run before any connection is
    /// built — `dvrtool storage --help` cannot demand a --host.
    /// </summary>
    internal static bool TryRunHelp(string subcommand, Dictionary<string, string> opts,
        out int exitCode)
    {
        exitCode = 0;
        if (subcommand.Length > 0 && subcommand != "help" && !opts.ContainsKey("help"))
            return false;
        Console.WriteLine(Usage);
        // A bare `dvrtool storage` is a usage error; an explicit --help is not.
        exitCode = subcommand.Length == 0 && !opts.ContainsKey("help") ? 2 : 0;
        return true;
    }

    internal static async Task<int> RunAsync(INvrClient client, string subcommand,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        if (client is not IStorageClient storage)
        {
            Console.Error.WriteLine(
                $"error: storage management isn't implemented for {client.Vendor} devices.");
            return 2;
        }

        return subcommand switch
        {
            "disks" => await DisksAsync(storage, ct),
            "retention" => await RetentionAsync(client, storage, opts, ct),
            "plan" => await PlanAsync(client, storage, opts, ct),
            "set" => await SetAsync(storage, opts, ct),
            _ => UnknownSubcommand(subcommand),
        };
    }

    private static int UnknownSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"error: unknown storage subcommand '{subcommand}'\n");
        Console.WriteLine(Usage);
        return 2;
    }

    // ----- disks -----

    private static async Task<int> DisksAsync(IStorageClient storage, CancellationToken ct)
    {
        var info = await storage.GetStorageInfoAsync(ct);
        if (info.Hdds.Count == 0)
        {
            Console.WriteLine("The device reports no disk bays at all.");
            return 0;
        }

        Console.WriteLine($"{"BAY",3}  {"STATUS",-10}  {"CAPACITY",10}  {"FREE",10}  " +
                          $"{"TYPE",-5}  {"MODEL",-24}  SERIAL");
        foreach (var h in info.Hdds)
        {
            string capacity = h.IsInstalled ? FormatTb(h.CapacityMB) : "—";
            string free = h.IsInstalled ? FormatTb(h.FreeSpaceMB) : "—";
            string status = h.IsInstalled ? h.Status : "empty*";
            Console.WriteLine($"{h.Id,3}  {status,-10}  {capacity,10}  {free,10}  " +
                              $"{h.HddType,-5}  {h.Model,-24}  {h.SerialNumber}");
        }

        Console.WriteLine();
        Console.WriteLine($"{info.InstalledCount} disk(s) installed, {FormatTb(info.TotalCapacityMB)} total" +
            (info.WorkMode is { Length: > 0 } mode ? $", work mode {mode}" : "") +
            (info.MaxSupportedHdds is int max ? $"; firmware supports up to {max} disks" : "") + ".");
        if (info.GhostBayCount > 0)
            Console.WriteLine($"* {info.GhostBayCount} bay(s) remember a removed disk — wired and " +
                "known-good, currently empty. Physical bay count comes from the model's spec " +
                "sheet, not from the firmware ceiling.");
        if (info.NonRecordingHdds.Count > 0)
            Console.WriteLine($"{info.NonRecordingHdds.Count} volume(s) hold no footage and are not " +
                "counted in the total: " +
                string.Join(", ", info.NonRecordingHdds.Select(h => $"bay {h.Id} {h.Name} ({h.Property})")) +
                ".");
        foreach (var bad in info.UnhealthyHdds)
            Console.Error.WriteLine($"warning: bay {bad.Id} ({bad.Model}) reports status " +
                $"'{bad.Status}' — footage may not be landing on it.");
        if (info.TotalFreeSpaceMB == 0 && info.InstalledCount > 0)
            Console.WriteLine("Free space 0 is normal: the recorder overwrites oldest footage " +
                "continuously. Retention comes from capacity ÷ bitrate, not free space.");
        return 0;
    }

    // ----- retention -----

    private static async Task<int> RetentionAsync(INvrClient client, IStorageClient storage,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        int? only = null;
        if (opts.TryGetValue("channel", out var chText))
        {
            if (!int.TryParse(chText, out int ch) || ch < 1)
                throw new ArgumentException("invalid --channel");
            only = ch;
        }

        var info = await storage.GetStorageInfoAsync(ct);
        var streams = await storage.GetMainStreamsAsync(ct);
        var names = (await client.GetChannelsAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
        if (only is int filter)
            streams = streams.Where(s => s.Channel == filter).ToList();
        if (streams.Count == 0)
        {
            Console.WriteLine(only is null
                ? "The device reports no camera streams."
                : $"No main stream configured for channel {only}.");
            return 0;
        }

        Console.WriteLine($"{"CH",3}  {"NAME",-22}  {"CODEC",-7}  {"RESOLUTION",-11}  " +
                          $"{"FPS",5}  {"MODE",-4}  {"MAX KBPS",8}  {"OLDEST FOOTAGE",-19}  DAYS");
        var now = DateTime.Now;
        DateTime? systemOldest = null;
        long totalKbps = 0;
        long secondaryKbps = 0;
        int enabledCount = 0;
        var failures = new List<string>();
        foreach (var s in streams)
        {
            string oldestText;
            string daysText = "";
            try
            {
                var oldest = await storage.FindOldestRecordingAsync(s.Channel, ct);
                if (oldest is DateTime t)
                {
                    oldestText = t.ToString("yyyy-MM-dd HH:mm:ss");
                    // A recorder-enforced age limit caps this number whatever the disks hold.
                    daysText = $"{(now - t).TotalDays:F1}" +
                               (s.ArchiveCapDays is int cap ? $" (cap {cap})" : "");
                    if (systemOldest is null || t < systemOldest)
                        systemOldest = t;
                }
                else
                {
                    oldestText = "(no recordings)";
                }
            }
            catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
            {
                // A transport blip on one camera must not take the whole report down;
                // the camera is labeled "(search failed)" and listed at the end.
                oldestText = "(search failed)";
                failures.Add($"channel {s.Channel}: {ex.Message}");
            }

            // Everything the camera writes: the main-stream cap plus any second stream the
            // recorder archives with it (Nx does, unless told not to).
            if (s.Enabled && s.RecordedBitrateKbps is int kbps)
            {
                totalKbps += kbps;
                secondaryKbps += s.SecondaryRecordedKbps ?? 0;
                enabledCount++;
            }

            string name = names.GetValueOrDefault(s.Channel, "");
            string maxKbps = (s.MaxBitrateKbps?.ToString() ?? "?") +
                             (s.SecondaryRecordedKbps is not null ? "+" : "");
            Console.WriteLine($"{s.Channel,3}  {Fit(name, 22),-22}  {s.CodecType,-7}  " +
                $"{s.Resolution,-11}  {FpsColumn(s),5}  {s.QualityControlType,-4}  " +
                $"{maxKbps,8}  {oldestText,-19}  {daysText}");
        }

        if (streams.Any(s => s.FrameRateIsFull))
            Console.WriteLine("* camera set to Full Frame Rate; shown is its native maximum");
        if (streams.Any(s => s.SecondaryRecordedKbps is not null))
            Console.WriteLine("+ a secondary (low-quality) stream is archived alongside the main " +
                "one; the totals below include it");

        Console.WriteLine();
        Console.WriteLine($"Disks: {FormatTb(info.TotalCapacityMB)} across " +
                          $"{info.InstalledCount} disk(s).");
        Console.WriteLine($"Configured max bitrate: {totalKbps:N0} kbps across {enabledCount} " +
                          "enabled camera(s)" +
                          (secondaryKbps > 0
                              ? $", of which {secondaryKbps:N0} kbps is secondary streams."
                              : "."));
        if (StorageEstimator.EstimateRetentionDays(info.TotalCapacityMB, totalKbps) is double est)
            Console.WriteLine($"Worst-case retention estimate: {est:F1} days " +
                "(every camera at its configured max, around the clock).");
        if (systemOldest is DateTime so)
            Console.WriteLine($"Oldest footage on the system: {so:yyyy-MM-dd HH:mm:ss} — " +
                $"{(now - so).TotalDays:F1} days held (device-local clock).");
        foreach (string f in failures)
            Console.Error.WriteLine($"warning: {f}");
        return failures.Count == 0 ? 0 : 1;
    }

    // ----- plan -----

    private static async Task<int> PlanAsync(INvrClient client, IStorageClient storage,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        if (!opts.TryGetValue("days", out var daysText) ||
            !double.TryParse(daysText, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double days) || days <= 0)
            throw new ArgumentException("missing or invalid --days (e.g. --days 30)");

        // Dry-run is the default, and an explicit --dry-run wins over --force when both
        // are given — same rule as `access grant`.
        bool force = opts.ContainsKey("force") && !opts.ContainsKey("dry-run");

        var info = await storage.GetStorageInfoAsync(ct);
        if (info.TotalCapacityMB <= 0)
        {
            Console.Error.WriteLine("error: the device reports no installed disk capacity — " +
                "there is nothing to plan against.");
            return 1;
        }

        var streams = (await storage.GetMainStreamsAsync(ct)).Where(s => s.Enabled).ToList();
        if (streams.Count == 0)
        {
            Console.Error.WriteLine("error: no enabled camera streams to plan for.");
            return 1;
        }
        var names = (await client.GetChannelsAsync(ct)).ToDictionary(c => c.Id, c => c.Name);

        // Each camera's writable range, so the plan promises only what the hardware will
        // accept. A camera that won't say gets the ISAPI-typical span. A second stream the
        // recorder archives alongside the main one is a fixed cost the plan spends first.
        var cameras = new List<PlanCamera>(streams.Count);
        foreach (var s in streams)
        {
            var range = await storage.GetBitrateRangeAsync(s.Channel, ct)
                ?? new BitrateRange(32, 16384);
            cameras.Add(new PlanCamera(s.Channel, names.GetValueOrDefault(s.Channel, ""),
                s.MaxBitrateKbps, range.MinKbps, range.MaxKbps,
                FixedKbps: s.SecondaryRecordedKbps ?? 0));
        }

        var plan = StorageEstimator.PlanUniform(info.TotalCapacityMB, days, cameras);

        Console.WriteLine($"Target: {days:F1} days on {FormatTb(info.TotalCapacityMB)} across " +
                          $"{plan.Cameras.Count} camera(s) → {plan.UniformKbps} kbps per camera.");
        Console.WriteLine();
        Console.WriteLine($"{"CH",3}  {"NAME",-22}  {"CURRENT",8}  {"PLANNED",8}  NOTE");
        foreach (var cam in plan.Cameras)
        {
            string note = cam.Clamped
                ? "clamped to the camera's writable range"
                : cam.Changes ? "" : "already there";
            Console.WriteLine($"{cam.Channel,3}  {Fit(cam.Name, 22),-22}  " +
                $"{cam.CurrentKbps,8}  {cam.PlannedKbps,8}  {note}");
        }
        Console.WriteLine();
        Console.WriteLine($"Planned total: {plan.PlannedTotalKbps:N0} kbps → estimated " +
                          $"{plan.EstimatedDays:F1} days (worst-case).");
        if (!plan.MeetsTarget)
            Console.Error.WriteLine(
                $"warning: the plan does NOT reach {days:F1} days — camera minimums keep the " +
                "total above the budget. More disk, fewer cameras, or a lower target.");

        var toWrite = plan.Cameras.Where(c => c.Changes).ToList();
        if (toWrite.Count == 0)
        {
            Console.WriteLine("Every camera is already at its planned bitrate — nothing to write.");
            return 0;
        }

        if (!force)
        {
            Console.WriteLine($"\nDRY RUN — no camera settings were written ({toWrite.Count} " +
                "would change). Re-run with --force to apply.");
            return 0;
        }

        Console.WriteLine($"\nApplying to {toWrite.Count} camera(s) …");
        // Unchanged cameras at their planned rate, every camera's fixed (secondary) part, and
        // then each write's read-back as it lands.
        long appliedTotalKbps = plan.Cameras.Where(c => !c.Changes).Sum(c => (long)c.PlannedKbps) +
                                plan.Cameras.Sum(c => (long)c.FixedKbps);
        var writeFailures = new List<string>();
        foreach (var cam in toWrite)
        {
            try
            {
                int actual = await storage.SetMaxBitrateAsync(cam.Channel, cam.PlannedKbps, ct);
                appliedTotalKbps += actual;
                Console.WriteLine(actual == cam.PlannedKbps
                    ? $"  ch{cam.Channel}: {cam.CurrentKbps} → {actual} kbps ✓"
                    : $"  ch{cam.Channel}: {cam.CurrentKbps} → asked {cam.PlannedKbps}, the " +
                      $"device kept {actual} kbps");
            }
            catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
            {
                // Carry the camera's configured rate forward so the final estimate reflects
                // the fleet as it actually stands after the partial apply.
                appliedTotalKbps += cam.CurrentKbps ?? 0;
                writeFailures.Add($"ch{cam.Channel}: {ex.Message}");
                Console.Error.WriteLine($"  ch{cam.Channel}: WRITE FAILED — {ex.Message}");
            }
        }

        Console.WriteLine();
        if (StorageEstimator.EstimateRetentionDays(info.TotalCapacityMB, appliedTotalKbps)
            is double achieved)
            Console.WriteLine($"Applied total: {appliedTotalKbps:N0} kbps → estimated " +
                              $"{achieved:F1} days (worst-case), from read-back values.");
        if (writeFailures.Count > 0)
        {
            Console.Error.WriteLine($"{writeFailures.Count} of {toWrite.Count} write(s) failed — " +
                "the estimate above already accounts for the cameras that kept their old rate.");
            return 1;
        }
        Console.WriteLine("All writes verified by read-back.");
        return 0;
    }

    // ----- set -----

    private static async Task<int> SetAsync(IStorageClient storage,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        if (!opts.TryGetValue("channel", out var chText) ||
            !int.TryParse(chText, out int channel) || channel < 1)
            throw new ArgumentException("missing or invalid --channel");
        if (!opts.TryGetValue("kbps", out var kbpsText) ||
            !int.TryParse(kbpsText, out int kbps) || kbps <= 0)
            throw new ArgumentException("missing or invalid --kbps");

        bool force = opts.ContainsKey("force") && !opts.ContainsKey("dry-run");

        var current = (await storage.GetMainStreamsAsync(ct))
            .FirstOrDefault(s => s.Channel == channel);
        if (current is null)
        {
            Console.Error.WriteLine($"error: channel {channel} has no main stream configured.");
            return 1;
        }
        var range = await storage.GetBitrateRangeAsync(channel, ct);
        if (range is not null && (kbps < range.MinKbps || kbps > range.MaxKbps))
            Console.Error.WriteLine($"warning: {kbps} kbps is outside the camera's stated " +
                $"writable range {range.MinKbps}–{range.MaxKbps}; the device may clamp or " +
                "refuse it.");

        Console.WriteLine($"ch{channel}: {current.CurrentDescription()} → {kbps} kbps");
        if (!force)
        {
            Console.WriteLine("\nDRY RUN — nothing was written. Re-run with --force to apply.");
            return 0;
        }

        int actual = await storage.SetMaxBitrateAsync(channel, kbps, ct);
        Console.WriteLine(actual == kbps
            ? $"written and verified by read-back: {actual} kbps."
            : $"written; the device kept {actual} kbps (asked {kbps}).");
        return 0;
    }

    private static string CurrentDescription(this CameraStream s) =>
        s.MaxBitrateKbps is int cur
            ? $"{cur} kbps ({s.QualityControlType})"
            : $"unknown ({s.QualityControlType})";

    // ----- helpers -----

    private static string FormatTb(long mb) => mb switch
    {
        <= 0 => "0",
        < 1_000_000 => $"{mb / 1_000.0:F0} GB",
        _ => $"{mb / 1_000_000.0:F2} TB",
    };

    private static string Fit(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>"20.0", or "30.0*" for a camera on Full Frame Rate (footnoted under the table).</summary>
    private static string FpsColumn(CameraStream s) => s.FrameRateFps is double fps
        ? s.FrameRateIsFull ? $"{fps:F1}*" : fps.ToString("F1")
        : "";
}
