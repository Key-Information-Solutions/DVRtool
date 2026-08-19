using System.Globalization;
using DVRTool.Core;
using DVRTool.Vendors.HikvisionAccess;

namespace DVRTool.Cli;

/// <summary>
/// The <c>access</c> command group: door-access panels rather than video recorders.
/// </summary>
/// <remarks>
/// Kept apart from the video verbs because an access panel is not an <see cref="INvrClient"/>
/// and shares no connection settings with one — different port, different protocol, its own
/// <c>OCB_*</c> credentials.
/// </remarks>
internal static class AccessCommands
{
    internal const string Usage = """
        dvrtool access — door-access control panels (Hikvision DS-K / OEM "OCB")

        Usage:
          dvrtool access <subcommand> [options]

        Subcommands:
          panels     Probe each panel: model, firmware, card count, capabilities
          cards      List the credentials on one panel
          roster     Unified cross-panel view of every fob and the doors it opens
          find       Locate a fob (--card) or cardholder (--name) across the fleet
          compare    Which fobs one panel has that another is missing
          export     Write the roster to CSV
          grant      Create or update a fob's door rights          (needs --force)
          revoke     Invalidate a fob — the device's own delete     (needs --force)

        Options:
          --panels <ip[,ip...]>   panels to talk to, or OCB_PANELS
          --panel <ip|all>        restrict to one panel (default: all)
          --user <name>           or OCB_USER
          --pass <password>       or OCB_PASS; omit both to be prompted
          --port <n>              SDK port, or OCB_SDK_PORT (default 8000)
          --sdk-dir <path>        folder holding HCNetSDK.dll, or OCB_SDK_DIR
          --card <no>             fob number
          --name <text>           cardholder name (substring, case-insensitive)
          --doors <n[,n...]|all>  doors a granted fob opens (grant)
          --valid-from <time>     "yyyy-MM-dd HH:mm[:ss]" (panel-local); default now
          --valid-until <time>    "yyyy-MM-dd HH:mm[:ss]" (panel-local)
          --against <ip>          the panel to compare against (compare)
          --out <file>            CSV target (export)
          --force                 actually perform a write; without it, grant/revoke
                                  only report what they would change
          --env <path>            .env file to load (default: .env in the working dir)

        Writes touch physical doors. grant/revoke re-read the fob afterwards and print
        the verified state, and refuse to act at all without --force.

        These panels speak only the Hikvision SDK on port 8000 (no HTTP), so this needs
        Windows, a 64-bit process, and the SDK (iVMS-4200 or HikCentral Lite bundle it).
        """;

    internal static async Task<int> RunAsync(string subcommand, Dictionary<string, string> opts,
        CancellationToken ct)
    {
        // `access --help` leaves the subcommand empty and lands "help" in the options, so
        // asking for help that way must still succeed rather than look like a usage error.
        bool askedForHelp = subcommand is "-h" or "--help" or "help" ||
                            opts.ContainsKey("help") || opts.ContainsKey("h");
        if (askedForHelp || subcommand.Length == 0)
        {
            Console.WriteLine(Usage);
            return askedForHelp ? 0 : 2;
        }

        return subcommand switch
        {
            "panels" => await PanelsAsync(opts, ct),
            "cards" => await CardsAsync(opts, ct),
            "roster" => await RosterAsync(opts, ct),
            "find" => await FindAsync(opts, ct),
            "compare" => await CompareAsync(opts, ct),
            "export" => await ExportAsync(opts, ct),
            "grant" => await GrantAsync(opts, ct),
            "revoke" => await RevokeAsync(opts, ct),
            _ => UnknownSubcommand(subcommand),
        };
    }

