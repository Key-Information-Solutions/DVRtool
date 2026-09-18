namespace DVRTool.Core;

/// <summary>
/// The recorder's own faults — the "Exception" page of a Hikvision NVR: illegal login, HDD
/// full, HDD error, network unplugged, IP conflict, recording failure. Device-level, as
/// opposed to the per-camera events (motion, video loss, tampering) that share the same
/// trigger list.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a device-level exception is told apart from a camera event</b>: by the absence of a
/// channel. Every camera trigger in a Hikvision trigger list carries a channel element —
/// <c>videoInputChannelID</c> on a DVR, <c>dynVideoInputChannelID</c> on an NVR — and every
/// recorder exception carries none. That rule was read off three live firmwares on
/// 2026-09-18 and is what <see cref="IsDeviceLevel"/> implements, because a hardcoded roster
/// would silently drop the exception a firmware adds next.
/// </para>
/// <para>
/// The labels below are the words the recorder's own web UI uses, so an audit row and the
/// page an operator would go fix it on say the same thing. An event type with no entry here
/// is shown verbatim rather than hidden — a fleet that grows a new exception type should
/// report it, not swallow it.
/// </para>
/// </remarks>
public static class ExceptionTypes
{
    /// <summary>
    /// An attempt to log into the recorder with the wrong password — the event an integrator
    /// actually wants mailed, and the reason this file exists.
    /// </summary>
    public const string IllegalLogin = "illaccess";

    /// <summary>The notification method that sends mail, as the firmware spells it.</summary>
    public const string EmailMethod = "email";

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        [IllegalLogin] = "Illegal login",
        ["diskfull"] = "HDD full",
        ["diskerror"] = "HDD error",
        ["nicbroken"] = "Network disconnected",
        ["ipconflict"] = "IP conflict",
        ["recordingfailure"] = "Record/capture exception",
        ["badvideo"] = "Video signal exception",
        ["videomismatch"] = "Video standard mismatch",
        ["resolutionmismatch"] = "Resolution mismatch",
        ["raidexception"] = "Array exception",
        ["spareException"] = "Hot spare exception",
        ["pocException"] = "PoC exception",
        ["abnormalReboot"] = "Abnormal reboot",
    };

    /// <summary>
    /// The <c>isSupportX</c> flags of <c>/ISAPI/Event/capabilities</c>, mapped to the event
    /// type each one gates. Every pair here was confirmed on live firmware by finding the
    /// flag true and the trigger present, or the flag false and the trigger absent, on at
    /// least one of the lab recorder, an M-series NVR and a hybrid DVR.
    /// </summary>
    /// <remarks>
    /// <c>isSupportViException</c> is deliberately absent: it reads false on a DVR that
    /// nonetheless lists a <c>badvideo</c> trigger, so pairing the two would report a working
    /// exception as unsupported. An unpaired flag is not a missing feature — which is exactly
    /// why <see cref="ExceptionTrigger.Exists"/> takes the trigger list as evidence in its
    /// own right.
    /// </remarks>
    private static readonly Dictionary<string, string> CapabilityFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["isSupportIllAccess"] = IllegalLogin,
        ["isSupportHDFull"] = "diskfull",
        ["isSupportHDError"] = "diskerror",
        ["isSupportNicBroken"] = "nicbroken",
        ["isSupportIpConflict"] = "ipconflict",
        ["isSupportRecordException"] = "recordingfailure",
        ["isSupportViMismatch"] = "videomismatch",
        ["isSupportViResMismatch"] = "resolutionmismatch",
        ["isSupportRaidException"] = "raidexception",
        ["isSupportSpareException"] = "spareException",
        ["isSupportPOCException"] = "pocException",
    };

    /// <summary>The recorder's word for it, or the vendor's own token when we have no word.</summary>
    public static string Label(string eventType) =>
        Labels.TryGetValue(eventType, out string? label) ? label : eventType;

    /// <summary>The event type a capabilities flag gates, or null when the flag pairs with none.</summary>
    public static string? EventTypeOfFlag(string flagName) =>
        CapabilityFlags.TryGetValue(flagName, out string? type) ? type : null;

    /// <summary>
    /// Whether a trigger belongs to the recorder rather than to a camera on it: no channel,
    /// no camera. <paramref name="childElementNames"/> is the trigger's own child elements,
    /// excluding its notification list.
    /// </summary>
    public static bool IsDeviceLevel(IEnumerable<string> childElementNames) =>
        !childElementNames.Any(n => n.Contains("Channel", StringComparison.OrdinalIgnoreCase) ||
                                    n.Contains("InputID", StringComparison.OrdinalIgnoreCase) ||
                                    n.Contains("PortID", StringComparison.OrdinalIgnoreCase));

    /// <summary>How an operator says a notification method out loud, for a report.</summary>
    public static string NotificationLabel(string method) => method.ToLowerInvariant() switch
    {
        "email" => "e-mail",
        "beep" => "audible warning",
        "center" => "notify surveillance centre",
        "io" => "alarm output",
        "record" => "start recording",
        "ftp" => "upload to FTP",
        "sms" => "SMS",
        "whitelightout" => "white light",
        "audioalarm" => "audible alarm",
        _ => method,
    };
}

