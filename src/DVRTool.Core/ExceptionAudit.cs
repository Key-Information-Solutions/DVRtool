namespace DVRTool.Core;

/// <summary>
/// What one recorder does about one exception, as a single word for a grid cell and a colour.
/// </summary>
/// <remarks>
/// The five states are deliberately not a bool. "It will mail somebody" and "the box is
/// ticked but the mail settings are blank" look identical on the recorder's own web page —
/// the ticked box is on the Exception page and the empty SMTP server is two menus away — and
/// telling them apart is the whole point of auditing this from outside the box.
/// </remarks>
public enum ExceptionEmailState
{
    /// <summary>The recorder could not be read. Unknown, never "fine".</summary>
    Unreadable,

    /// <summary>This firmware has no such exception, so nothing can be ticked on it.</summary>
    Unsupported,

    /// <summary>The exception exists and mail is not among its notifications.</summary>
    NotEnabled,

    /// <summary>Mail is ticked, and the mail settings would not deliver it.</summary>
    GoesNowhere,

    /// <summary>Mail is ticked and the recorder has a server and a recipient to send to.</summary>
    Emails,
}

/// <summary>
/// One recorder's answer for one exception type: does it have it, is mail ticked, and would
/// the mail arrive.
/// </summary>
/// <remarks>
/// The carried-failure rule from <see cref="ClockAuditRow"/> applies verbatim: a recorder that
/// could not be read is a row with an <see cref="Error"/>, never a row omitted and never a row
/// reading "fine".
/// </remarks>
public sealed record ExceptionAuditRow
{
    public required string DeviceName { get; init; }

    /// <summary>The exception this row is about, as the vendor spells it.</summary>
    public required string EventType { get; init; }

    /// <summary>What the recorder said about that exception, or null when it has none.</summary>
    public ExceptionTrigger? Trigger { get; init; }

    public EmailDelivery Email { get; init; } = EmailDelivery.Unknown;

    /// <summary>The model and firmware, so a "not supported" row names what it is not supported on.</summary>
    public string Model { get; init; } = "";

    /// <summary>Null on success; why the recorder could not be read otherwise.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// This row is blank because DVRTool cannot read this vendor's alerting at all, not
    /// because the recorder was unreachable. Kept apart from a failed read because the two
    /// need opposite things done about them — one is a site visit, the other is a feature —
    /// and both are equally not "no alerts configured".
    /// </summary>
    public bool VendorUnsupported { get; init; }

    public required ExceptionEmailState State { get; init; }

    /// <summary>What the operator should do about this row, or what it already does.</summary>
    public required string Verdict { get; init; }

    public bool Ok => Error is null;

    /// <summary>This recorder will actually put an e-mail in somebody's inbox.</summary>
    public bool Emails => State is ExceptionEmailState.Emails;

    /// <summary>
    /// A row an operator should act on: it could fire this exception and nobody would hear —
    /// or worse, somebody believes they would. A recorder without the exception at all is
    /// not a gap, because there is nothing to turn on.
    /// </summary>
    public bool IsGap => State is ExceptionEmailState.NotEnabled or ExceptionEmailState.GoesNowhere;

    /// <summary>The other things that do happen when it fires ("audible warning"), for the tooltip.</summary>
    public string OtherNotifications => Trigger?.NotificationsText ?? "?";

    /// <summary>A recorder that answered.</summary>
    public static ExceptionAuditRow For(string deviceName, string eventType,
        DeviceExceptions read, string model = "")
    {
        ArgumentNullException.ThrowIfNull(read);
        var trigger = read.Find(eventType);
        var state = StateOf(trigger, read.Email);
        return new ExceptionAuditRow
        {
            DeviceName = deviceName,
            EventType = eventType,
            Trigger = trigger,
            Email = read.Email,
            Model = model,
            State = state,
            Verdict = VerdictFor(state, trigger, read.Email),
        };
    }

    /// <summary>A recorder that did not.</summary>
    public static ExceptionAuditRow Failed(string deviceName, string eventType, string error) =>
        new()
        {
            DeviceName = deviceName,
            EventType = eventType,
            Error = error,
            State = ExceptionEmailState.Unreadable,
            Verdict = "could not be read — unknown, not fine",
        };

    /// <summary>A recorder of a vendor whose alerting DVRTool does not read.</summary>
    public static ExceptionAuditRow NotImplemented(string deviceName, string eventType,
        string vendor) =>
        new()
        {
            DeviceName = deviceName,
            EventType = eventType,
            Error = $"reading exceptions isn't implemented for {vendor} devices",
            VendorUnsupported = true,
            State = ExceptionEmailState.Unreadable,
            Verdict = $"not read: DVRTool reads exceptions on Hikvision only, not {vendor}",
        };

    internal static ExceptionEmailState StateOf(ExceptionTrigger? trigger, EmailDelivery email)
    {
        if (trigger is null || !trigger.Exists)
            return ExceptionEmailState.Unsupported;
        if (!trigger.SendsEmail)
            return ExceptionEmailState.NotEnabled;
        return email.Configured ? ExceptionEmailState.Emails : ExceptionEmailState.GoesNowhere;
    }