    private static int UnknownSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"error: unknown access subcommand '{subcommand}'\n");
        Console.WriteLine(Usage);
        return 2;
    }

    // ---------- reads ----------

    private static async Task<int> PanelsAsync(Dictionary<string, string> opts, CancellationToken ct)
    {
        var settings = AccessSettings.From(opts);
        Console.WriteLine($"{"PANEL",-16} {"MODEL",-24} {"FIRMWARE",-12} {"DOORS",-6} {"CARDS",-6} NAMES");

        int failures = 0;
        foreach (string host in settings.Panels)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var client = settings.Connect(host);
                var info = await client.GetDeviceInfoAsync(ct);
                var caps = await client.GetCapabilitiesAsync(ct);
                var cards = await client.GetCardsAsync(ct);
                Console.WriteLine(
                    $"{host,-16} {info.Model,-24} {info.FirmwareVersion,-12} " +
                    $"{caps.DoorCount?.ToString() ?? "?",-6} {cards.Count,-6} " +
                    (caps.SupportsCardholderNames ? "yes" : "NOT STORED"));
            }
            catch (Exception ex) when (ex is NvrException or ArgumentException)
            {
                failures++;
                Console.WriteLine($"{host,-16} ERROR: {ex.Message}");
            }
        }

        if (failures > 0)
            Console.Error.WriteLine($"\n{failures} of {settings.Panels.Count} panel(s) could not be read.");
        return failures == settings.Panels.Count ? 1 : 0;
    }

    private static async Task<int> CardsAsync(Dictionary<string, string> opts, CancellationToken ct)
    {
        var settings = AccessSettings.From(opts);
        var roster = await BuildRosterAsync(settings, ct);
        ReportFailures(roster);

        foreach (var panel in roster.Panels.Where(p => p.Ok))
        {
            Console.WriteLine($"\n=== {panel.PanelHost} — {panel.Cards.Count} credential(s) ===");
            if (panel.Cards.Count == 0)
                continue;
            Console.WriteLine($"{"CARD",-14} {"DOORS",-12} {"TYPE",-10} {"VALID",-7} {"UNTIL",-19} NAME");
            foreach (var c in panel.Cards.OrderBy(c => AccessRoster.NormalizeCardNo(c.CardNo).PadLeft(20)))
                Console.WriteLine(
                    $"{c.CardNo,-14} {c.DoorSummary,-12} {c.Type,-10} " +
                    $"{(c.Valid ? "yes" : "REVOKED"),-7} " +
                    $"{c.ValidUntil?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",-19} {c.Name}");
        }

        return roster.Panels.All(p => !p.Ok) ? 1 : 0;
    }

    private static async Task<int> RosterAsync(Dictionary<string, string> opts, CancellationToken ct)
    {
        var settings = AccessSettings.From(opts);
        var roster = await BuildRosterAsync(settings, ct);
        ReportFailures(roster);

        var panels = roster.Panels.Where(p => p.Ok).Select(p => p.PanelHost).ToList();
        if (panels.Count == 0)
            return 1;

        Console.WriteLine($"\n{roster.Entries.Count} distinct fob(s) across {panels.Count} panel(s)\n");
        Console.Write($"{"CARD",-14}");
        foreach (string p in panels)
            Console.Write($" {Shorten(p),-14}");
        Console.WriteLine("  NAME");

        foreach (var entry in roster.Entries)
        {
            Console.Write($"{entry.CardNo,-14}");
            foreach (string p in panels)
            {
                var presence = entry.Presence.FirstOrDefault(x =>
                    string.Equals(x.PanelHost, p, StringComparison.OrdinalIgnoreCase));
                string cell = presence is null
                    ? "-"
                    : presence.Valid ? $"doors {presence.Doors.Count switch
                    {
                        0 => "none",
                        _ => string.Join("/", presence.Doors),
                    }}" : "REVOKED";
                Console.Write($" {cell,-14}");
            }
            Console.WriteLine($"  {entry.Name}");
        }

        Console.WriteLine();
        foreach (string p in panels)
        {
            var only = roster.OnlyOn(p);
            Console.WriteLine($"  {p}: {roster.Entries.Count(e => e.Panels.Contains(p, StringComparer.OrdinalIgnoreCase))} fob(s)" +
                (only.Count > 0 ? $", {only.Count} found nowhere else" : ""));
        }

        if (!roster.AnyPanelStoresNames)
            Console.WriteLine("\n" + NoNamesNotice);
        return 0;
    }

    private static async Task<int> FindAsync(Dictionary<string, string> opts, CancellationToken ct)
    {
        string? card = Value(opts, "card");
        string? name = Value(opts, "name");
        if (card is null && name is null)
            throw new ArgumentException("give --card <no> or --name <text>");

        var settings = AccessSettings.From(opts);
        var roster = await BuildRosterAsync(settings, ct);
        ReportFailures(roster);
        if (roster.Panels.All(p => !p.Ok))
            return 1;

        if (card is not null)
        {
            var entry = roster.FindCard(card);
            if (entry is null)
            {
                Console.WriteLine($"fob {card}: not present on any panel read.");
                // A partial roster cannot support "this fob has no access" as a conclusion.
                return roster.IsPartial ? 1 : 0;
            }
            PrintEntry(entry);
            return 0;
        }

        if (!roster.AnyPanelStoresNames)
        {
            Console.Error.WriteLine(
                $"error: cannot search by name — {NoNamesNotice}");
            return 2;
        }

        var hits = roster.FindByName(name!);
        Console.WriteLine($"{hits.Count} match(es) for '{name}'.");
        foreach (var hit in hits)
            PrintEntry(hit);
        return 0;
    }

    private static async Task<int> CompareAsync(Dictionary<string, string> opts, CancellationToken ct)
    {
        string primary = Value(opts, "panel")
            ?? throw new ArgumentException("compare needs --panel <ip>");
        string against = Value(opts, "against")
            ?? throw new ArgumentException("compare needs --against <ip>");

        var settings = AccessSettings.From(opts) with { Panels = [primary, against] };
        var roster = await BuildRosterAsync(settings, ct);
        ReportFailures(roster);
        if (roster.IsPartial)
        {
            Console.Error.WriteLine("error: refusing to report drift from a partial read.");
            return 1;
        }

        var missingHere = roster.MissingFrom(primary, against);
        var missingThere = roster.MissingFrom(against, primary);

        Console.WriteLine($"On {against} but not {primary}: {missingHere.Count}");
        foreach (var e in missingHere)
            Console.WriteLine($"  {e.CardNo,-14} {e.Name}");
        Console.WriteLine($"\nOn {primary} but not {against}: {missingThere.Count}");
        foreach (var e in missingThere)
            Console.WriteLine($"  {e.CardNo,-14} {e.Name}");
        return 0;
    }

    private static async Task<int> ExportAsync(Dictionary<string, string> opts, CancellationToken ct)
    {
        var settings = AccessSettings.From(opts);
        var roster = await BuildRosterAsync(settings, ct);
        ReportFailures(roster);
        if (roster.Panels.All(p => !p.Ok))
            return 1;

        string path = Value(opts, "out") ?? "access-roster.csv";
        bool force = opts.ContainsKey("force");
        DownloadPaths.EnsureNotOverwriting(path, force, DownloadPaths.CliOverwriteAdvice);

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("card_no,panel,doors,valid,valid_from,valid_until,card_type,name,employee_no");
        foreach (var entry in roster.Entries)
        {
            foreach (var p in entry.Presence)
            {
                var card = roster.Panels
                    .FirstOrDefault(x => string.Equals(x.PanelHost, p.PanelHost, StringComparison.OrdinalIgnoreCase))
                    ?.Cards.FirstOrDefault(c =>
                        AccessRoster.NormalizeCardNo(c.CardNo) == AccessRoster.NormalizeCardNo(entry.CardNo));
                await writer.WriteLineAsync(string.Join(",",
                    Csv(entry.CardNo), Csv(p.PanelHost), Csv(string.Join("/", p.Doors)),
                    p.Valid ? "yes" : "no",
                    Csv(card?.ValidFrom?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""),
                    Csv(card?.ValidUntil?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""),
                    Csv(card?.Type.ToString() ?? ""), Csv(entry.Name ?? ""),
                    (card?.EmployeeNo ?? 0).ToString(CultureInfo.InvariantCulture)));
            }
        }

        Console.WriteLine($"wrote {path} ({roster.Entries.Count} fob(s))");
        if (roster.IsPartial)
            Console.Error.WriteLine("warning: the export is PARTIAL — see the panel errors above.");
        return roster.IsPartial ? 1 : 0;
    }

    // ---------- writes ----------

    private static async Task<int> GrantAsync(Dictionary<string, string> opts, CancellationToken ct)
    {
        string card = Value(opts, "card") ?? throw new ArgumentException("grant needs --card <no>");
        var settings = AccessSettings.From(opts);
        if (settings.Panels.Count != 1)
            throw new ArgumentException(
                "grant writes to exactly one panel — name it with --panel <ip>. Door numbers " +
                "are per-panel, so a fleet-wide grant would mean different doors on each.");

        string host = settings.Panels[0];
        bool force = opts.ContainsKey("force");

        using var client = settings.Connect(host);
        var caps = await client.GetCapabilitiesAsync(ct);
        var doors = ParseDoors(Value(opts, "doors"), caps.DoorCount);
        var existing = await client.GetCardAsync(card, ct);

        Console.WriteLine($"panel {host}");
        Console.WriteLine(existing is null
            ? $"  before: fob {card} is not on this panel"
            : $"  before: fob {card} doors={existing.DoorSummary} valid={(existing.Valid ? "yes" : "no")}");
        Console.WriteLine($"  after:  fob {card} doors={string.Join(",", doors)} valid=yes");

        DateTime? from = Value(opts, "valid-from") is string f ? ParseTime(f) : null;
        DateTime? until = Value(opts, "valid-until") is string u ? ParseTime(u) : null;
        if (until is not null)
        {
            from ??= DateTime.Now;
            Console.WriteLine($"  valid:  {from:yyyy-MM-dd HH:mm:ss} → {until:yyyy-MM-dd HH:mm:ss} (panel-local)");
        }
        else if (from is not null)
        {
            throw new ArgumentException("--valid-from also needs --valid-until");
        }
        else if (existing?.ValidUntil is null)
        {
            // Every fob provisioned on these panels by iVMS carries a window (typically ten
            // years). A credential with none never expires on its own, so the only thing that
            // ever removes it is somebody remembering to — which is what offboarding misses.
            Console.WriteLine(
                "  note:   no validity window — this fob will not expire on its own. " +
                "Pass --valid-until to bound it.");
        }

        if (!force)
        {
            Console.Error.WriteLine(
                "\nrefused: this would change physical door access. Re-run with --force to apply.");
            return 2;
        }

        var wanted = (existing ?? new AccessCard { CardNo = card }) with
        {
            CardNo = card,
            Valid = true,
            Doors = doors,
            ValidFrom = from ?? existing?.ValidFrom,
            ValidUntil = until ?? existing?.ValidUntil,
            Name = Value(opts, "name") ?? existing?.Name,
        };

        await client.UpsertCardAsync(wanted, ct);
        return await VerifyAsync(client, card, host, expectValid: true, doors, ct);
    }

    private static async Task<int> RevokeAsync(Dictionary<string, string> opts, CancellationToken ct)
    {
        string card = Value(opts, "card") ?? throw new ArgumentException("revoke needs --card <no>");
        var settings = AccessSettings.From(opts);
        bool force = opts.ContainsKey("force");

        // Find where it actually is first, so revoking touches only panels that hold it.
        var present = new List<string>();
        int unreadable = 0;
        foreach (string host in settings.Panels)
        {
            try
            {
                using var probe = settings.Connect(host);
                var found = await probe.GetCardAsync(card, ct);
                Console.WriteLine(found is null
                    ? $"  {host}: fob {card} not present"
                    : $"  {host}: fob {card} doors={found.DoorSummary} valid={(found.Valid ? "yes" : "no")}");
                if (found is not null && found.Valid)
                    present.Add(host);
            }
            catch (Exception ex) when (ex is NvrException or ArgumentException)
            {
                unreadable++;
                Console.Error.WriteLine($"  {host}: ERROR {ex.Message}");
            }
        }

        if (unreadable > 0)
            Console.Error.WriteLine(
                $"warning: {unreadable} panel(s) could not be read — this fob may still be " +
                "active on them.");

        if (present.Count == 0)
        {
            Console.WriteLine($"\nfob {card} holds no active access on the panels read.");
            return unreadable > 0 ? 1 : 0;
        }

        Console.WriteLine($"\nwould revoke fob {card} on: {string.Join(", ", present)}");
        if (!force)
        {
            Console.Error.WriteLine(
                "refused: this would remove physical door access. Re-run with --force to apply.");
            return 2;
        }

        int failed = 0;
        foreach (string host in present)
        {
            using var client = settings.Connect(host);
            await client.RevokeCardAsync(card, ct);
            if (await VerifyAsync(client, card, host, expectValid: false, [], ct) != 0)
                failed++;
        }
        return failed == 0 && unreadable == 0 ? 0 : 1;
    }

    /// <summary>
    /// Reads the credential back after a write and reports what the device now holds.
    /// </summary>
    /// <remarks>
    /// A write that the SDK acknowledged is not proof the door changed. Verifying is the
    /// difference between "we sent it" and "it is so".
    /// </remarks>
    private static async Task<int> VerifyAsync(IAccessControlClient client, string card,
        string host, bool expectValid, IReadOnlyList<int> expectedDoors, CancellationToken ct)
    {
        var after = await client.GetCardAsync(card, ct);

        if (!expectValid)
        {
            bool gone = after is null || !after.Valid;
            Console.WriteLine(gone
                ? $"  {host}: verified — fob {card} is {(after is null ? "gone" : "revoked")}."
                : $"  {host}: NOT VERIFIED — fob {card} still reads valid " +
                  $"(doors={after!.DoorSummary}).");
            return gone ? 0 : 1;
        }

        if (after is null)
        {
            Console.Error.WriteLine($"  {host}: NOT VERIFIED — fob {card} is absent after the write.");
            return 1;
        }

        bool doorsMatch = expectedDoors.OrderBy(d => d).SequenceEqual(after.Doors.OrderBy(d => d));
        Console.WriteLine(
            $"  {host}: after write — fob {after.CardNo} doors={after.DoorSummary} " +
            $"valid={(after.Valid ? "yes" : "no")}" +
            (after.ValidUntil is DateTime vu ? $" until={vu:yyyy-MM-dd HH:mm:ss}" : "") +
            (after.Name is { Length: > 0 } n ? $" name='{n}'" : ""));

        if (!after.Valid || !doorsMatch)
        {
            Console.Error.WriteLine(
                $"  {host}: NOT VERIFIED — expected doors {string.Join(",", expectedDoors)} and valid=yes.");
            return 1;
        }
        Console.WriteLine($"  {host}: verified.");
        return 0;
    }

    // ---------- shared plumbing ----------

    private const string NoNamesNotice =
        "these panels store no cardholder identity: every credential is a fob number plus " +
        "door rights, and the card→name channel is unsupported on this firmware. Names live " +
        "only in whatever provisioned the fobs (iVMS-4200).";

    /// <summary>Reads every panel, keeping per-panel failures instead of dropping them.</summary>
    private static async Task<AccessRoster> BuildRosterAsync(AccessSettings settings,
        CancellationToken ct)
    {
        var results = new List<AccessPanelResult>();
        foreach (string host in settings.Panels)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var client = settings.Connect(host);
                var info = await client.GetDeviceInfoAsync(ct);
                var cards = await client.GetCardsAsync(ct);
                results.Add(new AccessPanelResult
                {
                    PanelHost = host,
                    Serial = info.SerialNumber,
                    Cards = cards,
                });
            }
            catch (Exception ex) when (ex is NvrException or ArgumentException)
            {
                results.Add(AccessPanelResult.Failed(host, ex.Message));
            }
        }
        return AccessRoster.Build(results);
    }

    private static void ReportFailures(AccessRoster roster)
    {
        foreach (var panel in roster.FailedPanels)
            Console.Error.WriteLine($"error: {panel.PanelHost}: {panel.Error}");
        if (roster.IsPartial)
            Console.Error.WriteLine(
                "warning: this view is PARTIAL — a fob could still be active on a panel that " +
                "could not be read, so \"no access\" is not a safe conclusion here.");
    }

    private static void PrintEntry(RosterEntry entry)
    {
        Console.WriteLine($"\nfob {entry.CardNo}{(entry.Name is { Length: > 0 } n ? $" — {n}" : "")}");
        foreach (var p in entry.Presence)
            Console.WriteLine($"  {p.PanelHost,-16} doors={(p.Doors.Count == 0 ? "none" : string.Join(",", p.Doors)),-12} " +
                $"{(p.Valid ? "active" : "REVOKED")}" +
                (p.ValidUntil is DateTime vu ? $"  until {vu:yyyy-MM-dd HH:mm:ss}" : ""));
        if (entry.FullyRevoked)
            Console.WriteLine("  (revoked everywhere it appears)");
    }

    private static IReadOnlyList<int> ParseDoors(string? value, int? doorCount)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("grant needs --doors <n[,n...]> or --doors all");

        if (value.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (doorCount is not int n)
                throw new ArgumentException(
                    "--doors all needs a known door count, and this panel's model was not " +
                    "recognized. List the doors explicitly, e.g. --doors 1,2,3,4");
            return Enumerable.Range(1, n).ToList();
        }

        var doors = new List<int>();
        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                 StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int d) || d < 1)
                throw new ArgumentException($"invalid door '{part}' in --doors");
            if (doorCount is int max && d > max)
                throw new ArgumentException(
                    $"door {d} is beyond this panel's {max} door(s)");
            doors.Add(d);
        }
        return doors.Distinct().OrderBy(d => d).ToList();
    }

    internal static DateTime ParseTime(string value)
    {
        string[] formats =
        [
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
            "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm",
        ];
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var t))
            return DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
        throw new ArgumentException($"can't parse time '{value}' (use \"yyyy-MM-dd HH:mm[:ss]\")");
    }

    private static string? Value(Dictionary<string, string> opts, string key) =>
        opts.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    private static string Shorten(string host)
    {
        // Panels usually differ only in the last octet; keep the table narrow but unambiguous.
        int lastDot = host.LastIndexOf('.');
        return lastDot > 0 && host.Length > 10 ? "…" + host[lastDot..] : host;
    }

    private static string Csv(string field) =>
        field.Contains(',') || field.Contains('"') || field.Contains('\n')
            ? '"' + field.Replace("\"", "\"\"") + '"'
            : field;
}

