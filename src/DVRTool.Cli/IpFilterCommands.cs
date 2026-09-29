using DVRTool.Core;

namespace DVRTool.Cli;

/// <summary>
/// <c>dvrtool ipfilter …</c> — the recorder's own IP blocklist: read it across the fleet,
/// block and unblock addresses.
/// </summary>
/// <remarks>
/// <para>
/// This group exists to be scripted: feed it the sources of the illegal-login attempts and
/// it puts them on every recorder that has a filter. So its safety is in the plan, not in a
/// prompt — <see cref="IpFilterPlan"/> refuses this workstation, the recorder, its gateway,
/// anybody logged in right now and (unless asked) LAN addresses, and a plan with one refusal
/// writes nothing.
/// </para>
/// <para>
/// Same contract as every write-capable group: dry-run is the default and wins over
/// <c>--force</c>; every write is read back and reported as what the recorder now holds. An
/// allowlist is never edited and the mode is never changed — that is the one change that
/// can lock everyone out, and it belongs on the recorder's own page.
/// </para>
/// </remarks>
internal static class IpFilterCommands
{
    private const string Usage = """
        dvrtool ipfilter — the recorder's IP blocklist

        Usage:
          dvrtool ipfilter show    [--all-saved | --device <name> ...] [connection options]
          dvrtool ipfilter add     --ip <addr> ... [--from-file <path>] [--enable]
                                   [--allow-lan] [--allow-logged-in] [--protect <addr> ...]
                                   [--all-saved | --device <name> ...] [--force]
          dvrtool ipfilter remove  --ip <addr> ... [--from-file <path>]
                                   [--all-saved | --device <name> ...] [--force]
          dvrtool ipfilter enable | disable  [--all-saved | --device <name> ...] [--force]

        Subcommands:
          show      The filter on one recorder (every address), or on several: one row per
                    recorder (has it / on / used of capacity), then which recorders block
                    which address.
          add       Block addresses. Existing entries are kept; one already listed is
                    reported and not written again.
          remove    Unblock addresses.
          enable    Turn a blocklist on. Adding to a filter that is OFF blocks nobody —
          disable   `add` says so, and `add --enable` does both in one write.

        Options:
          --ip <addr>          One address (IPv4 dotted quad, or IPv6 where the recorder
                               declares it). Repeatable; commas also separate. There are no
                               ranges or subnets: the firmware holds one host per entry.
          --from-file <path>   Addresses one per line; blank lines and # comments ignored.
          --enable             With add: also turn the filter on.
          --allow-lan          Permit private / LAN addresses (10/8, 172.16/12, 192.168/16,
                               100.64/10, link-local). Refused by default: on a customer
                               site that is usually the site's own iVMS workstation.
          --allow-logged-in    Permit an address that is logged into the recorder right now.
                               Refused by default because it cuts off a live session —
                               override it when that session IS the intruder.
          --protect <addr>     Never block this address (repeatable) — name this office's
                               public address here, which DVRTool cannot see from inside NAT.
                               This workstation's own interface addresses, the recorder's own
                               address and its gateway are always protected.
          --all-saved          Every recorder saved in the GUI. Recorders without a filter,
                               and non-Hikvision records, are listed and skipped.
          --device <name>      A saved GUI record (its stored credentials and expected
                               serial). Repeatable.
          --dry-run            Print the plan and write nothing (the default).
          --force              Actually write.

        Refused whole, on any recorder, with nothing written:
          - a filter in ALLOWLIST mode (only listed addresses may connect): any change to
            one can lock out everyone, this workstation included;
          - a list that would exceed the recorder's capacity (32 on the firmware seen);
          - any refused address — a plan is written as reviewed or not at all.

        Hikvision only. Exit code 1 means something was refused, failed, or could not be read.
        """;

    internal static bool TryRunHelp(string subcommand, Dictionary<string, string> opts,
        out int exitCode)
    {
        exitCode = 0;
        if (subcommand.Length > 0 && subcommand != "help" && !opts.ContainsKey("help"))
        {
            if (subcommand is "show" or "add" or "remove" or "enable" or "disable")
                return false;
            Console.Error.WriteLine($"error: unknown subcommand `ipfilter {subcommand}`.\n");
            Console.WriteLine(Usage);
            exitCode = 2;
            return true;
        }
        Console.WriteLine(Usage);
        exitCode = subcommand.Length == 0 && !opts.ContainsKey("help") ? 2 : 0;
        return true;
    }