/// <summary>
/// One exception type on one recorder: whether the firmware has it, and what it does when it
/// fires.
/// </summary>
/// <param name="EventType">The vendor's own token, verbatim — <c>illaccess</c>, not "illegal login".</param>
/// <param name="TriggerId">
/// The id the trigger list gave it, for the URL a write would use. Usually the event type
/// itself on a device-level exception; empty when the type is declared by capabilities but
/// absent from the list.
/// </param>
/// <param name="Declared">
/// What <c>/ISAPI/Event/capabilities</c> said, when it said anything. Null means the
/// capabilities document carries no flag for this type — <b>not</b> that the type is missing.
/// </param>
/// <param name="Listed">The trigger list carried it, which is evidence the firmware has it.</param>
/// <param name="Notifications">
/// The methods currently ticked, verbatim and in the device's order. <b>Presence is the
/// switch</b>: a notification that is off is simply absent from the list, so an empty list
/// means the exception fires and nothing happens.
/// </param>
public sealed record ExceptionTrigger(
    string EventType,
    string TriggerId = "",
    bool? Declared = null,
    bool Listed = false,
    IReadOnlyList<string>? Notifications = null)
{
    public IReadOnlyList<string> Notifications { get; init; } = Notifications ?? [];

    /// <summary>The recorder's own word for this exception.</summary>
    public string Label => ExceptionTypes.Label(EventType);

    /// <summary>
    /// Whether this recorder has the exception at all. The capabilities flag wins when there
    /// is one; otherwise a trigger in the list is proof enough — a firmware that lists a
    /// trigger it does not support has not been seen, while a firmware that supports one it
    /// does not flag has (a DVR's <c>badvideo</c>).
    /// </summary>
    public bool Exists => Declared ?? Listed;

    /// <summary>This exception is set to send mail when it fires.</summary>
    public bool SendsEmail => Notifications.Any(
        m => m.Equals(ExceptionTypes.EmailMethod, StringComparison.OrdinalIgnoreCase));

    /// <summary>"e-mail, audible warning", or "nothing" when the exception fires silently.</summary>
    public string NotificationsText => Notifications.Count == 0
        ? "nothing"
        : string.Join(", ", Notifications.Select(ExceptionTypes.NotificationLabel));
}

/// <summary>
/// Where a recorder's mail would actually go. Read separately from the triggers because the
/// two fail independently and an audit that conflates them is worse than no audit: a recorder
/// with the e-mail box ticked and no SMTP server configured reports "e-mails on illegal
/// login" and sends nothing, forever, with no error anywhere.
/// </summary>
public sealed record EmailDelivery
{
    /// <summary>The mail settings answered at all. False means unknown, never "no mail".</summary>
    public required bool Readable { get; init; }

    public string SenderName { get; init; } = "";
    public string SenderAddress { get; init; } = "";
    public string SmtpHost { get; init; } = "";
    public int SmtpPort { get; init; }
    public bool? Ssl { get; init; }
    public bool? Authenticated { get; init; }

    /// <summary>Every configured recipient, in the device's own order. Empty is a fault.</summary>
    public IReadOnlyList<string> Recipients { get; init; } = [];

    /// <summary>Why the read failed, when it did.</summary>
    public string? Error { get; init; }

