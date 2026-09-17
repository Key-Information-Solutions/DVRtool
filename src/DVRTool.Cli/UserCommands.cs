using DVRTool.Core;

namespace DVRTool.Cli;

/// <summary>
/// The `users` command group: the recorder's own login accounts, and adding one across a
/// fleet.
/// </summary>
/// <remarks>
/// Same contract as the other write-capable groups: dry-run is the default and wins over
/// <c>--force</c> when both are given, every applied write is read back, and what is reported
/// is what the recorder now says rather than what was asked for.
/// <para>
/// There is no <c>--password</c> flag, by design. A password on the command line persists in
/// shell history, in process listings and in audit logs; it is prompted instead, once per run,
/// and applied to every recorder named — which is also what makes the accounts actually match.
/// </para>
/// </remarks>
internal static class UserCommands
{
    private const string Usage = """
        dvrtool users — the login accounts configured on a recorder

        Usage:
          dvrtool users list [--device <name> ...] [--all-saved]
          dvrtool users add --name <account> --role <admin|operator|viewer>
                            [--device <name> ...] [--all-saved] [--force]

        Subcommands:
          list          List the accounts (the default when no subcommand is given)
          add           Create one account on every named recorder that does not have it.
                        The password is prompted — never passed as a flag. Add-only: a
                        recorder that already has the name is reported and left alone,
                        even if its level differs.

        Options:
          --name <s>            the account to create
          --role <r>            admin | operator | viewer (default: operator)
          --device <name>       a saved recorder, repeatable
          --all-saved           every saved recorder
          --dry-run             print the plan and write nothing (the default)
          --force               actually write

        Without --device/--all-saved, the usual connection options (--host/--user/--pass)
        address a single recorder.

        Only Hikvision recorders can be written today. `list` works on Hikvision and Dahua.
        """;

    internal static bool TryRunHelp(string subcommand, Dictionary<string, string> opts,
        out int exitCode)
    {
        if (opts.ContainsKey("help") || subcommand == "help")
        {
            Console.WriteLine(Usage);
            exitCode = 0;
            return true;
        }
        if (subcommand is not ("" or "list" or "add"))
        {
            Console.Error.WriteLine($"error: unknown subcommand `users {subcommand}`.");
            Console.Error.WriteLine(Usage);
            exitCode = 2;
            return true;
        }
        exitCode = 0;
        return false;
    }

    /// <summary>True when this invocation works from the GUI's saved records, not --host.</summary>
    internal static bool UsesSavedDevices(Dictionary<string, string> opts) =>
        opts.ContainsKey("device") || opts.ContainsKey("all-saved");

    /// <summary>
    /// The same gate every other write in this tool uses: dry-run is the default, and it wins
    /// when both flags are given, because the operator who typed both meant the cautious one.
    /// </summary>
    private static bool ShouldWrite(Dictionary<string, string> opts)
    {
        if (opts.ContainsKey("dry-run") && opts.ContainsKey("force"))
            Console.Error.WriteLine("note: both --dry-run and --force given; --dry-run wins (no writes).");
        return opts.ContainsKey("force") && !opts.ContainsKey("dry-run");
    }

    // ----- single recorder (--host) -----

    internal static async Task<int> RunAsync(INvrClient client, string subcommand,
        Dictionary<string, string> opts, CancellationToken ct) => subcommand switch
    {
        "" or "list" => await ListAsync(client, ct),
        "add" => await AddAsync([(client.Connection.Host, client)], opts, ct),
        _ => 2,
    };

    // ----- saved recorders (--device/--all-saved) -----

    internal static async Task<int> RunSavedAsync(string subcommand,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        var picked = ResolveSaved(opts, out int error);
        if (picked is null)
            return error;

        var clients = new List<(string Name, INvrClient Client)>();
        try
        {
            foreach (var device in picked)
            {
                INvrClient client = VendorClients.For(device);
                clients.Add((device.Name, client));

                // Identity before content, and before any write leg: host:port names a socket,
                // not a recorder, and one shared account logs into whatever is behind the port.
                var check = await DeviceIdentityGuard.CheckAsync(client,
                    device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null,
                    device.Name, ct: ct);
                DeviceIdentityGuard.Ensure(check);
                if (check.Message.Length > 0)
                    Console.Error.WriteLine($"note: {device.Name}: {check.Message}");
            }

            return subcommand switch
            {
                "" or "list" => await ListManyAsync(clients, ct),
                "add" => await AddAsync(clients, opts, ct),
                _ => 2,
            };
        }
        finally
        {
            foreach (var (_, client) in clients)
                client.Dispose();
        }
    }

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