    /// <summary>
    /// What to say about one recorder. Each case names the next thing an operator would do,
    /// and the two states that look alike on the device's own web UI are worded so they
    /// cannot be confused in a report.
    /// </summary>
    internal static string VerdictFor(ExceptionEmailState state, ExceptionTrigger? trigger,
        EmailDelivery email) => state switch
    {
        ExceptionEmailState.Unsupported =>
            "this firmware has no such exception",
        ExceptionEmailState.NotEnabled when email.Configured =>
            trigger is { Notifications.Count: > 0 }
                ? $"supported, e-mail not ticked (it only does: {trigger.NotificationsText})"
                : "supported, e-mail not ticked — it fires and nothing happens",
        ExceptionEmailState.NotEnabled =>
            $"supported, e-mail not ticked — and {email.Gap}",
        ExceptionEmailState.GoesNowhere =>
            $"e-mail is ticked but {email.Gap} — it sends nothing",
        ExceptionEmailState.Emails =>
            $"e-mails {string.Join(", ", email.Recipients)}",
        _ => "could not be read — unknown, not fine",
    };
}

/// <summary>
/// The fleet exception audit: one row per recorder for one exception type. Pure aggregation,
/// no I/O, like <see cref="ConfigAudit"/> and <see cref="FleetMatrix"/>, and for the same
/// reason — identical from the GUI, the CLI, or a fixture.
/// </summary>
/// <remarks>
/// The question this answers is "if somebody starts guessing passwords on one of these
/// recorders tonight, which ones would tell me?" — and the answer worth having is not one
/// number but four: the ones that mail, the ones that could and do not, the ones whose mail
/// would go nowhere, and the ones nobody could read. A summary that collapses those is how a
/// fleet ends up believed to be monitored.
/// </remarks>
public sealed record ExceptionAudit
{
    public required IReadOnlyList<ExceptionAuditRow> Rows { get; init; }

    /// <summary>The exception every row is about.</summary>
    public required string EventType { get; init; }

    public string Label => ExceptionTypes.Label(EventType);

    public IEnumerable<ExceptionAuditRow> FailedDevices => Rows.Where(r => !r.Ok);

    /// <summary>Recorders that will actually send mail.</summary>
    public IEnumerable<ExceptionAuditRow> Emailing => Rows.Where(r => r.Emails);

    /// <summary>Recorders that could send mail and will not.</summary>
    public IEnumerable<ExceptionAuditRow> Gaps => Rows.Where(r => r.IsGap);

    /// <summary>Recorders whose firmware has no such exception — nothing to turn on.</summary>
    public IEnumerable<ExceptionAuditRow> Unsupported =>
        Rows.Where(r => r.State is ExceptionEmailState.Unsupported);

    /// <summary>Recorders of a vendor this audit cannot read — a gap in DVRTool, not at the site.</summary>
    public IEnumerable<ExceptionAuditRow> NotImplemented => Rows.Where(r => r.VendorUnsupported);

    /// <summary>Recorders that were asked and did not answer — unknown, not fine.</summary>
    public IEnumerable<ExceptionAuditRow> Unreachable =>
        Rows.Where(r => !r.Ok && !r.VendorUnsupported);

    /// <summary>
    /// Recorders that could not send mail on anything, whatever their exception settings say:
    /// their mail page is blank. Worth its own line because one click on the exception would
    /// not fix them — they need mail set up first.
    /// </summary>
    public IEnumerable<ExceptionAuditRow> WithoutMail =>
        Rows.Where(r => r.Ok && r.Email.Readable && !r.Email.Configured);

    /// <summary>True when at least one recorder could not be read, so the audit is partial.</summary>
    public bool IsPartial => Rows.Any(r => !r.Ok);

    public static ExceptionAudit Build(string eventType, IEnumerable<ExceptionAuditRow> rows) =>
        new() { EventType = eventType, Rows = rows.ToList() };

    /// <summary>One line an operator can act on, partial audits included.</summary>
    public string Summary
    {
        get
        {
            int read = Rows.Count(r => r.Ok);
            int mails = Emailing.Count();
            int gaps = Gaps.Count();
            int none = Unsupported.Count();
            int noMail = WithoutMail.Count();
            int unreachable = Unreachable.Count();
            int otherVendor = NotImplemented.Count();

            if (read == 0)
                return $"no recorder answered — nothing is known about {Label} e-mail.";

            var parts = new List<string>(4)
            {
                $"{mails} of {read} e-mail on {Label.ToLowerInvariant()}",
            };
            if (gaps > 0)
                parts.Add($"{gaps} could and do not");
            if (noMail > 0)
                parts.Add($"{noMail} of those have no mail settings at all");
            if (none > 0)
                parts.Add($"{none} have no such exception");
            string head = string.Join(", ", parts) + ".";

            // The two kinds of blank row are named apart on purpose: one is a site to go
            // look at, the other is a vendor DVRTool has not built this for. Neither is
            // "nothing configured".
            if (unreachable > 0)
                head += $" PARTIAL: {unreachable} could not be read — those are unknown, not fine.";
            if (otherVendor > 0)
                head += $" {otherVendor} are not Hikvision and were not read.";
            return head;
        }
    }
}

/// <summary>
/// Reads one recorder for the fleet exception audit. Lives here, next to the aggregation it
/// feeds, so the GUI panel and <c>dvrtool exceptions audit</c> sweep identically — the same
/// reason <see cref="ClockSweep"/> does its I/O in Core.
/// </summary>
public static class ExceptionSweep
{
    /// <summary>
    /// One device, one row. The read is a whole <see cref="DeviceExceptions"/> because the
    /// two documents it needs — the trigger list and the mail settings — are the two halves
    /// of the only question worth asking, and a sweep that skipped the mail settings would
    /// report a ticked box as a working alert.
    /// </summary>
    public static async Task<ExceptionAuditRow> ReadAsync(string deviceName,
        IExceptionNotificationClient client, string eventType = ExceptionTypes.IllegalLogin,
        string model = "", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var read = await client.GetExceptionsAsync(ct);
        return ExceptionAuditRow.For(deviceName, eventType, read, model);
    }
}
