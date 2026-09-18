using System.Net;
using DVRTool.Core;
using DVRTool.Vendors.Hikvision;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The exception audit — "if somebody starts guessing passwords tonight, which recorders
/// would tell me?" — over fixtures transcribed from the live captures of 2026-09-18 (the lab
/// I-series NVR, an M-series NVR and a hybrid DVR; addresses and mailboxes redacted).
/// </summary>
/// <remarks>
/// The traps these pin are the ones that would make the audit lie rather than fail: a
/// per-camera trigger counted as a recorder fault, a capability flag paired with the wrong
/// event type, and — the one the recorder's own web UI cannot show — an e-mail tick above
/// mail settings that would deliver nothing.
/// </remarks>
public class ExceptionAuditTests
{
    private const string Ns = "http://www.isapi.org/ver20/XMLSchema";

    private static readonly NvrConnection Conn = new()
    {
        Host = "192.0.2.10",
        Username = "admin",
        Password = "p@ss:word",
    };

    /// <summary>The I-series roster: no RAID, no hot spare, no video-standard mismatch.</summary>
    private const string EventCapsXml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <EventCap version="2.0" xmlns="{Ns}">
        <isSupportHDFull>true</isSupportHDFull>
        <isSupportHDError>true</isSupportHDError>
        <isSupportNicBroken>true</isSupportNicBroken>
        <isSupportIpConflict>true</isSupportIpConflict>
        <isSupportIllAccess>true</isSupportIllAccess>
        <isSupportViException>false</isSupportViException>
        <isSupportViMismatch>false</isSupportViMismatch>
        <isSupportRecordException>true</isSupportRecordException>
        <isSupportRaidException>false</isSupportRaidException>
        <isSupportSpareException>false</isSupportSpareException>
        <isSupportFaceSnap>true</isSupportFaceSnap>
        <isSupportTriggerCapCheck>true</isSupportTriggerCapCheck>
        </EventCap>
        """;

    /// <summary>
    /// A trigger list in miniature, with one of each shape the real ones carry: a camera
    /// event with an NVR channel, one with a DVR channel, an alarm input with a port, and the
    /// device-level exceptions — one silent, one beeping, one mailing.
    /// </summary>
    private const string TriggersXml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <EventTriggerList version="2.0" xmlns="{Ns}">
        <EventTrigger>
        <id>VMD-1</id><eventType>VMD</eventType><dynVideoInputChannelID>1</dynVideoInputChannelID>
        <EventTriggerNotificationList>
        <EventTriggerNotification><id>record-1</id><notificationMethod>record</notificationMethod>
        <dynVideoInputID>1</dynVideoInputID></EventTriggerNotification>
        </EventTriggerNotificationList>
        </EventTrigger>
        <EventTrigger>
        <id>videoloss-2</id><eventType>videoloss</eventType><videoInputChannelID>2</videoInputChannelID>
        <EventTriggerNotificationList>
        <EventTriggerNotification><id>email</id><notificationMethod>email</notificationMethod>
        </EventTriggerNotification>
        </EventTriggerNotificationList>
        </EventTrigger>
        <EventTrigger>
        <id>softIO-1</id><eventType>softIO</eventType><inputIOPortID>1</inputIOPortID>
        <EventTriggerNotificationList></EventTriggerNotificationList>
        </EventTrigger>
        <EventTrigger>
        <id>illaccess</id><eventType>illaccess</eventType>
        <EventTriggerNotificationList>
        <EventTriggerNotification><id>email</id><notificationMethod>email</notificationMethod>
        </EventTriggerNotification>
        </EventTriggerNotificationList>
        </EventTrigger>
        <EventTrigger>
        <id>diskfull</id><eventType>diskfull</eventType>
        <EventTriggerNotificationList></EventTriggerNotificationList>
        </EventTrigger>
        <EventTrigger>
        <id>diskerror</id><eventType>diskerror</eventType>
        <EventTriggerNotificationList>
        <EventTriggerNotification><id>beep</id><notificationMethod>beep</notificationMethod>
        </EventTriggerNotification>
        </EventTriggerNotificationList>
        </EventTrigger>
        <EventTrigger>
        <id>badvideo</id><eventType>badvideo</eventType>
        <EventTriggerNotificationList></EventTriggerNotificationList>
        </EventTrigger>
        </EventTriggerList>
        """;

