using DVRTool.Core;
using DVRTool.Vendors.Hikvision;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The check that stops DVRTool acting on the wrong system: several recorders behind one
/// address, told apart only by forwarded port, all answering to one shared account.
/// Authentication passes against every one of them, so identity has to be proven separately.
/// </summary>
public class DeviceIdentityTests
{
    /// <summary>A store on a throwaway file — never the operator's real pin file.</summary>
    private sealed class TempStore : IDisposable
    {
        private readonly string _dir;

        public TempStore()
        {
            _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dvrtool-identity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            PinFile = System.IO.Path.Combine(_dir, "identities.json");
            Store = new DeviceIdentityStore(PinFile);
        }

        public string PinFile { get; }

        public DeviceIdentityStore Store { get; }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }
    }

    private static DeviceFingerprint Print(string serial, string model = "DS-7616NI-Q2") =>
        new() { Serial = serial, Model = model };

    // ----- addresses -----

    [Fact]
    public void Address_IsHostAndPort_Lowercased()
    {
        Assert.Equal("nvr.site.local:8081", DeviceAddress.Format(" NVR.Site.Local ", 8081));
        // The whole point: one host, two systems, two distinct keys.
        Assert.NotEqual(DeviceAddress.Format("10.0.0.5", 8081), DeviceAddress.Format("10.0.0.5", 8082));
    }

    [Theory]
    [InlineData("10.0.0.5", "10.0.0.5", 8000)]
    [InlineData("10.0.0.5:8001", "10.0.0.5", 8001)]
    [InlineData("  10.0.0.5:8001  ", "10.0.0.5", 8001)]
    [InlineData("[fe80::1]:8001", "fe80::1", 8001)]
    [InlineData("[fe80::1]", "fe80::1", 8000)]
    // A bare IPv6 literal is all colons and no port: taken whole rather than truncated.
    [InlineData("fe80::1", "fe80::1", 8000)]
    public void TryParse_SplitsHostFromPort(string text, string expectedHost, int expectedPort)
    {
        Assert.True(DeviceAddress.TryParse(text, 8000, out string? host, out int port, out _));
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10.0.0.5:0")]
    [InlineData("10.0.0.5:70000")]
    [InlineData("10.0.0.5:sdk")]
    [InlineData(":8000")]
    [InlineData("[fe80::1")]
    public void TryParse_RejectsNonsense(string text)
    {
        Assert.False(DeviceAddress.TryParse(text, 8000, out _, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ----- fingerprints -----

    [Fact]
    public void Fingerprint_ComparesSerialsIgnoringCaseAndPadding()
    {
        Assert.True(Print(" ds-7616ni-q216202301 ").SameDevice(Print("DS-7616NI-Q216202301")));
        Assert.False(Print("DS-7616NI-Q216202301").SameDevice(Print("DS-7616NI-Q216202302")));
    }

    /// <summary>
    /// Two devices that report no serial are not "the same device" — they are two unanswered
    /// questions. Treating blank as a value would re-create the exact hole this closes: every
    /// serial-less recorder matching every other.
    /// </summary>
    [Fact]
    public void Fingerprint_WithoutASerial_MatchesNothing()
    {
        var blank = Print("   ");
        Assert.False(blank.IsUsable);
        Assert.False(blank.SameDevice(Print("")));
        Assert.False(blank.SameDevice(Print("DS-7616NI-Q216202301")));
    }

    // ----- the pin store -----

    [Fact]
    public void Verify_PinsOnFirstContact_ThenMatches()
    {
        using var temp = new TempStore();

        var first = temp.Store.Verify("10.0.0.5:8081", Print("SERIAL-A"));
        Assert.Equal(IdentityVerdict.FirstContact, first.Verdict);
        Assert.True(first.IsTrusted);
        Assert.True(File.Exists(temp.PinFile));

        var second = temp.Store.Verify("10.0.0.5:8081", Print("SERIAL-A"));
        Assert.Equal(IdentityVerdict.Match, second.Verdict);
        // Nothing to say when the answer is the expected one.
        Assert.Equal("", second.Message);
    }

    /// <summary>
    /// The motivating case, end to end: two systems on one host, and the port that used to
    /// reach the second now reaches the first.
    /// </summary>
    [Fact]
    public void Verify_CatchesASwappedPortForward()
    {
        using var temp = new TempStore();
        temp.Store.Verify("10.0.0.5:8081", Print("SERIAL-A"));
        temp.Store.Verify("10.0.0.5:8082", Print("SERIAL-B"));

        var swapped = temp.Store.Verify("10.0.0.5:8082", Print("SERIAL-A"));

        Assert.Equal(IdentityVerdict.Mismatch, swapped.Verdict);
        Assert.False(swapped.IsTrusted);
        // Both serials named, or the operator cannot tell which record is wrong.
        Assert.Contains("SERIAL-A", swapped.Message);
        Assert.Contains("SERIAL-B", swapped.Message);
        // And the pin is not quietly moved to the impostor.
        Assert.Equal("SERIAL-B", temp.Store.Pinned("10.0.0.5:8082")!.Serial);
    }

    [Fact]
    public void Verify_HonoursTheCallersOwnExpectationFirst()
    {
        using var temp = new TempStore();

        var check = temp.Store.Verify("10.0.0.5:80", Print("SERIAL-A"), "SERIAL-B", "'Store 3'");

        Assert.Equal(IdentityVerdict.Mismatch, check.Verdict);
        Assert.Contains("'Store 3' expects", check.Message);
        // A failed expectation must not pin either — the address stays unclaimed.
        Assert.Null(temp.Store.Pinned("10.0.0.5:80"));
    }

    [Fact]
    public void Verify_WithoutASerial_IsUnverifiableAndPinsNothing()
    {
        using var temp = new TempStore();

        var check = temp.Store.Verify("10.0.0.5:80", Print("", model: "OEM DVR"));

        Assert.Equal(IdentityVerdict.Unverifiable, check.Verdict);
        Assert.False(check.IsTrusted);
        Assert.Null(temp.Store.Pinned("10.0.0.5:80"));
    }

    [Fact]
    public void ForgetAndRepin_AreHowAReplacedRecorderIsAccepted()
    {
        using var temp = new TempStore();
        temp.Store.Verify("10.0.0.5:80", Print("SERIAL-A"));

        temp.Store.Forget("10.0.0.5:80");
        Assert.Null(temp.Store.Pinned("10.0.0.5:80"));
        Assert.Equal(IdentityVerdict.FirstContact,
            temp.Store.Verify("10.0.0.5:80", Print("SERIAL-B")).Verdict);

        temp.Store.Repin("10.0.0.5:80", Print("SERIAL-C"));
        Assert.Equal(IdentityVerdict.Match, temp.Store.Verify("10.0.0.5:80", Print("SERIAL-C")).Verdict);
    }

    /// <summary>
    /// An unreadable pin file must not read as an empty one: empty would route every pinned
    /// address into the first-contact branch and re-pin whatever answered — the check
    /// disabling itself in silence, which is worse than the check failing loudly.
    /// </summary>
    [Fact]
    public void Verify_FailsClosed_WhenAnExistingPinFileCannotBeRead()
    {
        using var temp = new TempStore();
        temp.Store.Verify("10.0.0.5:80", Print("SERIAL-A"));

        using var exclusive = new FileStream(
            temp.PinFile, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.ThrowsAny<IOException>(() => temp.Store.Verify("10.0.0.5:80", Print("SERIAL-B")));
    }

    [Fact]
    public void Verify_StartsFresh_OnAHandEditedFileThatIsNotJson()
    {
        using var temp = new TempStore();
        File.WriteAllText(temp.PinFile, "{ this is not json");

        Assert.Equal(IdentityVerdict.FirstContact,
            temp.Store.Verify("10.0.0.5:80", Print("SERIAL-A")).Verdict);
    }

    [Fact]
    public void Guard_Ensure_ThrowsOnlyOnAMismatch()
    {
        using var temp = new TempStore();
        var ok = temp.Store.Verify("10.0.0.5:80", Print("SERIAL-A"));
        Assert.Same(ok, DeviceIdentityGuard.Ensure(ok));

        var bad = temp.Store.Verify("10.0.0.5:80", Print("SERIAL-B"));
        var thrown = Assert.Throws<DeviceIdentityException>(() => DeviceIdentityGuard.Ensure(bad));
        Assert.Equal(IdentityVerdict.Mismatch, thrown.Check.Verdict);
    }

    /// <summary>The pin key is the port that is authenticated on, not RTSP or the SDK port.</summary>
    [Fact]
    public void Guard_PinsAnNvrAgainstItsWebPort_AndAPanelAgainstItsSdkPort()
    {
        Assert.Equal("10.0.0.5:8081", DeviceIdentityGuard.AddressOf(new NvrConnection
        {
            Host = "10.0.0.5", HttpPort = 8081, RtspPort = 5541, SdkPort = 8000,
            Username = "admin", Password = "x",
        }));
        Assert.Equal("10.0.0.5:8001", DeviceIdentityGuard.AddressOf(new AccessPanelConnection
        {
            Host = "10.0.0.5", SdkPort = 8001, Username = "admin", Password = "x",
        }));
    }

    // ----- the fleet audit -----

    [Fact]
    public void Audit_RefusesTwoRecordsOnOneAddress()
    {
        var issues = FleetAudit.Inspect([
            new FleetRecord("Store 3", "10.0.0.5", 8081, "SERIAL-A"),
            new FleetRecord("Store 4", "10.0.0.5", 8081, null),
        ]);

        var issue = Assert.Single(issues, i => i.Kind == FleetIssueKind.DuplicateAddress);
        Assert.Equal(FleetIssueSeverity.Error, issue.Severity);
        Assert.Contains("'Store 3'", issue.Message);
        Assert.Contains("'Store 4'", issue.Message);
    }

    [Fact]
    public void Audit_ReportsOneRecorderSavedTwice()
    {
        var issues = FleetAudit.Inspect([
            new FleetRecord("Store 3", "10.0.0.5", 8081, "serial-a"),
            new FleetRecord("Store 4", "10.0.0.5", 8082, "SERIAL-A"),
        ]);

        var issue = Assert.Single(issues, i => i.Kind == FleetIssueKind.SameDevice);
        Assert.Equal(FleetIssueSeverity.Warning, issue.Severity);
        Assert.Contains("same physical device", issue.Message);
    }

    /// <summary>
    /// Sharing a host is a normal site layout, so it is stated and not scolded — but it is
    /// stated, because it is the configuration every other finding here depends on.
    /// </summary>
    [Fact]
    public void Audit_TreatsASharedHostAsInformation()
    {
        var issues = FleetAudit.Inspect([
            new FleetRecord("Store 3", "10.0.0.5", 8081, "SERIAL-A"),
            new FleetRecord("Store 4", "10.0.0.5", 8082, "SERIAL-B"),
        ]);

        Assert.Equal(FleetIssueKind.SharedHost, Assert.Single(issues).Kind);
        Assert.Equal(FleetIssueSeverity.Info, issues[0].Severity);
    }

    [Fact]
    public void Audit_IsSilentOnAnOrdinaryFleet()
    {
        Assert.Empty(FleetAudit.Inspect([
            new FleetRecord("Store 3", "10.0.0.5", 80, "SERIAL-A"),
            new FleetRecord("Store 4", "10.0.0.6", 80, "SERIAL-B"),
        ]));
    }

    [Fact]
    public void Audit_ForOneRecord_IgnoresProblemsBetweenTheOthers()
    {
        var subject = new FleetRecord("New site", "10.0.0.9", 80, "SERIAL-C");
        var issues = FleetAudit.InspectFor(subject, [
            new FleetRecord("Store 3", "10.0.0.5", 8081, "SERIAL-A"),
            new FleetRecord("Store 4", "10.0.0.5", 8081, "SERIAL-A"),
        ]);

        Assert.Empty(issues);
    }

    // ----- the connectivity probe -----

    private const string Ns = "http://www.hikvision.com/ver20/XMLSchema";

    private static MockHttpHandler DeviceInfoHandler(string serial) =>
        new((_, _) => MockHttpHandler.Xml($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <DeviceInfo xmlns="{Ns}" version="2.0">
              <deviceName>Front Office NVR</deviceName>
              <model>DS-7616NI-Q2</model>
              <serialNumber>{serial}</serialNumber>
              <firmwareVersion>V4.62.210</firmwareVersion>
            </DeviceInfo>
            """));

    private static NvrConnection ProbeConn => new()
    {
        Host = "10.0.0.5", HttpPort = 8082, Username = "admin", Password = "x",
    };

    [Fact]
    public async Task WebProbe_ReportsTheSerialAndPinsIt()
    {
        using var temp = new TempStore();
        var conn = ProbeConn;

        var result = await ConnectivityProbe.ProbeWebAsync(conn,
            () => new HikvisionClient(conn, DeviceInfoHandler("SERIAL-B")),
            identities: temp.Store);

        Assert.Equal(ProbeStatus.Ok, result.Status);
        Assert.Equal(ProbeSeverity.Pass, result.Severity);
        Assert.Equal(IdentityVerdict.FirstContact, result.Identity!.Verdict);
        Assert.Equal("SERIAL-B", temp.Store.Pinned("10.0.0.5:8082")!.Serial);
    }

    /// <summary>
    /// A perfect port with the wrong recorder behind it. This must never render as a pass:
    /// "all three ports reachable" is what sends an installer on to export the wrong footage.
    /// </summary>
    [Fact]
    public async Task WebProbe_FailsWhenAnotherRecorderAnswersTheSamePort()
    {
        using var temp = new TempStore();
        var conn = ProbeConn;
        temp.Store.Verify("10.0.0.5:8082", Print("SERIAL-B"));

        var result = await ConnectivityProbe.ProbeWebAsync(conn,
            () => new HikvisionClient(conn, DeviceInfoHandler("SERIAL-A")),
            identities: temp.Store);

        Assert.Equal(ProbeStatus.WrongDevice, result.Status);
        Assert.Equal(ProbeSeverity.Fail, result.Severity);
        Assert.Contains("WRONG DEVICE", result.Detail);
    }

    [Fact]
    public async Task WebProbe_HonoursAnExpectedSerial_EvenOnAFirstContact()
    {
        using var temp = new TempStore();
        var conn = ProbeConn;

        var result = await ConnectivityProbe.ProbeWebAsync(conn,
            () => new HikvisionClient(conn, DeviceInfoHandler("SERIAL-A")),
            expectedSerial: "SERIAL-B", expectedBy: "'Store 4'", identities: temp.Store);

        Assert.Equal(ProbeStatus.WrongDevice, result.Status);
        Assert.Contains("'Store 4' expects", result.Detail);
    }

    /// <summary>
    /// A recorder that reports no serial is a caution, not a pass: the port works and the
    /// question of which system this is went unanswered.
    /// </summary>
    [Fact]
    public async Task WebProbe_IsCautious_WhenTheDeviceReportsNoSerial()
    {
        using var temp = new TempStore();
        var conn = ProbeConn;

        var result = await ConnectivityProbe.ProbeWebAsync(conn,
            () => new HikvisionClient(conn, DeviceInfoHandler("")),
            identities: temp.Store);

        Assert.Equal(ProbeStatus.Partial, result.Status);
        Assert.Equal(ProbeSeverity.Caution, result.Severity);
        Assert.Equal(IdentityVerdict.Unverifiable, result.Identity!.Verdict);
    }
}
