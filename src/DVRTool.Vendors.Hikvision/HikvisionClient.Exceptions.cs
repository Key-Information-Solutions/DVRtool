using System.Globalization;
using System.Xml.Linq;
using DVRTool.Core;

namespace DVRTool.Vendors.Hikvision;

/// <summary>
/// The <see cref="IExceptionNotificationClient"/> face of the Hikvision client: the recorder's
/// own faults — the web UI's <b>Exception</b> page — and whether any of them reach a human.
/// </summary>
/// <remarks>
/// <para>
/// Three documents, all read-only, all observed live on 2026-09-18 across an I-series NVR
/// (DS-7716NI-I4/16P, V4.61.030), an M-series NVR (DS-9632NI-M8, V5.04.081) and a hybrid DVR
/// (iDS-7316HUHI-M4/S, V4.51.100):
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>/ISAPI/Event/capabilities</c> — the <c>isSupportX</c> roster, which is the honest
/// answer to "does this firmware have an Illegal Login exception at all"
/// (<c>isSupportIllAccess</c>). It is a per-model document: <c>isSupportViMismatch</c> is
/// true on the DVR and false on both NVRs, <c>isSupportSpareException</c> true only on the
/// M-series.
/// </description></item>
/// <item><description>
/// <c>/ISAPI/Event/triggers</c> — every trigger on the box, camera events and recorder
/// exceptions in one list (102 entries on a 16-channel NVR, 251 on the DVR), each with the
/// notification methods currently ticked. <b>Presence is the switch</b>: a method that is
/// off is simply absent, so an empty <c>EventTriggerNotificationList</c> means the exception
/// fires and nothing happens.
/// </description></item>
/// <item><description>
/// <c>/ISAPI/System/Network/mailing</c> — the SMTP settings, which is the half of the
/// question the Exception page cannot answer. A recorder with the e-mail box ticked and a
/// blank SMTP server looks configured on its own web UI and sends nothing.
/// </description></item>
/// </list>
/// <para>
/// <b>The trap worth naming</b>: asking for a trigger that does not exist —
/// <c>/ISAPI/Event/triggers/notARealEventType</c> — answers <b>503 "Device Busy"</b> with
/// <c>isapi get event trigger is failed</c>, not a 404. <c>/ISAPI/Event/triggers/capabilities</c>
/// answers the same 503 on every firmware tried, for the same reason: there is no such node.
/// So a 503 here is "no such trigger", and code that retries it as a busy device waits
/// forever. This client never asks for a single trigger by id; it reads the list, which is
/// one request and cannot be lied to this way.
/// </para>
/// </remarks>
public sealed partial class HikvisionClient : IExceptionNotificationClient
{
    private const string EventCapsPath = "/ISAPI/Event/capabilities";
    private const string EventTriggersPath = "/ISAPI/Event/triggers";
    private const string MailingPath = "/ISAPI/System/Network/mailing";

    public async Task<DeviceExceptions> GetExceptionsAsync(CancellationToken ct = default)
    {
        var failures = new List<ConfigNote>();

        var capsDoc = await ReadOrNoteAsync(EventCapsPath, "the exception roster", failures, ct);
        var triggerDoc = await ReadOrNoteAsync(EventTriggersPath, "the trigger list", failures, ct);
        var mailDoc = await ReadOrNoteAsync(MailingPath, "the mail settings", failures, ct);

        var declared = capsDoc is null
            ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            : ParseEventCapabilities(capsDoc);
        var listed = triggerDoc is null
            ? []
            : ParseDeviceTriggers(triggerDoc);

        return new DeviceExceptions
        {
            Triggers = MergeTriggers(declared, listed),
            Email = mailDoc is null
                ? EmailDelivery.Unknown with
                {
                    Error = failures.FirstOrDefault(f => f.Label == "the mail settings")?.Value
                        ?? "the mail settings could not be read",
                }
                : ParseMailing(mailDoc),
            Failures = failures,
        };
    }

    /// <summary>
    /// A read whose failure is a note rather than an exception — the per-part tolerance
    /// <see cref="DeviceConfiguration.Failures"/> already uses. A 401 still propagates:
    /// every silent retry burns an attempt toward the recorder's own illegal-login lockout,
    /// which would be an ironic way to run this particular audit.
    /// </summary>
    private async Task<XDocument?> ReadOrNoteAsync(string path, string what,
        List<ConfigNote> failures, CancellationToken ct)
    {
        var probe = new List<Exception>();
        var doc = await TryGetXmlAsync(path, ct, probe);
        if (doc is null)
            failures.Add(new ConfigNote("exceptions", what,
                probe.Count > 0 ? probe[0].Message : $"{path} is not supported on this device"));
        return doc;
    }

