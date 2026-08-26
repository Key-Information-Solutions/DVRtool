using System.Globalization;
using DVRTool.Core;
using DVRTool.Vendors.HikvisionAccess;
using DVRTool.Vendors.HikvisionIvms;

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
          identity   Import cardholder names from iVMS to enrich the roster (see below)
          reconcile  READ-ONLY drift: policy's expected per-panel cards vs the live panels
          onboard    Provision a new hire's fob from group membership   (--dry-run default)
          offboard   Revoke a departed person's fob on every panel       (--dry-run default)
          grant      Create or update a fob's door rights          (needs --force)
          revoke     Invalidate a fob — the device's own delete     (needs --force)

        reconcile / onboard / offboard are policy-driven (handoff §4-§5). The policy is the
        iVMS-extracted access-control-policy.json: each group -> which panels/doors + schedule.
        A person in several groups gets, per panel, the UNION of every group's doors. Grants
        are written 24/7 (right-plan 1); a group whose schedule is not 24/7 is provisioned
        always-on WITH A WARNING (a faithful schedule writer is a later task).

        The panels store no cardholder names. Once names are imported from iVMS they are
        cached in a DVRTool-owned map and applied AUTOMATICALLY to roster, cards, find,
        compare and export — no flag needed. Import is one-way (iVMS → DVRTool only).

        identity options (one-way name enrichment):
          --import-ivms           read the iVMS SQLCipher DB and decode each card to its fob
                                  directly — complete, and needs no panels. Add --panels to
                                  also expiry-correlate any card that does not decode (the
                                  rare high/5-digit variant)
          --import-csv <file>     import an iVMS Person export (plaintext card numbers), if
                                  your iVMS build offers one — also complete
          --ivms-db <path>        the iVMS person DB (or its UserData root); default:
                                  auto-discover the local install
          --db-key <b64>          the per-install SQLCipher key, base64 (or IVMS_DB_KEY,
                                  or the cached key from --capture-key)
          --capture-key <b64>     cache a per-install DB key that an external one-time
                                  step captured (DVRTool never derives it)
          --where                 show the name-map's location and current contents
          --clear                 delete the cached name map

        Options:
          --panels <ip[:port][,…]>  panels to talk to, or OCB_PANELS. Give a port per
                                    entry when several panels sit behind one address:
                                    --panels 10.0.0.5,10.0.0.5:8001
          --panel <ip[:port]|all>   restrict to one panel (default: all)
          --user <name>           or OCB_USER
          --pass <password>       or OCB_PASS; omit both to be prompted
          --port <n>              default SDK port for entries without one, or
                                  OCB_SDK_PORT (default 8000)
          --sdk-dir <path>        folder holding HCNetSDK.dll, or OCB_SDK_DIR
          --card <no>             fob number (onboard: the PHYSICAL fob, an input)
          --name <text>           cardholder name; grant/find substring, onboard/offboard
                                  exact (First.Last, matched against the imported map)
          --doors <n[,n...]|all>  doors a granted fob opens (grant)
          --valid-from <time>     "yyyy-MM-dd HH:mm[:ss]" (panel-local); default now
          --valid-until <time>    "yyyy-MM-dd[ HH:mm[:ss]]" (panel-local); onboard default
                                  is a bounded ~10-year window
          --group <name>          access group for onboard; repeat for several
                                  (--group Employes --group IT) or comma-separate
          --policy <path>         access-control-policy.json (reconcile/onboard), or OCB_POLICY
          --against <ip>          the panel to compare against (compare)
          --out <file>            CSV target (export)
          --dry-run               reconcile/onboard/offboard: print the writes without making
                                  them. THIS IS THE DEFAULT — provisioning never writes unless
                                  --force is given and --dry-run is not
          --force                 actually perform a write; without it, grant/revoke/onboard/
                                  offboard only report what they would change
          --env <path>            .env file to load (default: .env in the working dir)

        Writes touch physical doors. grant/revoke re-read the fob afterwards and print
        the verified state, and refuse to act at all without --force.

        Every panel is identified before it is read or written: the first login to an
        address records the controller's serial, and a later login that answers with a
        different one is refused rather than acted on. This is the check a password
        cannot do — one account across the fleet means the wrong port logs in cleanly,
        and a grant sent there is a working fob on someone else's building.

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
            "identity" => await IdentityAsync(opts, ct),
            "reconcile" => await ReconcileAsync(opts, ct),
            "onboard" => await OnboardAsync(opts, ct),
            "offboard" => await OffboardAsync(opts, ct),
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
        var seen = new List<FleetRecord>();
        var notes = new List<string>();
        foreach (var panel in settings.Panels)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (client, info, identity) = await settings.ConnectVerifiedAsync(panel, ct);
                using (client)
                {
                    var caps = await client.GetCapabilitiesAsync(ct);
                    var cards = await client.GetCardsAsync(ct);
                    Console.WriteLine(
                        $"{panel.Label,-16} {info.Model,-24} {info.FirmwareVersion,-12} " +
                        $"{caps.DoorCount?.ToString() ?? "?",-6} {cards.Count,-6} " +
                        (caps.SupportsCardholderNames ? "yes" : "NOT STORED"));
                }
                seen.Add(new FleetRecord(panel.Label, panel.Host, panel.SdkPort, info.SerialNumber));
                if (identity.Message.Length > 0)
                    notes.Add(identity.Message);
                if (identity.Verdict == IdentityVerdict.Mismatch)
                    failures++;
            }
            catch (Exception ex) when (ex is NvrException or ArgumentException)
            {
                failures++;
                Console.WriteLine($"{panel.Label,-16} ERROR: {ex.Message}");
            }
        }

        // `panels` is the survey verb, so it reports what the fleet looks like as a whole —
        // including two addresses that turn out to be one controller, which no single panel's
        // own row can show.
        foreach (string note in notes)
            Console.Error.WriteLine($"\n{note}");
        foreach (var issue in FleetAudit.Inspect(seen)
            .Where(i => i.Severity >= FleetIssueSeverity.Warning))
            Console.Error.WriteLine($"\nwarning: {issue.Message}");

        if (failures > 0)
            Console.Error.WriteLine(
                $"\n{failures} of {settings.Panels.Count} panel(s) could not be read or were not " +
                "the expected controller.");
        return failures == settings.Panels.Count ? 1 : 0;
    }

    private static async Task<int> CardsAsync(Dictionary<string, string> opts, CancellationToken ct)
    {
        var settings = AccessSettings.From(opts);
        var roster = await BuildEnrichedRosterAsync(settings, ct);
        ReportFailures(roster);

        foreach (var panel in roster.Panels.Where(p => p.Ok))
        {
            Console.WriteLine($"\n=== {panel.PanelHost} — {panel.Cards.Count} credential(s) ===");
            if (panel.Cards.Count == 0)
                continue;
            Console.WriteLine($"{"CARD",-14} {"DOORS",-12} {"TYPE",-10} {"VALID",-7} {"UNTIL",-19} NAME");
            foreach (var c in panel.Cards.OrderBy(c => AccessRoster.NormalizeCardNo(c.CardNo).PadLeft(20)))
                // The panel's own name is blank on this firmware; fall back to the enriched
                // roster entry so an imported iVMS name shows here too.
                Console.WriteLine(
                    $"{c.CardNo,-14} {c.DoorSummary,-12} {c.Type,-10} " +
                    $"{(c.Valid ? "yes" : "REVOKED"),-7} " +
                    $"{c.ValidUntil?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",-19} " +
                    (string.IsNullOrWhiteSpace(c.Name) ? roster.FindCard(c.CardNo)?.Name : c.Name));
        }

        return roster.Panels.All(p => !p.Ok) ? 1 : 0;
    }

    private static async Task<int> RosterAsync(Dictionary<string, string> opts, CancellationToken ct)
    {
        var settings = AccessSettings.From(opts);
        var roster = await BuildEnrichedRosterAsync(settings, ct);
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

        if (!roster.HasNames)
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
        var roster = await BuildEnrichedRosterAsync(settings, ct);
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

        if (!roster.HasNames)
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
        string primaryText = Value(opts, "panel")
            ?? throw new ArgumentException("compare needs --panel <ip>");
        string againstText = Value(opts, "against")
            ?? throw new ArgumentException("compare needs --against <ip>");

        // Both sides go through the same parse as the panel list, so the labels the roster is
        // keyed by are the labels compared against here — the two panels of a same-address
        // pair differ only by port, and a bare host would silently mean "the one on 8000".
        var fleet = AccessSettings.From(opts);
        var primaryTarget = PanelTarget.Parse(primaryText, fleet.SdkPort);
        var againstTarget = PanelTarget.Parse(againstText, fleet.SdkPort);
        if (string.Equals(primaryTarget.Address, againstTarget.Address, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"--panel and --against are the same panel ({primaryTarget.Address}) — a " +
                "compare against itself always reports no drift.");

        string primary = primaryTarget.Label;
        string against = againstTarget.Label;
        var settings = fleet with { Panels = [primaryTarget, againstTarget] };
        var roster = await BuildEnrichedRosterAsync(settings, ct);
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
        var roster = await BuildEnrichedRosterAsync(settings, ct);
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

    // ---------- identity (one-way iVMS → DVRTool name enrichment) ----------

    /// <summary>
    /// The <c>identity</c> subcommand: import cardholder names from iVMS and manage the
    /// cached name map. Option-driven so it stays one dispatch token in Program.cs.
    /// </summary>
    /// <remarks>
    /// Everything here is one-way (iVMS → DVRTool). Nothing writes back into iVMS, and the
    /// DB key is only ever supplied by the operator — never derived or embedded.
    /// </remarks>
    private static async Task<int> IdentityAsync(Dictionary<string, string> opts, CancellationToken ct)
    {
        if (Value(opts, "capture-key") is string captured)
            return CaptureKey(opts, captured);
        if (Value(opts, "import-csv") is string csv)
            return ImportCsv(csv);
        if (opts.ContainsKey("import-ivms"))
            return await ImportIvmsAsync(opts, ct);
        if (opts.ContainsKey("where"))
            return IdentityWhere();
        if (opts.ContainsKey("clear"))
            return IdentityClear();

        Console.Error.WriteLine(
            "error: `access identity` needs one of --import-csv, --import-ivms, " +
            "--capture-key, --where or --clear.\n");
        Console.WriteLine(Usage);
        return 2;
    }

    private static int ImportCsv(string csvPath)
    {
        if (!File.Exists(csvPath))
            throw new ArgumentException($"CSV file not found: {csvPath}");

        var imported = IvmsCsvImporter.Import(csvPath);
        var merged = MergeIntoStore(imported);
        Console.WriteLine(
            $"imported {imported.Count} name(s) from {csvPath}; " +
            $"map now holds {merged.Count} (saved to {IdentityMapStore.DefaultPath}).");
        return 0;
    }

    private static async Task<int> ImportIvmsAsync(Dictionary<string, string> opts,
        CancellationToken ct)
    {
        var install = ResolveInstall(opts);
        byte[] key = IvmsKeyStore.Resolve(Value(opts, "db-key"), install.Id);

        var rows = IvmsPersonReader.ReadCardholders(install.PersonDbPath, key);
        var direct = IvmsDirectImporter.Build(rows);
        Console.WriteLine(
            $"read {direct.Persons} person(s) / {direct.CardsSeen} card(s) from " +
            $"{install.Product} ({install.PersonDbPath}).");
        Console.WriteLine($"decoded {direct.Decoded} fob(s) directly from the card field.");

        var map = direct.Map;

        // The direct decode is complete for the ordinary ≤4-digit fobs and needs no panels.
        // Only the rare high/5-digit variant fails it. If panels are configured, expiry-
        // correlate just those stragglers; otherwise list them so they can be resolved by hand.
        bool panelsConfigured =
            (Value(opts, "panels") ?? Environment.GetEnvironmentVariable("OCB_PANELS")) is not null;

        if (direct.Undecodable.Count > 0 && panelsConfigured)
        {
            Console.WriteLine(
                $"{direct.Undecodable.Count} person(s) did not decode; correlating those by " +
                "unique expiry against the panels...");
            var settings = AccessSettings.From(opts);
            var roster = await BuildRosterAsync(settings, ct);
            ReportFailures(roster);
            var corr = IvmsExpiryCorrelator.Correlate(direct.Undecodable, roster);
            map = map.Merge(corr.Map);
            Console.WriteLine(
                $"  expiry correlation bound {corr.Matched} more, {corr.AmbiguousExpiries} " +
                $"ambiguous, {corr.UnmatchedPersons} still unmatched.");
        }
        else if (direct.Undecodable.Count > 0)
        {
            Console.WriteLine($"{direct.Undecodable.Count} person(s) could not be decoded " +
                "(e.g. a high/5-digit fob variant):");
            foreach (var person in direct.Undecodable)
                Console.WriteLine($"  - {person.Name}");
            Console.Error.WriteLine(
                "note: re-run with --panels to correlate these by expiry, or map them by hand.");
        }

        var merged = MergeIntoStore(map);
        Console.WriteLine($"map now holds {merged.Count} name(s), saved to {IdentityMapStore.DefaultPath}.");
        return 0;
    }

    private static int CaptureKey(Dictionary<string, string> opts, string base64)
    {
        var install = ResolveInstall(opts);
        IvmsKeyStore.Store(install.Id, base64);
        Console.WriteLine(
            $"cached the SQLCipher key for {install.Product} (install '{install.Id}'). " +
            "It will be used automatically by `--import-ivms`.");
        return 0;
    }

    private static int IdentityWhere()
    {
        string path = IdentityMapStore.DefaultPath;
        var map = IdentityMapStore.Load();
        if (map is null)
        {
            Console.WriteLine($"no name map yet. It will be created at {path}.");
            return 0;
        }
        Console.WriteLine($"name map: {path}");
        Console.WriteLine($"  {map.Count} name(s), source: {map.Source}" +
            (map.CapturedAtUtc is DateTime at ? $", captured {at:yyyy-MM-dd HH:mm:ss}Z" : ""));
        return 0;
    }

    private static int IdentityClear()
    {
        string path = IdentityMapStore.DefaultPath;
        if (File.Exists(path))
        {
            File.Delete(path);
            Console.WriteLine($"deleted the name map at {path}.");
        }
        else
        {
            Console.WriteLine($"no name map to delete (none at {path}).");
        }
        return 0;
    }

    /// <summary>Merges an imported map on top of the cached one (import wins) and saves it.</summary>
    private static IdentityMap MergeIntoStore(IdentityMap imported)
    {
        var existing = IdentityMapStore.Load();
        var merged = existing is null ? imported : existing.Merge(imported);
        IdentityMapStore.Save(merged);
        return merged;
    }

    /// <summary>
    /// Resolves which iVMS install to act on: an explicit <c>--ivms-db</c> (a person DB file
    /// or a UserData root), otherwise the single auto-discovered install.
    /// </summary>
    private static IvmsInstall ResolveInstall(Dictionary<string, string> opts)
    {
        if (Value(opts, "ivms-db") is string path)
        {
            if (Directory.Exists(path))
                return IvmsInstall.FromUserData(path);
            // A file path points at the DB itself; the UserData root is two levels up
            // (…\UserData\PersonalManagement.S\PersonalManagement).
            string? personalMgmtDir = Path.GetDirectoryName(path);
            string? userData = personalMgmtDir is null ? null : Path.GetDirectoryName(personalMgmtDir);
            if (userData is { Length: > 0 })
                return IvmsInstall.FromUserData(userData) with { PersonDbPath = path };
            throw new ArgumentException($"could not derive an iVMS install from --ivms-db '{path}'.");
        }

        var installs = IvmsInstall.Discover().ToList();
        if (installs.Count == 0)
            throw new NvrException(
                "no iVMS/NVMS install found on this machine — pass --ivms-db <path> to point " +
                "at the person database or its UserData root.");
        if (installs.Count > 1)
            throw new NvrException(
                $"multiple iVMS installs found ({string.Join(", ", installs.Select(i => i.Product))}) " +
                "— name one with --ivms-db <path>.");
        return installs[0];
    }

    // ---------- provisioning (policy-driven onboard / offboard / reconcile) ----------

    /// <summary>
    /// READ-ONLY drift report: the policy's expected per-panel card sets vs a live enumeration.
    /// Writes nothing — this is the safe way to prove the loader and resolver match production.
    /// </summary>
    private static async Task<int> ReconcileAsync(Dictionary<string, string> opts, CancellationToken ct)
    {
        var policy = AccessPolicy.Load(ResolvePolicyPath(opts));
        var settings = AccessSettings.From(opts);

        // The same read the roster verbs use — no side effects — then diff against the policy.
        var roster = await BuildRosterAsync(settings, ct);
        ReportFailures(roster);

        var map = IdentityMapStore.Load();
        var panelIps = settings.Panels.Select(p => p.Host).ToList();
        var report = AccessReconciler.Compare(policy, map, roster, panelIps);

        Console.WriteLine($"\nreconcile against {policy.Groups.Count} group(s)" +
            (map is null
                ? " — no identity map loaded, so every member is unmapped (import from iVMS first)."
                : $" ({map.Count} name(s) mapped)."));

        bool anyDrift = false;
        foreach (var panel in report.Panels)
        {
            if (!panel.Read)
            {
                Console.WriteLine($"\n  {panel.PanelIp}: NOT READ — expected {panel.ExpectedCount}; " +
                    "drift can't be judged (see the panel error above).");
                anyDrift = true;
                continue;
            }

            bool synced = panel.InSync;
            anyDrift |= !synced;
            Console.WriteLine($"\n  {panel.PanelIp}: expected {panel.ExpectedCount}, live {panel.LiveCount}" +
                (synced ? " — in sync" : " — DRIFT"));

            foreach (var m in panel.Missing)
                Console.WriteLine($"      missing : fob {m.Fob} doors={string.Join(",", m.Doors)}" +
                    (m.Name.Length > 0 ? $"  ({m.Name})" : ""));
            foreach (var d in panel.DoorMismatches)
                Console.WriteLine($"      doors   : fob {d.Fob} expected={string.Join(",", d.Expected)} " +
                    $"live={string.Join(",", d.Actual)}" + (d.Name.Length > 0 ? $"  ({d.Name})" : ""));
            if (panel.ExtraFobs.Count > 0)
                Console.WriteLine($"      extra   : {panel.ExtraFobs.Count} live fob(s) not in policy " +
                    $"(incl. any unmapped holder): {string.Join(", ", panel.ExtraFobs.Take(20))}" +
                    (panel.ExtraFobs.Count > 20 ? ", …" : ""));
        }

        if (report.UnmappedMembers.Count > 0)
            Console.Error.WriteLine(
                $"\nnote: {report.UnmappedMembers.Count} policy member(s) have no unique fob in the " +
                "identity map and were NOT checked (their live fobs show as \"extra\" above). " +
                "Import/repair the map with `access identity`.");
        foreach (var warning in report.ScheduleWarnings)
            Console.Error.WriteLine($"warning: {warning}");

        if (roster.IsPartial)
        {
            Console.Error.WriteLine("\nRESULT: PARTIAL — at least one panel could not be read.");
            return 1;
        }
        Console.WriteLine(anyDrift ? "\nRESULT: DRIFT — see above." : "\nRESULT: in sync.");
        return anyDrift ? 1 : 0;
    }

    /// <summary>
    /// Provision a new hire: resolve their group membership to a per-panel door union, then write
    /// one bounded, active fob per panel. <c>--dry-run</c> (the default) prints the exact writes;
    /// <c>--force</c> performs them and records name↔fob in the identity map.
    /// </summary>
    private static async Task<int> OnboardAsync(Dictionary<string, string> opts, CancellationToken ct)
    {
        string name = Value(opts, "name")
            ?? throw new ArgumentException("onboard needs --name First.Last");
        string card = Value(opts, "card")
            ?? throw new ArgumentException("onboard needs --card <fob> (the physical fob number)");
        var groups = ParseGroups(opts);
        var policy = AccessPolicy.Load(ResolvePolicyPath(opts));

        DateTime? until = Value(opts, "valid-until") is string u ? ParseValidUntil(u) : null;
        DateTime? from = Value(opts, "valid-from") is string f ? ParseTime(f) : null;
        var plan = AccessProvisioner.PlanOnboard(policy, name, card, groups, from, until);

        Console.WriteLine($"onboard {plan.Name} — fob {plan.Fob} — group(s): {string.Join(", ", plan.Groups)}");
        Console.WriteLine($"  valid:  {plan.ValidFrom:yyyy-MM-dd HH:mm:ss} → " +
            $"{plan.ValidUntil:yyyy-MM-dd HH:mm:ss} (panel-local)");
        foreach (var w in plan.Writes)
            Console.WriteLine($"  {w.PanelIp,-16} upsert fob {w.Card.CardNo} " +
                $"doors={w.Card.DoorSummary} valid=yes");
        foreach (var warning in plan.ScheduleWarnings)
            Console.Error.WriteLine($"warning: {warning}");

        if (!ShouldWrite(opts))
        {
            Console.WriteLine("\nDRY RUN — no panels were written. Re-run with --force to apply.");
            return 0;
        }

        // ---- write path (operator, --force) — verified per panel, identity map updated after ----
        var settings = AccessSettings.From(opts);
        int failed = 0;
        foreach (var w in plan.Writes)
        {
            var panel = PanelTarget.Parse(w.PanelIp, settings.SdkPort);
            var verified = await settings.ConnectVerifiedAsync(panel, ct);
            using var client = verified.Client;
            DeviceIdentityGuard.Ensure(verified.Identity);
            if (verified.Identity.Message.Length > 0)
                Console.Error.WriteLine($"note: {verified.Identity.Message}");

            Console.WriteLine($"\npanel {panel.Label}: writing fob {w.Card.CardNo} …");
            await client.UpsertCardAsync(w.Card, ct);
            if (await VerifyAsync(client, w.Card.CardNo, panel.Label, expectValid: true, w.Card.Doors, ct) != 0)
                failed++;
        }

        // Record the name↔fob binding only once the panels actually hold the card.
        var identity = new CardholderIdentity { Fob = plan.Fob, Name = plan.Name, Source = "onboard" };
        var updated = (IdentityMapStore.Load() ?? IdentityMap.Build([], "onboard")).With(identity);
        IdentityMapStore.Save(updated);
        Console.WriteLine($"\nrecorded {plan.Name} → fob {plan.Fob} in {IdentityMapStore.DefaultPath}.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Offboard a departed person: resolve their name to a fob via the identity map, then revoke
    /// it on EVERY panel (a stale fob can linger anywhere), and drop the map entry.
    /// </summary>
    private static async Task<int> OffboardAsync(Dictionary<string, string> opts, CancellationToken ct)
    {
        string name = Value(opts, "name")
            ?? throw new ArgumentException("offboard needs --name First.Last");
        var settings = AccessSettings.From(opts);
        var map = IdentityMapStore.Load();
        var panelIps = settings.Panels.Select(p => p.Host).ToList();
        var plan = AccessProvisioner.PlanOffboard(name, map, panelIps);

        Console.WriteLine($"offboard {plan.Name} — fob {plan.Fob}");
        Console.WriteLine($"  would revoke fob {plan.Fob} on all {plan.PanelRevokes.Count} panel(s): " +
            string.Join(", ", plan.PanelRevokes));

        if (!ShouldWrite(opts))
        {
            Console.WriteLine("\nDRY RUN — no panels were written. Re-run with --force to apply.");
            return 0;
        }

        // ---- write path (operator, --force): revoke everywhere it can be, then drop the map entry ----
        int failed = 0;
        foreach (var panel in settings.Panels)
        {
            var verified = await settings.ConnectVerifiedAsync(panel, ct);
            using var client = verified.Client;
            DeviceIdentityGuard.Ensure(verified.Identity);
            if (verified.Identity.Message.Length > 0)
                Console.Error.WriteLine($"note: {verified.Identity.Message}");

            Console.WriteLine($"\npanel {panel.Label}: revoking fob {plan.Fob} …");
            await client.RevokeCardAsync(plan.Fob, ct);
            if (await VerifyAsync(client, plan.Fob, panel.Label, expectValid: false, [], ct) != 0)
                failed++;
        }

        // map is non-null here: PlanOffboard would have thrown otherwise.
        IdentityMapStore.Save(map!.Without(plan.Fob));
        Console.WriteLine($"\ndropped fob {plan.Fob} ({plan.Name}) from {IdentityMapStore.DefaultPath}.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// A write happens only with an explicit <c>--force</c> and no <c>--dry-run</c>. <c>--dry-run</c>
    /// is the default and, if given alongside <c>--force</c>, wins — the safe reading of a
    /// contradictory command is to not touch a physical door.
    /// </summary>
    private static bool ShouldWrite(Dictionary<string, string> opts)
    {
        if (opts.ContainsKey("dry-run") && opts.ContainsKey("force"))
            Console.Error.WriteLine("note: both --dry-run and --force given; --dry-run wins (no writes).");
        return opts.ContainsKey("force") && !opts.ContainsKey("dry-run");
    }

    private static string ResolvePolicyPath(Dictionary<string, string> opts) =>
        Value(opts, "policy")
        ?? Environment.GetEnvironmentVariable("OCB_POLICY")
        ?? throw new ArgumentException(
            "missing --policy <path> (or OCB_POLICY) — point it at access-control-policy.json.");

    /// <summary>Reads one or more <c>--group</c> values (repeated flags and/or comma-separated).</summary>
    private static IReadOnlyList<string> ParseGroups(Dictionary<string, string> opts)
    {
        string? raw = Value(opts, "group");
        if (raw is null)
            throw new ArgumentException("onboard needs at least one --group <name>");
        var groups = raw
            .Split(['\u001f', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count == 0)
            throw new ArgumentException("onboard needs at least one --group <name>");
        return groups;
    }

    /// <summary>
    /// Parses <c>--valid-until</c>. A bare date means "through the end of that day", so a fob
    /// valid until 2026-08-30 still opens the door all of the 30th.
    /// </summary>
    private static DateTime ParseValidUntil(string value)
    {
        var t = ParseTime(value);
        return t.TimeOfDay == TimeSpan.Zero ? t.Date.AddDays(1).AddSeconds(-1) : t;
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

        var panel = settings.Panels[0];
        string host = panel.Label;
        bool force = opts.ContainsKey("force");

        // Identity first, and fatal: this is about to put a working credential on a physical
        // door, and a panel that is not the one named cannot be written to "carefully".
        var verified = await settings.ConnectVerifiedAsync(panel, ct);
        using var client = verified.Client;
        DeviceIdentityGuard.Ensure(verified.Identity);
        if (verified.Identity.Message.Length > 0)
            Console.Error.WriteLine($"note: {verified.Identity.Message}");

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
        var present = new List<PanelTarget>();
        int unreadable = 0;
        foreach (var panel in settings.Panels)
        {
            string host = panel.Label;
            try
            {
                // Verified before the fob is even looked up. An identity mismatch here aborts
                // the whole verb rather than counting as one unreadable panel: the operator
                // asked to remove someone's access, and doing that to the wrong building
                // while reporting success is worse than doing nothing.
                var verified = await settings.ConnectVerifiedAsync(panel, ct);
                using var probe = verified.Client;
                DeviceIdentityGuard.Ensure(verified.Identity);
                if (verified.Identity.Message.Length > 0)
                    Console.Error.WriteLine($"  note: {verified.Identity.Message}");

                var found = await probe.GetCardAsync(card, ct);
                Console.WriteLine(found is null
                    ? $"  {host}: fob {card} not present"
                    : $"  {host}: fob {card} doors={found.DoorSummary} valid={(found.Valid ? "yes" : "no")}");
                if (found is not null && found.Valid)
                    present.Add(panel);
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
        foreach (var panel in present)
        {
            // Re-verified on the write connection, not trusted from the read pass: this is a
            // second login, and between the two the address could be answering elsewhere.
            var verified = await settings.ConnectVerifiedAsync(panel, ct);
            using var client = verified.Client;
            DeviceIdentityGuard.Ensure(verified.Identity);
            await client.RevokeCardAsync(card, ct);
            if (await VerifyAsync(client, card, panel.Label, expectValid: false, [], ct) != 0)
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
        "only in whatever provisioned the fobs (iVMS-4200). Import them from iVMS with " +
        "`dvrtool access identity --import-ivms`.";

    /// <summary>Reads every panel, keeping per-panel failures instead of dropping them.</summary>
    private static async Task<AccessRoster> BuildRosterAsync(AccessSettings settings,
        CancellationToken ct)
    {
        var results = new List<AccessPanelResult>();
        var identified = new List<FleetRecord>();
        foreach (var panel in settings.Panels)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (client, info, identity) = await settings.ConnectVerifiedAsync(panel, ct);
                using (client)
                {
                    // A panel that turns out to be the wrong controller is recorded as a
                    // failed read, not skipped: its cards would otherwise be filed under
                    // this address and the roster would quietly describe another building.
                    // Failing it also makes the roster partial, which is what stops
                    // "no access found" from being read as a conclusion.
                    if (identity.Verdict == IdentityVerdict.Mismatch)
                    {
                        results.Add(AccessPanelResult.Failed(panel.Label, identity.Message));
                        continue;
                    }
                    if (identity.Message.Length > 0)
                        Console.Error.WriteLine($"note: {identity.Message}");

                    identified.Add(new FleetRecord(
                        panel.Label, panel.Host, panel.SdkPort, info.SerialNumber));
                    results.Add(new AccessPanelResult
                    {
                        PanelHost = panel.Label,
                        Serial = info.SerialNumber,
                        Cards = await client.GetCardsAsync(ct),
                    });
                }
            }
            catch (Exception ex) when (ex is NvrException or ArgumentException)
            {
                results.Add(AccessPanelResult.Failed(panel.Label, ex.Message));
            }
        }

        // Two addresses answering with one serial means the same panel was read twice: every
        // fob on it shows two presences, and a compare against it finds no drift because it
        // is being compared with itself.
        foreach (var issue in FleetAudit.Inspect(identified)
            .Where(i => i.Kind == FleetIssueKind.SameDevice))
            Console.Error.WriteLine($"warning: {issue.Message}");

        return AccessRoster.Build(results);
    }

    /// <summary>
    /// Reads every panel and then applies the cached iVMS name map, if one exists, so the
    /// read verbs show cardholder names automatically. Enrichment is pure and additive — a
    /// panel-supplied name still wins, and a missing map simply leaves the roster as-read.
    /// </summary>
    private static async Task<AccessRoster> BuildEnrichedRosterAsync(AccessSettings settings,
        CancellationToken ct)
    {
        var roster = await BuildRosterAsync(settings, ct);
        var map = IdentityMapStore.Load();
        return map is null ? roster : roster.EnrichWith(map);
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

/// <summary>
/// One panel to talk to: a host and the port it answers the SDK on.
/// </summary>
/// <remarks>
/// A panel list of bare hosts cannot express a site that puts two controllers behind one
/// address on different forwarded ports — which is common, and which one shared account
/// makes indistinguishable at the login. So each entry carries its own port, and
/// <see cref="Label"/> (identical to <see cref="AccessPanelConnection.Label"/>, deliberately)
/// is what every card read from it is stamped with.
/// </remarks>
internal sealed record PanelTarget(string Host, int SdkPort)
{
    internal string Label => SdkPort == VendorPorts.HikvisionSdk ? Host : $"{Host}:{SdkPort}";

    /// <summary>The identity-pin key: always port-qualified, never abbreviated.</summary>
    internal string Address => DeviceAddress.Format(Host, SdkPort);

    public override string ToString() => Label;

    /// <summary>Parses one operator-typed <c>ip[:port]</c> entry.</summary>
    internal static PanelTarget Parse(string text, int defaultPort)
    {
        if (!DeviceAddress.TryParse(text, defaultPort, out string? host, out int port, out string? error))
            throw new ArgumentException($"bad panel address: {error}");
        return new PanelTarget(host, port);
    }
}

/// <summary>Resolved connection settings shared by every <c>access</c> subcommand.</summary>
internal sealed record AccessSettings
{
    internal required IReadOnlyList<PanelTarget> Panels { get; init; }
    internal required string Username { get; init; }
    internal required string Password { get; init; }
    internal required int SdkPort { get; init; }
    internal string? SdkDirectory { get; init; }

    internal static AccessSettings From(Dictionary<string, string> opts)
    {
        string list = Opt(opts, "panels")
            ?? Environment.GetEnvironmentVariable("OCB_PANELS")
            ?? throw new ArgumentException("missing --panels (or OCB_PANELS in env/.env)");

        string user = Opt(opts, "user")
            ?? Environment.GetEnvironmentVariable("OCB_USER")
            ?? throw new ArgumentException("missing --user (or OCB_USER in env/.env)");

        // The fleet-wide port is only a default now: each entry may carry its own, because a
        // site can forward several panels through one address.
        int port = VendorPorts.HikvisionSdk;
        string? portText = Opt(opts, "port") ?? Environment.GetEnvironmentVariable("OCB_SDK_PORT");
        if (portText is not null &&
            (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
             port is < 1 or > 65535))
            throw new ArgumentException($"invalid SDK port '{portText}'");

        var panels = list
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => PanelTarget.Parse(entry, port))
            .ToList();

        // --panel narrows the set; "all" is the explicit spelling of the default.
        if (Opt(opts, "panel") is string only &&
            !only.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            panels = [PanelTarget.Parse(only, port)];
        }

        if (panels.Count == 0)
            throw new ArgumentException("no panels to talk to");

        // One address listed twice would be read twice and reported as two panels — a
        // roster that double-counts every fob on it, from what looks like a wider fleet.
        var duplicate = panels
            .GroupBy(p => p.Address, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException(
                $"panel {duplicate.Key} is listed more than once. If those were meant to be " +
                "different controllers behind one address, give each its own port " +
                "(ip:port).");

        return new AccessSettings
        {
            Panels = panels,
            Username = user,
            Password = ResolvePassword(opts, user, panels[0].Label),
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

    internal IAccessControlClient Connect(PanelTarget panel)
    {
        // The driver is a P/Invoke wrapper over Hikvision's Windows-only HCNetSDK; there is
        // no cross-platform path to these panels at all (they expose no HTTP interface).
        if (!OperatingSystem.IsWindows())
            throw new NvrException(
                "access-control panels need Hikvision's HCNetSDK, which is Windows-only.");

        return new HikvisionAccessClient(
            new AccessPanelConnection
            {
                Host = panel.Host,
                SdkPort = panel.SdkPort,
                Username = Username,
                Password = Password,
            },
            SdkDirectory);
    }

    /// <summary>
    /// Logs in, then confirms the controller is the one this address is pinned to — and
    /// hands back both the client and what it turned out to be.
    /// </summary>
    /// <remarks>
    /// Panels are the sharper end of this problem. A wrong port on a read produces a
    /// misleading roster; a wrong port on a <c>grant</c> puts a working fob on someone
    /// else's building, and the panel's own login cannot tell the difference because the
    /// whole fleet shares one account.
    /// </remarks>
    internal async Task<(IAccessControlClient Client, DeviceInfo Info, IdentityCheck Identity)>
        ConnectVerifiedAsync(PanelTarget panel, CancellationToken ct)
    {
        var client = Connect(panel);
        try
        {
            var info = await client.GetDeviceInfoAsync(ct);
            var identity = DeviceIdentityGuard.Check(
                panel.Address, info, expectedSerial: null, expectedBy: null);
            return (client, info, identity);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static string? Opt(Dictionary<string, string> opts, string key) =>
        opts.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
}