    /// <summary>Mail that would arrive: a server, a sender and one real recipient among the blanks.</summary>
    private const string MailingXml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <mailingList version="2.0" xmlns="{Ns}">
        <mailing>
        <id>1</id>
        <sender>
        <name>Lab NVR</name>
        <emailAddress>nvr@example.test</emailAddress>
        <smtp>
        <enableAuthorization>true</enableAuthorization>
        <enableSSL>false</enableSSL>
        <addressingFormatType>hostname</addressingFormatType>
        <hostName>mail.example.test</hostName>
        <portNo>587</portNo>
        <accountName>nvr@example.test</accountName>
        </smtp>
        </sender>
        <receiverList>
        <receiver><id>1</id><name>Ops</name><emailAddress>ops@example.test</emailAddress></receiver>
        <receiver><id>2</id><name></name><emailAddress></emailAddress></receiver>
        <receiver><id>3</id><name></name><emailAddress></emailAddress></receiver>
        </receiverList>
        </mailing>
        </mailingList>
        """;

    /// <summary>The shipped-blank mail page: three placeholder recipients and no server.</summary>
    private const string EmptyMailingXml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <mailingList version="2.0" xmlns="{Ns}">
        <mailing>
        <id>1</id>
        <sender>
        <name></name><emailAddress></emailAddress>
        <smtp>
        <enableAuthorization>false</enableAuthorization><enableSSL>false</enableSSL>
        <addressingFormatType>hostname</addressingFormatType><hostName></hostName>
        <portNo>25</portNo><accountName></accountName>
        </smtp>
        </sender>
        <receiverList>
        <receiver><id>1</id><name></name><emailAddress></emailAddress></receiver>
        <receiver><id>2</id><name></name><emailAddress></emailAddress></receiver>
        <receiver><id>3</id><name></name><emailAddress></emailAddress></receiver>
        </receiverList>
        </mailing>
        </mailingList>
        """;