    /// <summary>Nothing is known — the endpoint refused, or was never asked.</summary>
    public static readonly EmailDelivery Unknown = new() { Readable = false };

    /// <summary>
    /// Mail this recorder could plausibly deliver: a server to hand it to and somebody to
    /// hand it to. The sender address is *not* required here — plenty of relays accept an
    /// empty sender — but it is named in <see cref="Gap"/> so an operator sees it.
    /// </summary>
    public bool Configured => Readable && SmtpHost.Length > 0 && Recipients.Count > 0;

    /// <summary>What is missing, in the words of the recorder's own mail page. Empty when nothing is.</summary>
    public string Gap
    {
        get
        {
            if (!Readable)
                return "the mail settings could not be read";
            var missing = new List<string>(3);
            if (SmtpHost.Length == 0)
                missing.Add("no SMTP server");
            if (Recipients.Count == 0)
                missing.Add("no recipient");
            if (SenderAddress.Length == 0)
                missing.Add("no sender address");
            return string.Join(", ", missing);
        }
    }

    /// <summary>"smtp.example.com → ops@example.com", or what is missing instead.</summary>
    public string Summary
    {
        get
        {
            if (!Readable)
                return "?";
            if (!Configured)
                return Gap;
            return $"{HostText} -> {string.Join(", ", Recipients)}";
        }
    }

    /// <summary>
    /// The same fact in a column's worth of room: the server, plus how many people it would
    /// reach. A grid cell that truncates the recipient list mid-address tells an operator
    /// nothing, and the addresses are already in the verdict of the row that sends mail.
    /// </summary>
    public string ShortSummary
    {
        get
        {
            if (!Readable)
                return "?";
            if (SmtpHost.Length == 0)
                return "no mail configured";
            return Recipients.Count switch
            {
                0 => $"{HostText} (no recipient)",
                1 => HostText,
                _ => $"{HostText} ({Recipients.Count} recipients)",
            };
        }
    }

    private string HostText => SmtpPort is 0 or 25 ? SmtpHost : $"{SmtpHost}:{SmtpPort}";
}

/// <summary>Everything one recorder says about its own exceptions and where they go.</summary>
public sealed record DeviceExceptions
{
    public required IReadOnlyList<ExceptionTrigger> Triggers { get; init; }

    public required EmailDelivery Email { get; init; }

    /// <summary>
    /// Parts that were asked for and could not be read, each with why. Same rule as
    /// <see cref="DeviceConfiguration.Failures"/>: a recorder that answers its triggers and
    /// refuses its mail settings is worth a partial answer, as long as the partial answer
    /// says so.
    /// </summary>
    public IReadOnlyList<ConfigNote> Failures { get; init; } = [];

    /// <summary>One exception type by its vendor token, or null when the recorder has none.</summary>
    public ExceptionTrigger? Find(string eventType) => Triggers.FirstOrDefault(
        t => t.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase));

    /// <summary>The exceptions currently set to send mail.</summary>
    public IEnumerable<ExceptionTrigger> Emailing => Triggers.Where(t => t.SendsEmail);
}

/// <summary>
/// Opt-in capability: reading which faults a recorder raises and what it does about them. A
/// sibling of <see cref="IDeviceConfigClient"/> rather than part of it — the same split
/// <see cref="ICameraSettingsClient"/> and <see cref="IStorageClient"/> already make — because
/// "what does this recorder tell me when something goes wrong" is a different question from
/// "what is this recorder set to", and a vendor may answer one and not the other.
/// </summary>
/// <remarks>
/// Read-only on purpose. Ticking a notification on a customer's recorder is a write with no
/// undo inside DVRTool, and the audit is worth having on its own: a fleet sweep that finds
/// working SMTP on every recorder and the illegal-login box ticked on one is a finding, not a
/// build step.
/// </remarks>
public interface IExceptionNotificationClient
{
    /// <summary>
    /// Every device-level exception and the recorder's mail settings, in one call.
    /// Implementations tolerate a per-part failure by recording it in
    /// <see cref="DeviceExceptions.Failures"/> rather than throwing.
    /// </summary>
    Task<DeviceExceptions> GetExceptionsAsync(CancellationToken ct = default);
}