    internal static bool UsesSavedDevices(Dictionary<string, string> opts) =>
        opts.ContainsKey("device") || opts.ContainsKey("all-saved");

    private static bool ShouldWrite(Dictionary<string, string> opts)
    {
        if (opts.ContainsKey("dry-run") && opts.ContainsKey("force"))
            Console.Error.WriteLine("note: both --dry-run and --force given; --dry-run wins (no writes).");
        return opts.ContainsKey("force") && !opts.ContainsKey("dry-run");
    }

    private sealed record Target(string Name, INvrClient Client);

    // ----- entry points -----

    internal static async Task<int> RunAsync(INvrClient client, string subcommand,
        Dictionary<string, string> opts, CancellationToken ct) =>
        await RunOnAsync([new Target(client.Connection.Host, client)], subcommand, opts,
            single: true, ct);

    internal static async Task<int> RunSavedAsync(string subcommand,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        var picked = ConfigCommands.ResolveSaved(opts, out int error);
        if (picked is null)
            return error;

        var targets = new List<Target>();
        var skipped = new List<IpFilterAuditRow>();
        try
        {
            // Identity before anything, like every fleet read: a filter written onto the wrong
            // box is a block nobody asked for on a recorder nobody named.
            var checks = await Task.WhenAll(picked.Select(async d =>
            {
                INvrClient? client = null;
                try
                {
                    client = VendorClients.For(d);
                    if (client is not IIpFilterClient)
                    {
                        client.Dispose();
                        return (d, (INvrClient?)null,
                            IpFilterAuditRow.Unimplemented(d.Name, d.VendorKind.ToString()));
                    }
                    var check = await DeviceIdentityGuard.CheckAsync(client,
                        d.ExpectedSerial.Length > 0 ? d.ExpectedSerial : null, d.Name, ct: ct);
                    DeviceIdentityGuard.Ensure(check);
                    return (d, client, (IpFilterAuditRow?)null);
                }
                // A wrong device is not a per-device failure to carry on past: it aborts the
                // whole run (DeviceIdentityException is deliberately not an NvrException).
                catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
                {
                    client?.Dispose();
                    return (d, null, IpFilterAuditRow.Failed(d.Name, ex.Message));
                }
            }));

            foreach (var (device, client, row) in checks)
            {
                if (client is not null)
                    targets.Add(new Target(device.Name, client));
                else
                    skipped.Add(row!);
            }

            return await RunOnAsync(targets, subcommand, opts, single: picked.Count == 1, ct,
                skipped);
        }
        finally
        {
            foreach (var t in targets)
                t.Client.Dispose();
        }
    }

    private static async Task<int> RunOnAsync(List<Target> targets, string subcommand,
        Dictionary<string, string> opts, bool single, CancellationToken ct,
        List<IpFilterAuditRow>? skipped = null)
    {
        skipped ??= [];
        if (targets.Count > 0 && targets.All(t => t.Client is not IIpFilterClient))
        {
            Console.Error.WriteLine(
                $"error: the IP filter isn't implemented for {targets[0].Client.Vendor} devices.");
            return 2;
        }

        return subcommand switch
        {
            "show" => await ShowAsync(targets, skipped, single, ct),
            "add" or "remove" or "enable" or "disable" =>
                await ChangeAsync(targets, skipped, subcommand, opts, ct),
            _ => 2,
        };
    }

    // ----- show -----