/// <summary>Resolved connection settings shared by every <c>access</c> subcommand.</summary>
internal sealed record AccessSettings
{
    internal required IReadOnlyList<string> Panels { get; init; }
    internal required string Username { get; init; }
    internal required string Password { get; init; }
    internal required int SdkPort { get; init; }
    internal string? SdkDirectory { get; init; }

    internal static AccessSettings From(Dictionary<string, string> opts)
    {
        string list = Opt(opts, "panels")
            ?? Environment.GetEnvironmentVariable("OCB_PANELS")
            ?? throw new ArgumentException("missing --panels (or OCB_PANELS in env/.env)");

        var panels = list
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // --panel narrows the set; "all" is the explicit spelling of the default.
        if (Opt(opts, "panel") is string only &&
            !only.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            panels = [only];
        }

        if (panels.Count == 0)
            throw new ArgumentException("no panels to talk to");

        string user = Opt(opts, "user")
            ?? Environment.GetEnvironmentVariable("OCB_USER")
            ?? throw new ArgumentException("missing --user (or OCB_USER in env/.env)");

        int port = 8000;
        string? portText = Opt(opts, "port") ?? Environment.GetEnvironmentVariable("OCB_SDK_PORT");
        if (portText is not null &&
            (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
             port is < 1 or > 65535))
            throw new ArgumentException($"invalid SDK port '{portText}'");

        return new AccessSettings
        {
            Panels = panels,
            Username = user,
            Password = ResolvePassword(opts, user, panels[0]),
            SdkPort = port,
            SdkDirectory = Opt(opts, "sdk-dir") ?? Environment.GetEnvironmentVariable("OCB_SDK_DIR"),
        };
    }

    private static string ResolvePassword(Dictionary<string, string> opts, string user, string host)
    {
        if (Opt(opts, "pass") is string p)
            return p;
        if (Environment.GetEnvironmentVariable("OCB_PASS") is { Length: > 0 } env)
            return env;
        // Never guess: these panels lock out the calling IP after a few bad logins, and
        // iVMS-4200 typically shares that IP.
        return ConsolePrompt.ReadSecret(
            $"Password for {user}@{host} (access panel): ",
            "missing --pass (or OCB_PASS in env/.env)");
    }

    internal IAccessControlClient Connect(string host)
    {
        // The driver is a P/Invoke wrapper over Hikvision's Windows-only HCNetSDK; there is
        // no cross-platform path to these panels at all (they expose no HTTP interface).
        if (!OperatingSystem.IsWindows())
            throw new NvrException(
                "access-control panels need Hikvision's HCNetSDK, which is Windows-only.");

        return new HikvisionAccessClient(
            new AccessPanelConnection
            {
                Host = host,
                SdkPort = SdkPort,
                Username = Username,
                Password = Password,
            },
            SdkDirectory);
    }

    private static string? Opt(Dictionary<string, string> opts, string key) =>
        opts.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
}