    /// <summary>
    /// The <c>isSupportX</c> flags that pair with an exception type. Flags with no pairing —
    /// the smart-detection roster, <c>isSupportViException</c> — are dropped here rather than
    /// guessed at: a flag matched to the wrong event type reports a working exception as
    /// unsupported, which is the one answer this audit must never give.
    /// </summary>
    internal static Dictionary<string, bool> ParseEventCapabilities(XDocument doc)
    {
        var declared = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in doc.Root?.Elements() ?? [])
        {
            if (ExceptionTypes.EventTypeOfFlag(el.Name.LocalName) is not string type)
                continue;
            declared[type] = el.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return declared;
    }

    /// <summary>
    /// The device-level triggers out of the whole list — the ones with no channel. A DVR's
    /// list carries 22 <c>videoloss</c> and 34 <c>softIO</c> entries that are about cameras
    /// and alarm inputs; the recorder's own faults are the handful that name no channel and
    /// no I/O port.
    /// </summary>
    internal static List<ExceptionTrigger> ParseDeviceTriggers(XDocument doc)
    {
        var found = new List<ExceptionTrigger>();
        foreach (var trigger in ElementsNamed(doc.Root!, "EventTrigger"))
        {
            string type = Child(trigger, "eventType")?.Trim() ?? "";
            if (type.Length == 0)
                continue;

            var ownChildren = trigger.Elements()
                .Where(e => e.Name.LocalName != "EventTriggerNotificationList")
                .Select(e => e.Name.LocalName);
            if (!ExceptionTypes.IsDeviceLevel(ownChildren))
                continue;

            var methods = trigger
                .Descendants().Where(e => e.Name.LocalName == "notificationMethod")
                .Select(e => e.Value.Trim())
                .Where(m => m.Length > 0)
                .ToList();

            found.Add(new ExceptionTrigger(type, Child(trigger, "id")?.Trim() ?? type,
                Listed: true, Notifications: methods));
        }
        return found;
    }

    /// <summary>
    /// The two sources joined. A type the capabilities document declares but the list omits
    /// is still reported (with no notifications), and a type the list carries with no
    /// capability flag is reported as existing — the DVR that lists <c>badvideo</c> while
    /// <c>isSupportViException</c> reads false is why neither source alone is trusted.
    /// </summary>
    internal static List<ExceptionTrigger> MergeTriggers(
        IReadOnlyDictionary<string, bool> declared, List<ExceptionTrigger> listed)
    {
        var merged = listed
            .Select(t => declared.TryGetValue(t.EventType, out bool flag)
                ? t with { Declared = flag }
                : t)
            .ToList();

        foreach (var (type, flag) in declared)
        {
            if (!merged.Any(t => t.EventType.Equals(type, StringComparison.OrdinalIgnoreCase)))
                merged.Add(new ExceptionTrigger(type, Declared: flag));
        }

        return merged
            .OrderByDescending(t => t.EventType.Equals(ExceptionTypes.IllegalLogin,
                StringComparison.OrdinalIgnoreCase))
            .ThenBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The mail settings. The first <c>mailing</c> entry is the one the Exception page's
    /// e-mail tick uses; an empty recipient slot is a placeholder the firmware ships three of,
    /// not a recipient.
    /// </summary>
    internal static EmailDelivery ParseMailing(XDocument doc)
    {
        var mailing = ElementsNamed(doc.Root!, "mailing").FirstOrDefault() ?? doc.Root!;
        var sender = ElementsNamed(mailing, "sender").FirstOrDefault();
        var smtp = sender is null ? null : ElementsNamed(sender, "smtp").FirstOrDefault();

        string host = "";
        if (smtp is not null)
        {
            // The format type says which of the two address fields is the live one, but a
            // firmware that says "hostname" and fills in ipAddress has been seen on other
            // documents, so whichever is filled wins and the declared type only breaks ties.
            string byName = Child(smtp, "hostName")?.Trim() ?? "";
            string byAddress = Child(smtp, "ipAddress")?.Trim() ?? "";
            string declaredType = Child(smtp, "addressingFormatType")?.Trim() ?? "";
            host = declaredType.Equals("ipaddress", StringComparison.OrdinalIgnoreCase)
                ? (byAddress.Length > 0 ? byAddress : byName)
                : (byName.Length > 0 ? byName : byAddress);
        }

        var recipients = ElementsNamed(mailing, "receiver")
            .Select(r => Child(r, "emailAddress")?.Trim() ?? "")
            .Where(a => a.Length > 0)
            .ToList();

        return new EmailDelivery
        {
            Readable = true,
            SenderName = (sender is null ? null : Child(sender, "name"))?.Trim() ?? "",
            SenderAddress = (sender is null ? null : Child(sender, "emailAddress"))?.Trim() ?? "",
            SmtpHost = host,
            SmtpPort = smtp is not null &&
                int.TryParse(Child(smtp, "portNo"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int port)
                ? port
                : 0,
            Ssl = ParseFlag(smtp, "enableSSL"),
            Authenticated = ParseFlag(smtp, "enableAuthorization"),
            Recipients = recipients,
        };
    }

    private static bool? ParseFlag(XElement? parent, string name)
    {
        string? raw = parent is null ? null : Child(parent, name);
        return raw is null ? null : raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