    // ----- list -----

    private static async Task<int> ListAsync(INvrClient client, CancellationToken ct)
    {
        if (client is not IUserManagementClient users)
        {
            Console.Error.WriteLine(
                $"error: user management isn't implemented for {client.Vendor} devices.");
            return 2;
        }

        var accounts = await users.GetUsersAsync(ct);
        if (accounts.Count == 0)
        {
            Console.WriteLine("No users reported.");
            return 0;
        }

        Console.WriteLine(
            $"{"ID",-6}  {"NAME",-20}  {"LEVEL",-14}  {"ROLE",-9}  {"RESERVED",-8}  MEMO");
        foreach (var u in accounts)
            Console.WriteLine(
                $"{u.Id,-6}  {u.Name,-20}  {u.NativeLevel,-14}  {u.Role,-9}  " +
                $"{(u.Reserved ? "yes" : ""),-8}  {u.Memo}");
        return 0;
    }

    private static async Task<int> ListManyAsync(
        IReadOnlyList<(string Name, INvrClient Client)> devices, CancellationToken ct)
    {
        var read = await ReadAllAsync(devices, ct);
        var matrix = UserMatrix.Build(read);

        // Each column is as wide as the widest thing in it, header included, so the levels
        // line up under the recorder they belong to — a matrix whose columns drift is a
        // matrix that gets misread.
        int userWidth = Math.Max(4, matrix.Rows.Count == 0 ? 4 : matrix.Rows.Max(r => r.User.Length));
        var widths = matrix.Devices
            .Select((d, i) => Math.Max(d.DeviceName.Length,
                matrix.Rows.Count == 0 ? 0 : matrix.Rows.Max(r => Cell(r.Cells[i], d.Ok).Length)))
            .ToList();

        Console.WriteLine($"{"USER".PadRight(userWidth)}  " +
            string.Join("  ", matrix.Devices.Select((d, i) => d.DeviceName.PadRight(widths[i]))));
        foreach (var row in matrix.Rows)
        {
            var cells = row.Cells.Select((c, i) => Cell(c, matrix.Devices[i].Ok).PadRight(widths[i]));
            Console.WriteLine($"{row.User.PadRight(userWidth)}  {string.Join("  ", cells)}  {row.Status}");
        }

        foreach (var failed in matrix.FailedDevices)
            Console.Error.WriteLine($"{failed.DeviceName}: {failed.Error}");

        // "?" is not "missing": a recorder that did not answer is a recorder whose accounts
        // are unknown, and the exit code says the view is incomplete.
        return matrix.IsPartial ? 1 : 0;
    }

    private static string Cell(string? level, bool deviceOk) =>
        level ?? (deviceOk ? "-" : "?");

    private static async Task<List<DeviceUsersResult>> ReadAllAsync(
        IReadOnlyList<(string Name, INvrClient Client)> devices, CancellationToken ct)
    {
        var results = new List<DeviceUsersResult>();
        foreach (var (name, client) in devices)
        {
            try
            {
                if (client is not IUserManagementClient users)
                {
                    results.Add(DeviceUsersResult.Failed(name,
                        $"user management isn't implemented for {client.Vendor} devices"));
                    continue;
                }
                results.Add(new DeviceUsersResult
                {
                    DeviceName = name,
                    Users = await users.GetUsersAsync(ct),
                });
            }
            catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
            {
                results.Add(DeviceUsersResult.Failed(name, ex.Message));
            }
        }
        return results;
    }

    // ----- add -----

