using System.Globalization;
using DVRTool.Core;

namespace DVRTool.Cli;

/// <summary>
/// <c>dvrtool config …</c> — the recorder's own configuration: its clock, its time source, its
/// service ports, its LAN address and its name.
/// </summary>
/// <remarks>
/// <para>
/// <c>config audit</c> is the reason this command group exists. Footage search, timeline
/// geometry and export naming all run on NVR-local wall clock, so a recorder whose clock is
/// out stamps the wrong time onto every recording it writes — and the discovery pass of
/// 2026-09-09 found two wrong clocks on the first three recorders it read, one of them 58
/// minutes out on a 128-channel NVR. Reading the clock across the fleet is a correctness check
/// on the export product DVRTool already ships.
/// </para>
/// <para>
/// House gates, unchanged from <see cref="StorageCommands"/> and
/// <see cref="RecordingCommands"/>: dry run is the default, an explicit <c>--dry-run</c> beats
/// <c>--force</c>, every applied write is read back, and what gets printed is what the
/// recorder now holds. Address and port writes are deliberately absent — a network write
/// cannot be verified on the socket that issued it.
/// </para>
/// </remarks>
internal static class ConfigCommands
{
    private const string Usage = """
        dvrtool config — the recorder's own settings (clock, NTP, ports, LAN address)

        Usage:
          dvrtool config show   [connection options]
          dvrtool config clock  [connection options]
          dvrtool config audit  [--all-saved | --device <name> ...] [connection options]
          dvrtool config set    [--ntp-server <host>] [--ntp-port <n>] [--ntp-interval <min>]
                                [--ntp on|off] [--timezone <vendor value>] [--sync-now]
                                [--name <text>] [--force] [connection options]

        Subcommands:
          show    Everything this recorder says about itself: clock and drift, time source,
                  service ports with their declared ranges, the LAN address, and the vendor
                  notes (DDNS, UPnP map, lockout policy, Nx's clock-master settings).
          clock   Just the clock, its drift against this workstation, and the verdict.
          audit   THE FLEET CLOCK AUDIT. Reads the clock and the time source of every saved
                  device (--all-saved), or the ones named with --device, and prints a row
                  each. The only config verb that reads more than one recorder.
          set     Change the time source, the zone, or the name. DRY RUN by default:
                  prints what would change and stops. --force applies it and reads back.

        Options:
          --all-saved          Audit every recorder saved in the GUI (%APPDATA%\DVRTool\
                               devices.json). Door panels are skipped — they are not recorders.
          --device <name>      Work from a saved GUI record instead of --host/--user/--pass:
                               its stored (DPAPI-protected) credentials and its expected
                               serial. Repeatable for `audit`; exactly one for the others.
          --ntp-server <host>  The NTP server, as a hostname or an IP.
          --ntp-port <n>       Its port (default 123 on both vendors).
          --ntp-interval <min> Sync interval in MINUTES (Hikvision accepts 1–10080).
          --ntp on|off         Whether the recorder keeps its clock from NTP at all.
          --timezone <value>   The VENDOR'S OWN zone value: a Hikvision timeZone string, a
                               Dahua zone index. There is no IANA mapping — inventing one
                               would silently mis-set clocks, which is the fault this command
                               exists to catch — so `config show` prints the current value in
                               copyable form and the value moves between same-vendor units.
          --dst on|off         Dahua only: its DST switch. Hikvision folds DST into the zone
                               string and has no switch to set.
          --sync-now           Stamp this workstation's clock onto the recorder. Refused on a
                               recorder that syncs from NTP — a hand-set clock there is
                               overwritten at the next sync and hides the real cause.
          --name <text>        The device name.
          --force              Apply. Without it nothing is written.

        Drift is measured as a difference of WALL-CLOCK DIGITS, not of instants: Hikvision
        firmware reports an offset that disagrees with its own DST rule (observed: −05:00 while
        standing in −04:00), so an instant built from the device's offset is an hour wrong. A
        recorder deliberately set to another zone therefore reads as drifted; give that record
        an expected offset in the GUI (Add/Edit device) to measure it against its own zone.

        NOT IMPLEMENTED, on purpose: changing the LAN address or a service port. A network
        write cannot be verified on the socket that issued it, and one made through a port
        forward is unrecoverable by definition — that is a truck roll, and the tool declines
        rather than offering it. See docs/device-config-spec.md §7.

        Connection options are the same as every other command (--host/--user/--pass or
        DVR_HOST/DVR_USER/DVR_PASS, --tls, --expect-serial, …). `config audit --all-saved`
        needs none of them: it reads the saved records.
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

    /// <summary>
    /// Whether this invocation works from the GUI's saved records rather than from a
    /// <c>--host</c> on the command line — in which case it must run before <c>Program</c>
    /// demands connection options this invocation has no use for.
    /// </summary>
    internal static bool UsesSavedDevices(string subcommand, Dictionary<string, string> opts) =>
        opts.ContainsKey("device") || (subcommand == "audit" && opts.ContainsKey("all-saved"));

    /// <summary>
    /// The saved-record entry point: the fleet sweep, or any single-device verb against one
    /// saved recorder — which is how a field verb runs without the operator retyping
    /// credentials that are already stored (and DPAPI-protected) for that recorder.
    /// </summary>
    internal static async Task<int> RunSavedAsync(string subcommand,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        if (subcommand is "audit" or "")
            return await RunFleetAuditAsync(opts, ct);

        var picked = ResolveSaved(opts, out int error);
        if (picked is null)
            return error;
        if (picked.Count != 1)
        {
            Console.Error.WriteLine(
                $"error: `config {subcommand}` reads one recorder — name exactly one " +
                "--device. Only `config audit` reads several.");
            return 2;
        }

        var device = picked[0];
        using var client = VendorClients.For(device);

        // Identity before content, exactly as the GUI does it: a config view is where reading
        // the wrong box misleads worst.
        var check = await DeviceIdentityGuard.CheckAsync(client,
            device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name, ct: ct);
        DeviceIdentityGuard.Ensure(check);
        if (check.Message.Length > 0)
            Console.Error.WriteLine("note: " + check.Message);

        return await RunAsync(client, subcommand, opts, ct);
    }

    /// <summary>The saved recorders this invocation names, or null with an exit code.</summary>
    private static List<SavedDevice>? ResolveSaved(Dictionary<string, string> opts,
        out int exitCode)
    {
        exitCode = 0;
        var saved = DeviceStore.Load().Where(d => !d.IsPanel).ToList();
        if (saved.Count == 0)
        {
            Console.Error.WriteLine(
                "error: no recorders are saved. Add them in the GUI (Add Device…), or use " +
                "connection options (--host/--user/--pass).");
            exitCode = 2;
            return null;
        }
        if (!opts.TryGetValue("device", out string? names) || names.Length == 0)
            return saved;

        // Repeated flags arrive joined by an ASCII unit separator (see ParseOptions).
        var asked = names.Split('', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        var missing = asked
            .Where(a => !saved.Any(d => d.Name.Equals(a, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine(
                $"error: no saved recorder named {string.Join(", ", missing)}. " +
                $"Saved: {string.Join(", ", saved.Select(d => d.Name))}");
            exitCode = 2;
            return null;
        }
        return [.. saved.Where(d => asked.Contains(d.Name, StringComparer.OrdinalIgnoreCase))];
    }

    // ----- the fleet sweep -----

    /// <summary>
    /// <c>config audit</c> across saved devices: two cheap reads per recorder (the clock and
    /// the time source), each device carrying its own failure.
    /// </summary>
    internal static async Task<int> RunFleetAuditAsync(Dictionary<string, string> opts,
        CancellationToken ct)
    {
        var wanted = ResolveSaved(opts, out int error);
        if (wanted is null)
            return error;

        // All at once: one slow site must not serialize the rest, and a failure is carried
        // per device rather than faulting the sweep.
        var rows = await Task.WhenAll(wanted.Select(d => ReadOneAsync(d, ct)));
        PrintAudit(ConfigAudit.Build(rows));
        return rows.Any(r => !r.Ok) ? 1 : 0;
    }

    private static async Task<ClockAuditRow> ReadOneAsync(SavedDevice device,
        CancellationToken ct)
    {
        INvrClient? client = null;
        try
        {
            client = VendorClients.For(device);
            if (client is not IDeviceConfigClient config)
                return ClockAuditRow.Failed(device.Name,
                    $"reading the clock isn't implemented for {device.VendorKind} devices");

            // Identity before content, like every other fleet read: host:port names a socket,
            // not a recorder, and a clock audit that credits the wrong box is worse than none.
            // DeviceIdentityException is deliberately not an NvrException, so a serial
            // mismatch during a sweep is a finding rather than a skipped row.
            var check = await DeviceIdentityGuard.CheckAsync(client,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name,
                ct: ct);
            DeviceIdentityGuard.Ensure(check);

            return await ClockSweep.ReadAsync(device.Name, config,
                device.ExpectedOffsetMinutes, ct);
        }
        catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
        {
            return ClockAuditRow.Failed(device.Name, ex.Message);
        }
        finally
        {
            client?.Dispose();
        }
    }

    // ----- single-device verbs -----

    internal static async Task<int> RunAsync(INvrClient client, string subcommand,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        if (client is not IDeviceConfigClient config)
        {
            Console.Error.WriteLine(
                $"error: device configuration isn't implemented for {client.Vendor} devices.");
            return 2;
        }

        return subcommand switch
        {
            "show" => await ShowAsync(config, ct),
            "clock" => await ClockAsync(config, ct),
            "audit" => await AuditOneAsync(client, config, ct),
            "set" => await SetAsync(config, opts, ct),
            _ => UnknownSubcommand(subcommand),
        };
    }

    private static int UnknownSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"error: unknown config subcommand '{subcommand}'\n");
        Console.WriteLine(Usage);
        return 2;
    }

    private static async Task<int> ClockAsync(IDeviceConfigClient config, CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        var clock = await config.GetClockAsync(ct);
        var finished = DateTimeOffset.Now;
        var source = await config.GetTimeSourceAsync(ct);
        var drift = ClockDrift.Measure(clock, started, finished);

        PrintClock(clock, drift, source);
        return ClockAuditRow.For("this device", clock, drift, source).IsFault ? 1 : 0;
    }

    private static async Task<int> AuditOneAsync(INvrClient client, IDeviceConfigClient config,
        CancellationToken ct)
    {
        // `audit` without --all-saved or --device is the same sweep over one recorder, so a
        // script can run it against a device that is not saved in the GUI.
        var row = await ClockSweep.ReadAsync(
            DeviceIdentityGuard.AddressOf(client.Connection), config, null, ct);

        PrintAudit(ConfigAudit.Build([row]));
        return row.IsFault ? 1 : 0;
    }

    private static async Task<int> ShowAsync(IDeviceConfigClient config, CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        var doc = await config.GetConfigurationAsync(ct);
        var finished = DateTimeOffset.Now;
        var drift = ClockDrift.Measure(doc.Clock, started, finished);

        if (doc.DeviceName is { Length: > 0 } name)
            Console.WriteLine($"Name:        {name}");
        if (doc.Model is { Length: > 0 } model)
            Console.WriteLine($"Model:       {model}");
        if (doc.FirmwareVersion is { Length: > 0 } firmware)
            Console.WriteLine($"Firmware:    {firmware}");
        Console.WriteLine();

        PrintClock(doc.Clock, drift, doc.TimeSource);

        Console.WriteLine();
        Console.WriteLine("NTP");
        if (!doc.Scope.Ntp)
            Console.WriteLine($"  n/a — {doc.Scope.Reason}");
        else if (doc.NtpServers is null)
            Console.WriteLine("  ?  (not readable)");
        else
        {
            foreach (var server in doc.NtpServers)
                Console.WriteLine($"  {server.Address}:{server.Port}" +
                    (server.Enabled ? "" : "  (disabled)"));
            if (doc.NtpServers.Count == 0)
                Console.WriteLine("  (no server configured)");
            if (doc.NtpInterval is TimeSpan interval)
                Console.WriteLine($"  every {interval.TotalMinutes:0} min");
        }

        Console.WriteLine();
        Console.WriteLine("Service ports");
        if (!doc.Scope.Ports)
            Console.WriteLine($"  n/a — {doc.Scope.Reason}");
        else if (doc.Ports.Count == 0)
            Console.WriteLine("  (none this device exposes by name)");
        else
        {
            Console.WriteLine($"  {"PROTOCOL",-14} {"PORT",5}  {"STATE",-8}  RANGE");
            foreach (var port in doc.Ports)
            {
                var (range, fromDevice) = port.EffectiveRange;
                Console.WriteLine(
                    $"  {port.Protocol,-14} {port.Port,5}  " +
                    $"{(port.Enabled ? "enabled" : "disabled"),-8}  {range.Text}" +
                    (fromDevice ? "" : $"  [{ServicePortRange.FallbackLabel}]"));
                foreach (var (key, value) in port.Extras)
                    Console.WriteLine($"  {"",14} {"",5}  {key} = {value}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Network");
        if (!doc.Scope.Network)
            Console.WriteLine($"  n/a — {doc.Scope.Reason}");
        else if (doc.Interfaces is null)
            Console.WriteLine("  ?  (not readable)");
        else
        {
            foreach (var nic in doc.Interfaces)
            {
                bool configured = nic.IpAddress.Length > 0 && nic.IpAddress != "0.0.0.0";
                Console.WriteLine($"  {nic.Name}{(nic.IsDefault ? "  (default)" : "")}" +
                    (configured ? "" : "  (no address)"));
                if (configured)
                    Console.WriteLine($"    {nic.AddressingText}  {nic.IpAddress}" +
                        (nic.SubnetMask.Length > 0 && nic.SubnetMask != "0.0.0.0"
                            ? $"/{nic.SubnetMask}" : "") +
                        (nic.Gateway.Length > 0 && nic.Gateway != "0.0.0.0"
                            ? $"  gw {nic.Gateway}" : ""));
                if (nic.Dns.Count > 0)
                    Console.WriteLine($"    DNS {string.Join(", ", nic.Dns)}" +
                        (nic.DnsAuto is true ? "  (automatic)" : ""));
                var bits = new List<string>();
                if (nic.MacAddress.Length > 0)
                    bits.Add(nic.MacAddress);
                if (nic.Mtu is int mtu)
                    bits.Add($"MTU {mtu}" + (nic.MtuRange is { } r ? $" of {r.Text}" : ""));
                if (nic.LinkSpeedMbps is int speed)
                    bits.Add($"{speed} Mbps");
                if (nic.LinkUp is bool up)
                    bits.Add(up ? "link up" : "link down");
                if (bits.Count > 0)
                    Console.WriteLine($"    {string.Join("  ", bits)}");
            }
        }

        if (doc.Notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Notes");
            foreach (var group in doc.Notes.GroupBy(n => n.Group))
                foreach (var note in group)
                    Console.WriteLine($"  {group.Key,-18} {note.Label,-24} {note.Value}");
        }

        if (doc.Failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Parts this recorder would not answer (shown as “?” above):");
            foreach (var failure in doc.Failures)
                Console.WriteLine($"  {failure.Label}: {failure.Value}");
        }
        return 0;
    }

    // ----- set -----

    private static async Task<int> SetAsync(IDeviceConfigClient config,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        if (config is not IDeviceConfigWriter writer)
        {
            Console.Error.WriteLine(
                "error: this recorder has no writable configuration through DVRTool. An Nx / " +
                "DW Spectrum system keeps one clock for the whole VMS and takes its zone and " +
                "address from the Windows box it runs on.");
            return 2;
        }

        if (!TryReadSwitch(opts, "ntp", out bool? ntpEnabled) ||
            !TryReadSwitch(opts, "dst", out bool? dst))
            return 2;

        string? server = opts.GetValueOrDefault("ntp-server");
        string? zone = opts.GetValueOrDefault("timezone");
        string? name = opts.GetValueOrDefault("name");
        bool syncNow = opts.ContainsKey("sync-now");
        if (!TryReadInt(opts, "ntp-port", out int? ntpPort) ||
            !TryReadInt(opts, "ntp-interval", out int? intervalMinutes))
            return 2;

        var ntp = new NtpSettings(server, ntpPort,
            intervalMinutes is int minutes ? TimeSpan.FromMinutes(minutes) : null, ntpEnabled);
        var time = new TimeSettings(zone, dst);

        bool wantsNtp = server is not null || ntpPort is not null ||
            intervalMinutes is not null || ntpEnabled is not null;
        bool wantsTime = zone is not null || dst is not null;
        if (!wantsNtp && !wantsTime && !syncNow && name is null)
        {
            Console.Error.WriteLine(
                "error: nothing to set — give --ntp-server / --ntp-port / --ntp-interval / " +
                "--ntp on|off, --timezone, --dst on|off, --sync-now or --name.");
            return 2;
        }

        // The read is not optional: a config write is the device's own document with fields
        // replaced, and the writer refuses without one.
        var before = await writer.GetConfigurationAsync(ct);
        var driftReference = DateTimeOffset.Now;

        Console.WriteLine($"{before.DeviceName ?? "this recorder"} now holds:");
        Console.WriteLine($"  clock        {before.Clock.WallClockText}" +
            (before.Clock.VendorZoneLabel is { Length: > 0 } label ? $"  zone {label}" : ""));
        if (before.Clock.DstEnabled is bool dstNow)
            Console.WriteLine($"  DST          {(dstNow ? "on" : "off")}");
        if (before.Scope.Ntp)
            Console.WriteLine($"  time source  " +
                (before.NtpSummary.Length > 0 ? before.NtpSummary : "none"));
        Console.WriteLine();

        var asked = new List<string>();
        if (server is not null)
            asked.Add($"NTP server → {server}");
        if (ntpPort is int port)
            asked.Add($"NTP port → {port}");
        if (intervalMinutes is int interval)
            asked.Add($"NTP interval → {interval} min");
        if (ntpEnabled is bool enabled)
            asked.Add($"NTP → {(enabled ? "on" : "off")}");
        if (zone is not null)
            asked.Add($"time zone → {zone}");
        if (dst is bool wantDst)
            asked.Add($"DST → {(wantDst ? "on" : "off")}");
        if (syncNow)
            asked.Add($"clock → this workstation ({driftReference:yyyy-MM-dd HH:mm:ss})");
        if (name is not null)
            asked.Add($"name → {name}");
        foreach (string line in asked)
            Console.WriteLine($"  {line}");

        // Dry run is the default and an explicit --dry-run wins over --force, the same gate
        // every other write in this tool uses.
        bool force = opts.ContainsKey("force") && !opts.ContainsKey("dry-run");
        if (!force)
        {
            Console.WriteLine();
            Console.WriteLine("DRY RUN — nothing was written. Re-run with --force to apply.");
            return 0;
        }

        Console.WriteLine();
        int failed = 0, changed = 0;
        if (wantsNtp)
            Report(await writer.SetNtpAsync(ntp, ct), ref changed, ref failed);
        if (wantsTime)
            Report(await writer.SetTimeAsync(time, ct), ref changed, ref failed);
        if (syncNow)
            Report(await writer.SyncTimeNowAsync(ct), ref changed, ref failed);
        if (name is not null)
            Report(await writer.SetDeviceNameAsync(name, ct), ref changed, ref failed);

        Console.WriteLine();
        Console.WriteLine($"{changed} setting(s) changed, {failed} refused or rejected.");
        return failed > 0 ? 1 : 0;
    }

    /// <summary>Prints one write's outcome as what the recorder now holds, not what was asked.</summary>
    private static void Report(ConfigChange change, ref int changed, ref int failed)
    {
        string note = change.Note.Length > 0 ? $"  ({change.Note})" : "";
        if (change.Rejected)
        {
            failed++;
            Console.Error.WriteLine($"  {change.Field,-12} REJECTED{note}");
        }
        else if (change.Changed)
        {
            changed++;
            Console.WriteLine($"  {change.Field,-12} now {Describe(change.Field, change.After)}");
        }
        else
        {
            // "Nothing to do" is not a failure — except when a refusal explains why, which is
            // worth an exit code, since the operator asked for something that did not happen.
            if (change.Note.Length > 0)
                failed++;
            Console.WriteLine($"  {change.Field,-12} unchanged{note}");
        }
    }

    private static string Describe(string field, DeviceConfiguration doc) => field switch
    {
        "NTP" => doc.NtpSummary.Length > 0 ? doc.NtpSummary : "none",
        "device name" => doc.DeviceName ?? "?",
        _ => doc.Clock.WallClockText +
            (doc.Clock.VendorZoneLabel is { Length: > 0 } zone ? $"  zone {zone}" : ""),
    };

    // ----- printing -----

    private static void PrintClock(DeviceClock clock, ClockDrift drift, TimeSourceStatus source)
    {
        Console.WriteLine($"Clock:       {clock.WallClockText}");
        Console.WriteLine($"This host:   {drift.ReferenceWallClock:yyyy-MM-dd HH:mm:ss}" +
            (drift.ExpectedOffsetMinutes is int shift
                ? $"  (shifted {shift:+#;-#;0} min for this recorder's zone)"
                : ""));
        Console.WriteLine($"Drift:       {drift.Text}  " +
            $"(±{drift.ReadLatency.TotalSeconds:0.0} s, the read's own round trip)");

        // The offset is shown as a claim DVRTool ignores, because an operator comparing this
        // against the recorder's own web UI has to be able to see why the two disagree.
        if (clock.DeclaredOffsetText is { } offset)
            Console.WriteLine($"Says offset: {offset}" +
                (drift.DeclaredOffsetDiffers
                    ? "   ← the device's claim, and it disagrees with this host; DVRTool " +
                      "compares wall clocks and ignores it"
                    : ""));
        if (clock.VendorZoneLabel is { Length: > 0 } label)
            Console.WriteLine($"Zone:        {label}");
        if (clock.DstEnabled is bool dst)
            Console.WriteLine($"DST:         {(dst ? "on" : "off")}");
        Console.WriteLine($"Time source: " + (source.NtpEnabled switch
        {
            true => "NTP",
            false => "NONE — nothing is keeping this clock",
            null => source.Detail.Length > 0 ? "" : "n/a",
        }) + (source.Detail.Length > 0
            ? (source.NtpEnabled is null ? source.Detail : $"  ({source.Detail})")
            : ""));

        string verdict = ClockAuditRow.For("", clock, drift, source).Verdict;
        if (verdict.Length > 0)
            Console.WriteLine($"Verdict:     {verdict}");
    }

    private static void PrintAudit(ConfigAudit audit)
    {
        Console.WriteLine($"{"DEVICE",-22}  {"CLOCK",-19}  {"DRIFT",-13}  {"SOURCE",-6}  VERDICT");
        foreach (var row in audit.Rows)
        {
            if (!row.Ok)
            {
                Console.WriteLine($"{Truncate(row.DeviceName, 22),-22}  {"?",-19}  {"?",-13}  " +
                    $"{"?",-6}  {row.Verdict}: {Truncate(row.Error!, 60)}");
                continue;
            }
            string source = row.TimeSource.NtpEnabled switch
            {
                true => "NTP",
                false => "none",
                null => "n/a",
            };
            Console.WriteLine(
                $"{Truncate(row.DeviceName, 22),-22}  {row.Clock!.WallClockText,-19}  " +
                $"{row.Drift!.Text,-13}  {source,-6}  {row.Verdict}");
        }

        Console.WriteLine();
        Console.WriteLine(audit.Summary);

        // The zone label is the answer to "why does that recorder read an hour out" when the
        // answer is "it is in another zone on purpose", so it is printed for every row that
        // has one and is out.
        foreach (var row in audit.Faults.Where(r => r.ZoneText.Length > 0))
            Console.WriteLine($"  {row.DeviceName}: zone {row.ZoneText}");
    }

    // ----- option plumbing -----

    private static bool TryReadSwitch(Dictionary<string, string> opts, string name,
        out bool? value)
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

    private static bool TryReadInt(Dictionary<string, string> opts, string name, out int? value)
    {
        value = null;
        if (!opts.TryGetValue(name, out string? text))
            return true;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ||
            parsed <= 0)
        {
            Console.Error.WriteLine($"error: --{name} takes a positive number, not '{text}'.");
            return false;
        }
        value = parsed;
        return true;
    }

    private static string Truncate(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "…";
}