    private static async Task<int> ShowAsync(List<Target> targets, List<IpFilterAuditRow> skipped,
        bool single, CancellationToken ct)
    {
        var rows = (await Task.WhenAll(targets.Select(t => ReadRowAsync(t, ct))))
            .Concat(skipped).ToList();

        if (single && rows.Count == 1 && rows[0].Ok)
        {
            PrintOne(rows[0].DeviceName, rows[0].State!);
            return rows[0].State!.Failures.Count > 0 ? 1 : 0;
        }

        var audit = IpFilterAudit.Build(rows);
        Console.WriteLine($"{"DEVICE",-22}  {"FILTER",-26}  VERDICT");
        foreach (var row in audit.Rows.OrderBy(r => r.DeviceName, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"{Truncate(row.DeviceName, 22),-22}  {Truncate(row.Summary, 26),-26}  {row.Verdict}");

        if (audit.Addresses.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{"BLOCKED ADDRESS",-40}  ON");
            foreach (var (address, devices) in audit.Addresses)
                Console.WriteLine($"{address,-40}  {string.Join(", ", devices)}");
        }

        Console.WriteLine();
        Console.WriteLine(audit.Summary);
        return audit.IsPartial ? 1 : 0;
    }

    private static void PrintOne(string name, IpFilterState s)
    {
        Console.WriteLine($"{name}: {s.Summary}");
        if (!s.Supported)
        {
            Console.WriteLine("  this firmware has no IP filter (isSupportIPFilter is false).");
            return;
        }
        if (s.Mode == IpFilterMode.Allow)
            Console.WriteLine("  ALLOWLIST: only the addresses below may connect. DVRTool will not edit it.");
        foreach (var e in s.Entries)
            Console.WriteLine($"  {e.Id,3}  {e.Address}" +
                (e.Mode != s.Mode ? $"  ({e.Mode})" : ""));
        if (!s.Enabled && s.Entries.Count > 0)
            Console.WriteLine("  the filter is OFF — these addresses are listed and not blocked.");
        foreach (var session in s.Sessions)
            Console.WriteLine($"  logged in now: {session.User} from {session.Address}" +
                (session.LoginTime.Length > 0 ? $" since {session.LoginTime}" : ""));
        foreach (var note in s.Failures)
            Console.Error.WriteLine($"note: {note.Label} could not be read — {note.Value}");
    }

    private static async Task<IpFilterAuditRow> ReadRowAsync(Target t, CancellationToken ct)
    {
        try
        {
            if (t.Client is not IIpFilterClient filter)
                return IpFilterAuditRow.Unimplemented(t.Name, t.Client.Vendor.ToString());
            return new IpFilterAuditRow { DeviceName = t.Name, State = await filter.GetIpFilterAsync(ct) };
        }
        catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
        {
            return IpFilterAuditRow.Failed(t.Name, ex.Message);
        }
    }

    // ----- add / remove / enable / disable -----

    private static async Task<int> ChangeAsync(List<Target> targets,
        List<IpFilterAuditRow> skipped, string verb, Dictionary<string, string> opts,
        CancellationToken ct)
    {
        List<string> addresses;
        try
        {
            addresses = ReadAddresses(opts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        if (verb is "add" or "remove" && addresses.Count == 0)
        {
            Console.Error.WriteLine($"error: `ipfilter {verb}` needs at least one --ip or --from-file.");
            return 2;
        }
        if (opts.ContainsKey("enable") && opts.ContainsKey("disable"))
        {
            Console.Error.WriteLine("error: --enable and --disable together mean nothing.");
            return 2;
        }

        var protect = Split(opts.GetValueOrDefault("protect", ""))
            .Concat(IpFilterAddress.ThisWorkstation())
            .ToList();
        var badProtect = Split(opts.GetValueOrDefault("protect", ""))
            .Where(p => IpFilterAddress.Parse(p) is null).ToList();
        if (badProtect.Count > 0)
        {
            Console.Error.WriteLine($"error: --protect {string.Join(", ", badProtect)} is not an IP address.");
            return 2;
        }

        var request = new IpFilterRequest(
            Add: verb == "add" ? addresses : [],
            Remove: verb == "remove" ? addresses : [],
            Enable: verb switch
            {
                "enable" => true,
                "disable" => false,
                _ => opts.ContainsKey("enable") ? true : opts.ContainsKey("disable") ? false : null,
            },
            AllowLocal: opts.ContainsKey("allow-lan"),
            AllowLoggedIn: opts.ContainsKey("allow-logged-in"),
            Protected: protect);

        // Read and plan everywhere first, so the operator sees the whole fleet's plan before
        // one byte is written anywhere.
        var planned = await Task.WhenAll(targets.Select(async t =>
        {
            try
            {
                var state = await ((IIpFilterClient)t.Client).GetIpFilterAsync(ct);
                return (t, Plan: (IpFilterPlan?)IpFilterPlan.For(state, request), Error: (string?)null);
            }
            catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
            {
                return (t, Plan: (IpFilterPlan?)null, Error: (string?)ex.Message);
            }
        }));

        int problems = 0;
        foreach (var row in skipped)
        {
            Console.WriteLine($"{row.DeviceName}: skipped — {row.Error}");
            if (!row.NotImplemented)
                problems++;
        }

        foreach (var (t, plan, err) in planned)
        {
            if (plan is null)
            {
                Console.WriteLine($"{t.Name}: could not be read — {err}");
                problems++;
                continue;
            }
            PrintPlan(t.Name, plan);
            if (plan.Before.Supported && !plan.Allowed)
                problems++;
        }

        var writable = planned
            .Where(p => p.Plan is { HasWork: true } && p.t.Client is IIpFilterWriter)
            .ToList();
        if (writable.Count == 0)
        {
            Console.WriteLine("\nNothing to write.");
            return problems > 0 ? 1 : 0;
        }

        if (!ShouldWrite(opts))
        {
            Console.WriteLine($"\nDRY RUN — nothing was written to {writable.Count} recorder(s). " +
                "Re-run with --force to apply.");
            return problems > 0 ? 1 : 0;
        }

        Console.WriteLine();
        foreach (var (t, plan, _) in writable)
        {
            try
            {
                var change = await ((IIpFilterWriter)t.Client).ApplyIpFilterAsync(plan!, ct);
                Console.WriteLine($"{t.Name}: " + DescribeChange(change));
                if (change.Rejected)
                    problems++;
            }
            catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
            {
                Console.WriteLine($"{t.Name}: FAILED — {ex.Message}");
                problems++;
            }
        }
        return problems > 0 ? 1 : 0;
    }

    private static void PrintPlan(string name, IpFilterPlan plan)
    {
        Console.WriteLine($"{name}: {plan.Before.Summary}");
        if (!plan.Before.Supported)
        {
            Console.WriteLine("  skipped — this firmware has no IP filter.");
            return;
        }
        if (plan.Blocked is not null)
        {
            Console.WriteLine($"  REFUSED — {plan.Blocked}");
            return;
        }
        foreach (var d in plan.Decisions)
        {
            string line = d.Action switch
            {
                IpFilterAction.Add => $"  + block    {d.Address}",
                IpFilterAction.Remove => $"  - unblock  {d.Address}",
                IpFilterAction.AlreadyPresent => $"  = already  {d.Address}",
                IpFilterAction.NotPresent => $"  = not listed {d.Address}",
                _ => $"  ! REFUSED  {d.Asked} — {d.Reason}",
            };
            Console.WriteLine(line);
        }
        if (plan.EnabledAfter != plan.Before.Enabled)
            Console.WriteLine($"  filter {(plan.EnabledAfter ? "ON" : "OFF")}");
        if (!plan.Allowed)
            Console.WriteLine("  nothing will be written to this recorder: a plan is applied as reviewed or not at all.");
        else if (plan.InertAfter && plan.Adds.Any())
            Console.WriteLine("  note: the filter stays OFF, so these addresses will be listed and NOT blocked " +
                "(add --enable to turn it on).");
        else if (!plan.HasWork)
            Console.WriteLine("  nothing to change.");
    }

    private static string DescribeChange(IpFilterChange change)
    {
        var parts = new List<string>();
        var added = change.Added.ToList();
        var removed = change.Removed.ToList();
        if (added.Count > 0)
            parts.Add($"blocked {string.Join(", ", added)}");
        if (removed.Count > 0)
            parts.Add($"unblocked {string.Join(", ", removed)}");
        if (change.Before.Enabled != change.After.Enabled)
            parts.Add($"filter turned {(change.After.Enabled ? "on" : "off")}");
        string text = parts.Count > 0 ? string.Join("; ", parts) : "unchanged";
        text += $" — now {change.After.Summary}";
        if (change.Rejected)
            text = "REJECTED — the recorder does not hold what was asked (" + change.Note + "). " + text;
        return text;
    }

    /// <summary>Every --ip and every line of --from-file, in order, blanks and comments dropped.</summary>
    private static List<string> ReadAddresses(Dictionary<string, string> opts)
    {
        var all = Split(opts.GetValueOrDefault("ip", "")).ToList();
        if (opts.TryGetValue("from-file", out string? path) && path.Length > 0)
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw;
                int hash = line.IndexOf('#');
                if (hash >= 0)
                    line = line[..hash];
                all.AddRange(Split(line));
            }
        }
        return all;
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(['\u001f', ',', ' ', '\t', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