    private static async Task<int> AddAsync(IReadOnlyList<(string Name, INvrClient Client)> devices,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        if (!opts.TryGetValue("name", out string? name) || name.Trim().Length == 0)
        {
            Console.Error.WriteLine("error: `users add` needs --name <account>.");
            return 2;
        }
        if (!TryParseRole(opts.GetValueOrDefault("role", "operator"), out UserRole role))
        {
            Console.Error.WriteLine(
                $"error: unknown --role '{opts.GetValueOrDefault("role")}'. " +
                "Use admin, operator or viewer.");
            return 2;
        }

        // Refuse a vendor that cannot be written before anything is read or typed.
        var unwritable = devices.Where(d => d.Client is not IUserAdminClient).ToList();
        if (unwritable.Count > 0)
        {
            Console.Error.WriteLine(
                "error: creating accounts isn't implemented for " +
                string.Join(", ", unwritable.Select(d => $"{d.Name} ({d.Client.Vendor})")) +
                ". Only Hikvision recorders can be written today.");
            return 2;
        }

        var read = await ReadAllAsync(devices, ct);
        var plan = UserAddPlan.For(read, name, role);
        PrintPlan(plan);

        if (!plan.HasWork)
            return plan.IsPartial ? 1 : 0;

        if (!ShouldWrite(opts))
        {
            Console.WriteLine("\nDRY RUN — nothing was written. Re-run with --force to apply.");
            return 0;
        }

        string password;
        string confirm;
        try
        {
            password = ConsolePrompt.ReadSecret(
                $"Password for '{plan.Name}': ",
                "a password is needed and input is redirected: run this from a terminal " +
                "(there is deliberately no --password flag).");
            confirm = ConsolePrompt.ReadSecret("Confirm: ", "input is redirected.");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        if (password != confirm)
        {
            Console.Error.WriteLine("error: the two passwords differ. Nothing was written.");
            return 2;
        }

        // A password the recorder is certain to refuse is refused here, so the operator is
        // told by the tool rather than by a statusCode. The device stays the authority: it can
        // still reject one this accepts, and then its own words are what gets printed.
        var complaints = UserPasswordRules.Check(password, plan.Name);
        if (complaints.Count > 0)
        {
            Console.Error.WriteLine("error: the password " + complaints[0]);
            foreach (string extra in complaints.Skip(1))
                Console.Error.WriteLine("       it also " + extra);
            Console.Error.WriteLine("Nothing was written.");
            return 2;
        }

        Console.WriteLine();
        var byName = devices.ToDictionary(d => d.Name, d => d.Client, StringComparer.Ordinal);
        int failures = 0;
        foreach (string deviceName in plan.Creates)
        {
            try
            {
                var admin = (IUserAdminClient)byName[deviceName];
                var created = await admin.CreateUserAsync(new NewUser(plan.Name, password, role), ct);

                // The read-back is the report: the id is the device's, and so is the level.
                Console.WriteLine(created.Role == role
                    ? $"{deviceName}: created and verified by read-back — id {created.Id}, " +
                      $"{created.NativeLevel}."
                    : $"{deviceName}: created; the recorder kept {created.NativeLevel} " +
                      $"(asked {role}) — id {created.Id}.");
            }
            catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
            {
                failures++;
                Console.Error.WriteLine($"{deviceName}: NOT CREATED — {ex.Message}");
            }
        }

        if (failures > 0)
            Console.Error.WriteLine(
                $"\n{failures} of {plan.Creates.Count} recorders were not written.");
        return failures > 0 || plan.IsPartial ? 1 : 0;
    }

    private static void PrintPlan(UserAddPlan plan)
    {
        Console.WriteLine($"Account:  {plan.Name}");
        Console.WriteLine($"Level:    {plan.Role}");

        if (plan.Creates.Count > 0)
        {
            Console.WriteLine($"\nWould create on {plan.Creates.Count} recorder(s):");
            foreach (string device in plan.Creates)
                Console.WriteLine($"  {device}");
        }
        else
        {
            Console.WriteLine("\nNothing to create.");
        }

        if (plan.AlreadyPresent.Count > 0)
        {
            // Named with the level they hold, because "already there" at the wrong level is a
            // thing the operator needs to see and this command will not change.
            Console.WriteLine("\nAlready has the account (left alone):");
            foreach (string device in plan.AlreadyPresent)
                Console.WriteLine($"  {device}");
        }

        if (plan.IsPartial)
        {
            Console.WriteLine(
                $"\nPARTIAL — {plan.Unreadable.Count} recorder(s) could not be read, so this " +
                "is not the whole fleet:");
            foreach (var device in plan.Unreadable)
                Console.WriteLine($"  {device.DeviceName} — {device.Error}");
        }
    }

    private static bool TryParseRole(string value, out UserRole role)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "admin" or "administrator":
                role = UserRole.Admin;
                return true;
            case "operator":
                role = UserRole.Operator;
                return true;
            case "viewer" or "user" or "guest":
                role = UserRole.Viewer;
                return true;
            default:
                role = UserRole.Custom;
                return false;
        }
    }
}
