using DVRTool.Core;

namespace DVRTool.Cli;

/// <summary>
/// <c>dvrtool exceptions …</c> — the recorder's own faults (its web UI's <b>Exception</b>
/// page) and whether any of them reach a human.
/// </summary>
/// <remarks>
/// <para>
/// <c>exceptions audit</c> is the reason this group exists, and the question it answers is
/// "if somebody starts guessing passwords on one of these recorders tonight, which ones would
/// tell me?". That needs two facts the recorder keeps on two different pages: whether the
/// e-mail box is ticked on the exception, and whether the mail settings would deliver
/// anything. A box ticked above blank SMTP settings looks configured on the device's own web
/// UI and sends nothing, forever, with no error anywhere — which is exactly the state an
/// audit from outside the box is for.
/// </para>
/// <para>
/// Read-only, all of it. Ticking a notification on a customer's recorder is a write with no
/// undo inside DVRTool, so this group reports and does not change; the fix is a click on the
/// recorder's own Exception page.
/// </para>
/// </remarks>
internal static class ExceptionCommands
{
    private const string Usage = """
        dvrtool exceptions — the recorder's own faults, and who hears about them

        Usage:
          dvrtool exceptions show  [connection options]
          dvrtool exceptions audit [--type <eventType>] [--all-saved | --device <name> ...]
                                   [connection options]

        Subcommands:
          show    Every device-level exception this recorder has — illegal login, HDD full,
                  HDD error, network disconnected, IP conflict, record failure — with what it
                  does when each one fires, followed by the recorder's mail settings.
          audit   THE FLEET EXCEPTION AUDIT. One row per saved recorder for ONE exception
                  (illegal login by default): does the firmware have it, is e-mail ticked,
                  and would that mail actually go anywhere.

        Options:
          --type <eventType>   The exception to audit, in the vendor's own spelling. Default
                               illaccess (illegal login). Others seen in the field: diskfull,
                               diskerror, nicbroken, ipconflict, recordingfailure,
                               videomismatch, badvideo, spareException. `exceptions show`
                               lists what a given recorder actually has.
          --all-saved          Audit every recorder saved in the GUI (%APPDATA%\DVRTool\
                               devices.json). Door panels are skipped — they are not recorders.
          --device <name>      Work from a saved GUI record instead of --host/--user/--pass:
                               its stored (DPAPI-protected) credentials and its expected
                               serial. Repeatable for `audit`; exactly one for `show`.

        Hikvision only. Dahua and Nx keep their event linkage somewhere else entirely and
        neither has been read for it — those rows say so rather than reading as "no alerts",
        because "not implemented" and "nothing configured" must never print the same.

        Exit code 1 means at least one recorder is a gap: it can raise the exception and
        nobody would hear, or its mail would go nowhere, or it could not be read at all.
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

    /// <summary>Whether this invocation works from the GUI's saved records.</summary>
    internal static bool UsesSavedDevices(string subcommand, Dictionary<string, string> opts) =>
        opts.ContainsKey("device") || (subcommand == "audit" && opts.ContainsKey("all-saved"));

    /// <summary>The exception the audit is about — <c>--type</c>, or illegal login.</summary>
    private static string TypeOf(Dictionary<string, string> opts)
    {
        string asked = opts.GetValueOrDefault("type", "").Trim();
        return asked.Length > 0 ? asked : ExceptionTypes.IllegalLogin;
    }

    internal static async Task<int> RunSavedAsync(string subcommand,
        Dictionary<string, string> opts, CancellationToken ct)
    {
        var picked = ConfigCommands.ResolveSaved(opts, out int error);
        if (picked is null)
            return error;

        if (subcommand is "audit" or "")
        {
            // All at once: one slow site must not serialize the rest, and a failure is
            // carried per device rather than faulting the sweep.
            string type = TypeOf(opts);
            var rows = await Task.WhenAll(picked.Select(d => ReadOneAsync(d, type, ct)));
            return PrintAudit(ExceptionAudit.Build(type, rows));
        }

        if (picked.Count != 1)
        {
            Console.Error.WriteLine(
                $"error: `exceptions {subcommand}` reads one recorder — name exactly one " +
                "--device. Only `exceptions audit` reads several.");
            return 2;
        }

        var device = picked[0];
        using var client = VendorClients.For(device);
        var check = await DeviceIdentityGuard.CheckAsync(client,
            device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name, ct: ct);
        DeviceIdentityGuard.Ensure(check);
        if (check.Message.Length > 0)
            Console.Error.WriteLine("note: " + check.Message);

        return await RunAsync(client, subcommand, opts, ct);
    }

    /// <summary>
    /// One saved recorder, read for the sweep. Identity before content, like every other
    /// fleet read: <c>host:port</c> names a socket, not a recorder, and an alerting audit
    /// that credits the wrong box is worse than none.
    /// </summary>
    private static async Task<ExceptionAuditRow> ReadOneAsync(SavedDevice device, string type,
        CancellationToken ct)
    {
        INvrClient? client = null;
        try
        {
            client = VendorClients.For(device);
            if (client is not IExceptionNotificationClient exceptions)
                return ExceptionAuditRow.NotImplemented(device.Name, type,
                    device.VendorKind.ToString());

            var check = await DeviceIdentityGuard.CheckAsync(client,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name,
                ct: ct);
            DeviceIdentityGuard.Ensure(check);

            return await ExceptionSweep.ReadAsync(device.Name, exceptions, type, ct: ct);
        }
        catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
        {
            return ExceptionAuditRow.Failed(device.Name, type, ex.Message);
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
        if (client is not IExceptionNotificationClient exceptions)
        {
            Console.Error.WriteLine(
                $"error: reading exceptions isn't implemented for {client.Vendor} devices.");
            return 2;
        }

        switch (subcommand)
        {
            case "show":
                return await ShowAsync(exceptions, ct);
            case "audit":
            {
                string type = TypeOf(opts);
                var row = await ExceptionSweep.ReadAsync("this device", exceptions, type, ct: ct);
                return PrintAudit(ExceptionAudit.Build(type, [row]));
            }
            default:
                Console.Error.WriteLine($"error: unknown exceptions subcommand '{subcommand}'\n");
                Console.WriteLine(Usage);
                return 2;
        }
    }

    private static async Task<int> ShowAsync(IExceptionNotificationClient client,
        CancellationToken ct)
    {
        var read = await client.GetExceptionsAsync(ct);

        Console.WriteLine($"{"EXCEPTION",-36}  {"HAS IT",-7}  WHAT HAPPENS WHEN IT FIRES");
        foreach (var t in read.Triggers)
        {
            string has = t.Exists ? "yes" : "no";
            Console.WriteLine(
                $"{Truncate($"{t.Label} ({t.EventType})", 36),-36}  {has,-7}  " +
                $"{(t.Exists ? t.NotificationsText : "—")}");
        }

        Console.WriteLine();
        Console.WriteLine($"Mail:  {read.Email.Summary}");
        if (read.Email.Readable)
        {
            if (read.Email.SenderAddress.Length > 0 || read.Email.SenderName.Length > 0)
                Console.WriteLine($"  from {read.Email.SenderName} <{read.Email.SenderAddress}>");
            if (read.Email.Ssl is true || read.Email.Authenticated is true)
                Console.WriteLine(
                    $"  {(read.Email.Authenticated is true ? "authenticated" : "anonymous")}" +
                    $"{(read.Email.Ssl is true ? ", SSL" : "")}");
        }

        var emailing = read.Emailing.ToList();
        Console.WriteLine(emailing.Count == 0
            ? "  no exception on this recorder is set to send mail."
            : $"  sends mail on: {string.Join(", ", emailing.Select(t => t.Label))}");

        foreach (var note in read.Failures)
            Console.Error.WriteLine($"note: {note.Label} could not be read — {note.Value}");

        // A recorder whose exceptions read fine is a 0 even when nothing is configured:
        // `show` reports, `audit` judges.
        return read.Failures.Count > 0 ? 1 : 0;
    }

    private static int PrintAudit(ExceptionAudit audit)
    {
        Console.WriteLine($"Exception: {audit.Label} ({audit.EventType})");
        Console.WriteLine();
        Console.WriteLine(
    $"{"DEVICE",-22}  {"HAS IT",-7}  {"E-MAIL",-7}  {"MAIL SERVER",-38}  VERDICT");
        foreach (var row in audit.Rows)
        {
            if (!row.Ok)
            {
                Console.WriteLine($"{Truncate(row.DeviceName, 22),-22}  {"?",-7}  {"?",-7}  " +
                    $"{"?",-38}  {row.Verdict}");
                continue;
            }

            string has = row.State is ExceptionEmailState.Unsupported ? "no" : "yes";
            string email = row.State switch
            {
                ExceptionEmailState.Emails => "on",
                ExceptionEmailState.GoesNowhere => "on*",
                ExceptionEmailState.NotEnabled => "off",
                _ => "—",
            };
            Console.WriteLine(
                $"{Truncate(row.DeviceName, 22),-22}  {has,-7}  {email,-7}  " +
                $"{Truncate(row.Email.ShortSummary, 38),-38}  {row.Verdict}");
        }

        Console.WriteLine();
        Console.WriteLine(audit.Summary);
        if (audit.Rows.Any(r => r.State is ExceptionEmailState.GoesNowhere))
            Console.WriteLine(
                "  * ticked, but the recorder has no server or nobody to send to — it sends " +
                "nothing and says nothing.");

        return audit.Gaps.Any() || audit.IsPartial ? 1 : 0;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