    private static MockHttpHandler Handler(Dictionary<string, string>? overrides = null)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/ISAPI/Event/capabilities"] = EventCapsXml,
            ["/ISAPI/Event/triggers"] = TriggersXml,
            ["/ISAPI/System/Network/mailing"] = MailingXml,
        };
        foreach (var (path, body) in overrides ?? [])
            bodies[path] = body;

        return new MockHttpHandler((req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (!bodies.TryGetValue(path, out string? content))
                return MockHttpHandler.Xml($"""
                    <ResponseStatus version="2.0" xmlns="{Ns}">
                    <statusCode>4</statusCode><statusString>Invalid Operation</statusString>
                    <subStatusCode>notSupport</subStatusCode>
                    </ResponseStatus>
                    """, HttpStatusCode.Forbidden);
            return MockHttpHandler.Xml(content);
        });
    }

    // ----- reading one recorder -----

    [Fact]
    public async Task GetExceptions_ReadsExactlyThreeDocuments()
    {
        var handler = Handler();
        using var client = new HikvisionClient(Conn, handler);

        await client.GetExceptionsAsync();

        Assert.Equal(
            ["/ISAPI/Event/capabilities", "/ISAPI/Event/triggers", "/ISAPI/System/Network/mailing"],
            handler.Requests.Select(r => r.Request.RequestUri!.AbsolutePath));

        // Never a single trigger by id: that path answers 503 "Device Busy" for a trigger
        // that does not exist, which no caller can tell from a genuinely busy recorder.
        Assert.DoesNotContain(handler.Requests,
            r => r.Request.RequestUri!.AbsolutePath.StartsWith("/ISAPI/Event/triggers/",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeviceLevelExceptionsAreTheOnesWithNoChannel()
    {
        using var client = new HikvisionClient(Conn, Handler());

        var read = await client.GetExceptionsAsync();

        // VMD, videoloss and softIO are about cameras and alarm inputs, not about the
        // recorder — and the videoloss entry mails, so counting it would make this audit
        // report alerting the recorder does not have for the fault being asked about.
        Assert.DoesNotContain(read.Triggers, t => t.EventType is "VMD" or "videoloss" or "softIO");
        Assert.Contains(read.Triggers, t => t.EventType == "illaccess");
        Assert.Contains(read.Triggers, t => t.EventType == "diskfull");
    }

    [Fact]
    public async Task IllegalLoginIsListedFirst_BecauseItIsTheQuestionBeingAsked()
    {
        using var client = new HikvisionClient(Conn, Handler());

        var read = await client.GetExceptionsAsync();

        Assert.Equal(ExceptionTypes.IllegalLogin, read.Triggers[0].EventType);
        Assert.Equal("Illegal login", read.Triggers[0].Label);
    }

    [Fact]
    public async Task ATriggerTheCapabilitiesDocumentDoesNotFlag_StillExists()
    {
        using var client = new HikvisionClient(Conn, Handler());

        var read = await client.GetExceptionsAsync();

        // isSupportViException reads false on a DVR that lists badvideo anyway, so the list
        // is evidence in its own right. Reporting it as unsupported would tell an operator
        // there is nothing to turn on.
        var badVideo = read.Find("badvideo");
        Assert.NotNull(badVideo);
        Assert.Null(badVideo.Declared);
        Assert.True(badVideo.Exists);
    }

    [Fact]
    public async Task ATypeDeclaredButNotListed_IsReportedWithItsFlag()
    {
        using var client = new HikvisionClient(Conn, Handler());

        var read = await client.GetExceptionsAsync();

        // recordingfailure is flagged true and absent from this (miniature) list.
        var record = read.Find("recordingfailure");
        Assert.NotNull(record);
        Assert.True(record.Declared);
        Assert.True(record.Exists);
        Assert.Empty(record.Notifications);
    }

    [Fact]
    public async Task AFlaggedFalseType_ExistsNot()
    {
        using var client = new HikvisionClient(Conn, Handler());

        var read = await client.GetExceptionsAsync();

        var raid = read.Find("raidexception");
        Assert.NotNull(raid);
        Assert.False(raid.Exists);
    }

    [Fact]
    public async Task NotificationsAreReadAsTheyAreTicked()
    {
        using var client = new HikvisionClient(Conn, Handler());

        var read = await client.GetExceptionsAsync();

        Assert.True(read.Find(ExceptionTypes.IllegalLogin)!.SendsEmail);
        Assert.Equal("audible warning", read.Find("diskerror")!.NotificationsText);

        // An empty notification list is not missing data: the exception fires and nothing
        // happens, which is what the report must say.
        Assert.Equal("nothing", read.Find("diskfull")!.NotificationsText);
    }

    [Fact]
    public async Task MailSettingsAreReadWithoutTheBlankPlaceholderRecipients()
    {
        using var client = new HikvisionClient(Conn, Handler());

        var read = await client.GetExceptionsAsync();

        Assert.True(read.Email.Readable);
        Assert.True(read.Email.Configured);
        Assert.Equal("mail.example.test", read.Email.SmtpHost);
        Assert.Equal(587, read.Email.SmtpPort);
        Assert.Equal(["ops@example.test"], read.Email.Recipients);
        Assert.Equal("nvr@example.test", read.Email.SenderAddress);
        Assert.True(read.Email.Authenticated);
    }

    [Fact]
    public async Task AnUnreadableMailPage_IsUnknownAndNotEmpty()
    {
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.AbsolutePath == "/ISAPI/System/Network/mailing"
                ? MockHttpHandler.Xml("busy", HttpStatusCode.ServiceUnavailable)
                : MockHttpHandler.Xml(
                    req.RequestUri.AbsolutePath == "/ISAPI/Event/triggers"
                        ? TriggersXml
                        : EventCapsXml));
        using var client = new HikvisionClient(Conn, handler);

        var read = await client.GetExceptionsAsync();

        Assert.False(read.Email.Readable);
        Assert.False(read.Email.Configured);
        Assert.Contains(read.Failures, f => f.Label == "the mail settings");
    }

    // ----- the audit's verdicts -----

    private static DeviceExceptions Read(ExceptionTrigger? trigger, EmailDelivery email) =>
        new() { Triggers = trigger is null ? [] : [trigger], Email = email };

    private static readonly EmailDelivery GoodMail = new()
    {
        Readable = true,
        SmtpHost = "mail.example.test",
        SenderAddress = "nvr@example.test",
        Recipients = ["ops@example.test"],
    };

    private static readonly EmailDelivery BlankMail = new() { Readable = true };

    [Fact]
    public void ARecorderWhoseFirmwareLacksTheException_IsNotAGap()
    {
        var row = ExceptionAuditRow.For("DVR", ExceptionTypes.IllegalLogin,
            Read(new ExceptionTrigger(ExceptionTypes.IllegalLogin, Declared: false), GoodMail));

        Assert.Equal(ExceptionEmailState.Unsupported, row.State);
        Assert.False(row.IsGap); // there is nothing to turn on
        Assert.False(row.Emails);
    }

    [Fact]
    public void TickedAboveBlankMailSettings_IsItsOwnVerdict()
    {
        var row = ExceptionAuditRow.For("NVR", ExceptionTypes.IllegalLogin,
            Read(new ExceptionTrigger(ExceptionTypes.IllegalLogin, Declared: true, Listed: true,
                Notifications: ["email"]), BlankMail));

        // The state this whole feature exists to catch: the Exception page says yes, the mail
        // page is empty, and the recorder reports neither.
        Assert.Equal(ExceptionEmailState.GoesNowhere, row.State);
        Assert.False(row.Emails);
        Assert.True(row.IsGap);
        Assert.Contains("sends nothing", row.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportedAndUnticked_SaysWhatDoesHappenInstead()
    {
        var row = ExceptionAuditRow.For("NVR", ExceptionTypes.IllegalLogin,
            Read(new ExceptionTrigger(ExceptionTypes.IllegalLogin, Declared: true, Listed: true,
                Notifications: ["beep"]), GoodMail));

        Assert.Equal(ExceptionEmailState.NotEnabled, row.State);
        Assert.True(row.IsGap);
        Assert.Contains("audible warning", row.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportedTickedAndDeliverable_NamesWhoGetsIt()
    {
        var row = ExceptionAuditRow.For("NVR", ExceptionTypes.IllegalLogin,
            Read(new ExceptionTrigger(ExceptionTypes.IllegalLogin, Declared: true, Listed: true,
                Notifications: ["email", "beep"]), GoodMail));

        Assert.Equal(ExceptionEmailState.Emails, row.State);
        Assert.True(row.Emails);
        Assert.False(row.IsGap);
        Assert.Contains("ops@example.test", row.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedReadIsARow_NeverAnOmissionAndNeverFine()
    {
        var audit = ExceptionAudit.Build(ExceptionTypes.IllegalLogin, [
            ExceptionAuditRow.For("Good", ExceptionTypes.IllegalLogin,
                Read(new ExceptionTrigger(ExceptionTypes.IllegalLogin, Declared: true,
                    Listed: true, Notifications: ["email"]), GoodMail)),
            ExceptionAuditRow.Failed("Offline", ExceptionTypes.IllegalLogin, "connection refused"),
            ExceptionAuditRow.NotImplemented("A Dahua", ExceptionTypes.IllegalLogin, "Dahua"),
        ]);

        Assert.Equal(3, audit.Rows.Count);
        Assert.True(audit.IsPartial);
        Assert.Single(audit.Emailing);

        // The two blank rows are counted apart: one is a site to go look at, the other is a
        // vendor this was never built for. Neither may read as "nothing configured".
        Assert.Single(audit.Unreachable);
        Assert.Single(audit.NotImplemented);
        Assert.Contains("could not be read", audit.Summary, StringComparison.Ordinal);
        Assert.Contains("not Hikvision", audit.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSummaryCountsWhatAnOperatorWouldActOn()
    {
        var audit = ExceptionAudit.Build(ExceptionTypes.IllegalLogin, [
            ExceptionAuditRow.For("Mails", ExceptionTypes.IllegalLogin,
                Read(new ExceptionTrigger(ExceptionTypes.IllegalLogin, Declared: true,
                    Listed: true, Notifications: ["email"]), GoodMail)),
            ExceptionAuditRow.For("Unticked", ExceptionTypes.IllegalLogin,
                Read(new ExceptionTrigger(ExceptionTypes.IllegalLogin, Declared: true,
                    Listed: true), GoodMail)),
            ExceptionAuditRow.For("No mail at all", ExceptionTypes.IllegalLogin,
                Read(new ExceptionTrigger(ExceptionTypes.IllegalLogin, Declared: true,
                    Listed: true), BlankMail)),
        ]);

        Assert.Equal("1 of 3 e-mail on illegal login, 2 could and do not, " +
            "1 of those have no mail settings at all.", audit.Summary);
    }

    [Fact]
    public void AGridCellSaysTheServerAndHowManyPeople_NotATruncatedAddress()
    {
        var mail = GoodMail with { Recipients = ["ops@example.test", "oncall@example.test"] };

        Assert.Equal("mail.example.test (2 recipients)", mail.ShortSummary);
        Assert.Equal("no mail configured", BlankMail.ShortSummary);
        Assert.Equal("?", EmailDelivery.Unknown.ShortSummary);
    }
}
