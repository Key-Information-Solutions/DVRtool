using System.Net;
using System.Xml.Linq;
using DVRTool.Core;
using DVRTool.Vendors.Hikvision;
using Xunit;

namespace DVRTool.Tests;

public class IpFilterTests
{
    // ----- the documents, as the M-series NVR and the lab recorder sent them -----

    private const string Ns = "http://www.isapi.org/ver20/XMLSchema";

    private static string FilterDoc(bool enabled, string mode, params string[] addresses) => $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <IPFilter version="2.0" xmlns="{Ns}">
        <enabled>{(enabled ? "true" : "false")}</enabled>
        <permissionType>{mode}</permissionType>
        <IPFilterAddressList size="32">
        {string.Concat(addresses.Select((a, i) => $"""
            <IPFilterAddress><id>{i + 1}</id><permissionType>{mode}</permissionType>
            <addressFilterType>mask</addressFilterType>
            <AddressMask><ipAddress>{a}</ipAddress></AddressMask></IPFilterAddress>
            """))}
        </IPFilterAddressList>
        </IPFilter>
        """;

    private const string FilterCaps = $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <IPFilter version="2.0" xmlns="{Ns}">
        <enabled opt="true,false"/>
        <permissionType opt="deny,allow"/>
        <IPFilterAddressList size="32">
        <IPFilterAddress>
        <id min="1" max="32"/>
        <permissionType opt="deny,allow"/>
        <addressFilterType opt="mask"/>
        <AddressMask><ipAddress min="7" max="15"/><ipv6Address min="2" max="39"/></AddressMask>
        </IPFilterAddress>
        </IPFilterAddressList>
        </IPFilter>
        """;

    private const string OnlineUsers = $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <OnlineUserList version="2.0" xmlns="{Ns}">
        <OnlineUser><id>1</id><name>admin</name><type>admin</type>
        <loginTime>2026-09-29 08:21:34</loginTime>
        <clientAddress><ipAddress>198.51.100.200</ipAddress></clientAddress></OnlineUser>
        </OnlineUserList>
        """;

    private const string Interfaces = $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <NetworkInterfaceList version="2.0" xmlns="{Ns}">
        <NetworkInterface><id>1</id><IPAddress>
        <ipVersion>dual</ipVersion><addressingType>static</addressingType>
        <ipAddress>192.168.1.64</ipAddress><subnetMask>255.255.255.0</subnetMask>
        <DefaultGateway><ipAddress>192.168.1.1</ipAddress></DefaultGateway>
        <PrimaryDNS><ipAddress>8.8.8.8</ipAddress></PrimaryDNS>
        </IPAddress></NetworkInterface>
        </NetworkInterfaceList>
        """;

    private static IpFilterState Deny(bool enabled = true, params string[] addresses) => new()
    {
        Supported = true,
        Enabled = enabled,
        Mode = IpFilterMode.Deny,
        Entries = addresses.Select((a, i) => new IpFilterEntry(i + 1, a)).ToList(),
        Capacity = 32,
        OwnAddresses = ["192.168.1.64", "192.168.1.1"],
        Sessions = [new IpFilterSession("admin", "198.51.100.200", "2026-09-29 08:21:34")],
    };

    // ----- address parsing -----

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData(" 203.0.113.7 ", "203.0.113.7")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    public void ParsesSingleAddresses(string text, string expected) =>
        Assert.Equal(expected, IpFilterAddress.Parse(text)?.ToString());

    [Theory]
    [InlineData("1")]            // IPAddress.TryParse: 0.0.0.1
    [InlineData("10.1")]         // IPAddress.TryParse: 10.0.0.1
    [InlineData("010.1.1.1")]    // octal in some parsers
    [InlineData("203.0.113.0/24")]
    [InlineData("203.0.113.1-203.0.113.9")]
    [InlineData("256.1.1.1")]
    [InlineData("fe80::1%12")]
    [InlineData("host.example.com")]
    [InlineData("")]
    public void RefusesAnythingThatIsNotOneHost(string text) =>
        Assert.Null(IpFilterAddress.Parse(text));

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.177", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("203.0.113.7", false)]
    [InlineData("fd00::1", true)]
    public void KnowsWhatIsLocal(string text, bool local) =>
        Assert.Equal(local, IpFilterAddress.IsLocal(IpFilterAddress.Parse(text)!));

    // ----- the plan -----

    [Fact]
    public void AddsKeepsExistingEntriesAndSkipsDuplicates()
    {
        var plan = IpFilterPlan.For(Deny(true, "198.51.100.1"),
            new IpFilterRequest(Add: ["203.0.113.7", "198.51.100.1", "203.0.113.7"]));

        Assert.True(plan.Allowed);
        Assert.Equal(["198.51.100.1", "203.0.113.7"], plan.After);
        Assert.Single(plan.Adds);
        Assert.Contains(plan.Decisions, d => d.Action == IpFilterAction.AlreadyPresent);
    }

    [Fact]
    public void RemovesAndReportsWhatWasNotListed()
    {
        var plan = IpFilterPlan.For(Deny(true, "198.51.100.1", "198.51.100.2"),
            new IpFilterRequest(Remove: ["198.51.100.1", "203.0.113.9"]));

        Assert.Equal(["198.51.100.2"], plan.After);
        Assert.Contains(plan.Decisions, d => d is { Address: "203.0.113.9", Action: IpFilterAction.NotPresent });
        Assert.True(plan.HasWork);
    }

    [Fact]
    public void OneRefusalRefusesTheWholePlan()
    {
        var plan = IpFilterPlan.For(Deny(), new IpFilterRequest(Add: ["203.0.113.7", "10.1"]));

        Assert.False(plan.Allowed);
        Assert.False(plan.HasWork);
    }

    [Theory]
    [InlineData("192.168.1.64")]   // the recorder itself
    [InlineData("192.168.1.1")]    // its gateway — masquerading routers
    [InlineData("127.0.0.1")]
    [InlineData("224.0.0.5")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    public void NeverBlocksTheRecorderItsGatewayOrNonHosts(string address)
    {
        // Even with every override on.
        var plan = IpFilterPlan.For(Deny(), new IpFilterRequest(Add: [address],
            AllowLocal: true, AllowLoggedIn: true));

        Assert.False(plan.Allowed);
    }

    [Fact]
    public void NeverBlocksAProtectedWorkstationAddress()
    {
        var plan = IpFilterPlan.For(Deny(), new IpFilterRequest(Add: ["198.51.100.50"],
            AllowLocal: true, AllowLoggedIn: true, Protected: ["198.51.100.50"]));

        var refusal = Assert.Single(plan.Refusals);
        Assert.Contains("workstation", refusal.Reason);
    }

    [Fact]
    public void RefusesALiveSessionUnlessOverridden()
    {
        var refused = IpFilterPlan.For(Deny(), new IpFilterRequest(Add: ["198.51.100.200"]));
        Assert.Contains("logged into the recorder", Assert.Single(refused.Refusals).Reason);

        var allowed = IpFilterPlan.For(Deny(), new IpFilterRequest(Add: ["198.51.100.200"],
            AllowLoggedIn: true));
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void RefusesLanAddressesUnlessAllowed()
    {
        Assert.False(IpFilterPlan.For(Deny(), new IpFilterRequest(Add: ["192.168.1.177"])).Allowed);
        Assert.True(IpFilterPlan.For(Deny(), new IpFilterRequest(Add: ["192.168.1.177"],
            AllowLocal: true)).Allowed);
    }

    [Fact]
    public void RefusesAnAllowlistWhole()
    {
        var allow = Deny() with { Mode = IpFilterMode.Allow };

        // Removing from an allowlist is the lockout; even enabling one is refused.
        Assert.NotNull(IpFilterPlan.For(allow, new IpFilterRequest(Add: ["203.0.113.7"])).Blocked);
        Assert.NotNull(IpFilterPlan.For(allow, new IpFilterRequest(Enable: true)).Blocked);
        Assert.NotNull(IpFilterPlan.For(Deny() with { Mode = IpFilterMode.Unknown },
            new IpFilterRequest(Add: ["203.0.113.7"])).Blocked);
    }

    [Fact]
    public void RefusesAMixedList()
    {
        var mixed = Deny() with
        {
            Entries = [new IpFilterEntry(1, "198.51.100.1", IpFilterMode.Allow)],
        };
        Assert.NotNull(IpFilterPlan.For(mixed, new IpFilterRequest(Add: ["203.0.113.7"])).Blocked);
    }

    [Fact]
    public void RefusesByNameTheAddsThatOverflowCapacity()
    {
        var full = Deny(true, Enumerable.Range(1, 31).Select(i => $"198.51.100.{i}").ToArray());

        var plan = IpFilterPlan.For(full, new IpFilterRequest(Add: ["203.0.113.1", "203.0.113.2"]));

        Assert.False(plan.Allowed);
        var refusal = Assert.Single(plan.Refusals);
        Assert.Equal("203.0.113.2", refusal.Address);
        Assert.Contains("holds 32", refusal.Reason);
    }

    [Fact]
    public void RefusesAddsWhenCapacityIsUnknown()
    {
        var plan = IpFilterPlan.For(Deny() with { Capacity = 0 },
            new IpFilterRequest(Add: ["203.0.113.7"]));
        Assert.NotNull(plan.Blocked);
    }

    [Fact]
    public void SaysWhenTheFilterStaysOff()
    {
        var off = IpFilterPlan.For(Deny(enabled: false), new IpFilterRequest(Add: ["203.0.113.7"]));
        Assert.True(off.InertAfter);

        var on = IpFilterPlan.For(Deny(enabled: false),
            new IpFilterRequest(Add: ["203.0.113.7"], Enable: true));
        Assert.False(on.InertAfter);
        Assert.True(on.EnabledAfter);
    }

    [Fact]
    public void RefusesIpv6WhereTheRecorderDeclaresNoField()
    {
        var plan = IpFilterPlan.For(Deny() with { AcceptsIpv6 = false },
            new IpFilterRequest(Add: ["2001:db8::1"]));
        Assert.False(plan.Allowed);
    }

    [Fact]
    public void AReadWithAFailedPartIsNotWritable() =>
        Assert.NotNull(IpFilterPlan.WhyNotWritable(Deny() with
        {
            Failures = [new ConfigNote("ipfilter", "the online-user list", "timeout")],
        }));

    // ----- the Hikvision documents -----

    [Fact]
    public void ParsesTheLiveDocument()
    {
        var state = HikvisionClient.ParseIpFilter(
            XDocument.Parse(FilterDoc(true, "deny", "203.0.113.21", "203.0.113.22")),
            XDocument.Parse(FilterCaps));

        Assert.True(state.Supported);
        Assert.True(state.Enabled);
        Assert.Equal(IpFilterMode.Deny, state.Mode);
        Assert.Equal(32, state.Capacity);
        Assert.True(state.AcceptsIpv6);
        Assert.Equal(["deny", "allow"], state.DeclaredModes);
        Assert.Equal(["203.0.113.21", "203.0.113.22"], state.Entries.Select(e => e.Address));
        Assert.Equal("on, blocklist, 2 of 32", state.Summary);
    }

    [Fact]
    public void ParsesTheSessionsAndTheRecordersOwnAddresses()
    {
        var session = Assert.Single(HikvisionClient.ParseOnlineUsers(XDocument.Parse(OnlineUsers)));
        Assert.Equal(("admin", "198.51.100.200"), (session.User, session.Address));

        // Address and gateway; DNS servers are somebody else's and are not the recorder.
        Assert.Equal(["192.168.1.64", "192.168.1.1"],
            HikvisionClient.ParseOwnAddresses(XDocument.Parse(Interfaces)));
    }

    [Fact]
    public void BuildsTheDocumentInTheFirmwaresOwnShape()
    {
        var doc = XDocument.Parse(FilterDoc(false, "deny", "198.51.100.1", "198.51.100.2"));

        HikvisionClient.BuildIpFilterDocument(doc, ["198.51.100.2", "203.0.113.7", "2001:db8::1"],
            enabled: true, IpFilterMode.Deny);

        var reread = HikvisionClient.ParseIpFilter(doc, null);
        Assert.True(reread.Enabled);
        Assert.Equal(["198.51.100.2", "203.0.113.7", "2001:db8::1"], reread.Entries.Select(e => e.Address));
        Assert.Equal([1, 2, 3], reread.Entries.Select(e => e.Id)); // renumbered: a slot, not an identity
        Assert.Equal(32, reread.Capacity);                           // size attribute kept
        Assert.All(doc.Descendants(XName.Get("IPFilterAddress", Ns)),
            e => Assert.Equal("deny", e.Element(XName.Get("permissionType", Ns))!.Value));
        Assert.Single(doc.Descendants(XName.Get("ipv6Address", Ns)));
    }

    // ----- the client, end to end -----

    private static readonly NvrConnection Conn = new()
    {
        Host = "192.0.2.10",
        Username = "admin",
        Password = "x",
    };

    /// <summary>A recorder whose filter is whatever the last PUT made it.</summary>
    private sealed class FakeRecorder
    {
        public string Filter = FilterDoc(false, "deny");
        public bool IgnorePuts;
        public int Puts;

        public MockHttpHandler Handler() => new((req, body) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Put && path == "/ISAPI/System/Network/ipFilter")
            {
                Puts++;
                if (!IgnorePuts)
                    Filter = body;
                return MockHttpHandler.Xml(
                    $"<ResponseStatus xmlns=\"{Ns}\"><statusCode>1</statusCode></ResponseStatus>");
            }
            return path switch
            {
                "/ISAPI/System/Network/capabilities" => MockHttpHandler.Xml(
                    $"<NetworkCap xmlns=\"{Ns}\"><isSupportIPFilter>true</isSupportIPFilter></NetworkCap>"),
                "/ISAPI/System/Network/ipFilter" => MockHttpHandler.Xml(Filter),
                "/ISAPI/System/Network/ipFilter/capabilities" => MockHttpHandler.Xml(FilterCaps),
                "/ISAPI/Security/onlineUser" => MockHttpHandler.Xml(OnlineUsers),
                "/ISAPI/System/Network/interfaces" => MockHttpHandler.Xml(Interfaces),
                _ => MockHttpHandler.Xml("<x/>", HttpStatusCode.NotFound),
            };
        });
    }

    [Fact]
    public async Task WritesReadsBackAndReportsWhatTheRecorderHolds()
    {
        var recorder = new FakeRecorder();
        using var client = new HikvisionClient(Conn, recorder.Handler());

        var state = await client.GetIpFilterAsync();
        var plan = IpFilterPlan.For(state, new IpFilterRequest(Add: ["203.0.113.7"], Enable: true));
        var change = await client.ApplyIpFilterAsync(plan);

        Assert.False(change.Rejected);
        Assert.Equal(["203.0.113.7"], change.Added);
        Assert.True(change.After.Enabled);
    }

    [Fact]
    public async Task ARecorderThatKeepsItsOwnListIsReportedAsRejected()
    {
        var recorder = new FakeRecorder { IgnorePuts = true };
        using var client = new HikvisionClient(Conn, recorder.Handler());

        var plan = IpFilterPlan.For(await client.GetIpFilterAsync(),
            new IpFilterRequest(Add: ["203.0.113.7"]));
        var change = await client.ApplyIpFilterAsync(plan);

        Assert.True(change.Rejected);
        Assert.Contains("203.0.113.7", change.Note);
    }

    [Fact]
    public async Task RefusesWhenTheListMovedSinceTheRead()
    {
        var recorder = new FakeRecorder();
        using var client = new HikvisionClient(Conn, recorder.Handler());

        var plan = IpFilterPlan.For(await client.GetIpFilterAsync(),
            new IpFilterRequest(Add: ["203.0.113.7"]));
        recorder.Filter = FilterDoc(false, "deny", "198.51.100.9"); // edited from the web UI

        await Assert.ThrowsAsync<NvrException>(() => client.ApplyIpFilterAsync(plan));
        Assert.Equal(0, recorder.Puts);
    }

    [Fact]
    public async Task RefusesAPlanNotMadeFromItsOwnLatestRead()
    {
        var recorder = new FakeRecorder();
        using var client = new HikvisionClient(Conn, recorder.Handler());
        await client.GetIpFilterAsync();

        var foreign = IpFilterPlan.For(Deny(enabled: false), new IpFilterRequest(Add: ["203.0.113.7"]));

        await Assert.ThrowsAsync<NvrException>(() => client.ApplyIpFilterAsync(foreign));
        Assert.Equal(0, recorder.Puts);
    }

    [Fact]
    public async Task AFirmwareThatSaysNoIsNotSupportedNotFailed()
    {
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.AbsolutePath == "/ISAPI/System/Network/capabilities"
                ? MockHttpHandler.Xml(
                    $"<NetworkCap xmlns=\"{Ns}\"><isSupportIPFilter>false</isSupportIPFilter></NetworkCap>")
                : MockHttpHandler.Xml("<x/>", HttpStatusCode.Forbidden));
        using var client = new HikvisionClient(Conn, handler);

        var state = await client.GetIpFilterAsync();

        Assert.False(state.Supported);
        Assert.Empty(state.Failures);
        Assert.DoesNotContain(handler.Requests, r => r.Request.RequestUri!.AbsolutePath.EndsWith("/ipFilter"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task NoFlagAndNoDocumentIsNotSupported(HttpStatusCode status)
    {
        // What all nine filterless firmwares in the fleet do: the flag is absent, the
        // document 403s or 404s.
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.AbsolutePath == "/ISAPI/System/Network/capabilities"
                ? MockHttpHandler.Xml($"<NetworkCap xmlns=\"{Ns}\"><isSupportNTP>true</isSupportNTP></NetworkCap>")
                : MockHttpHandler.Xml("<x/>", status));
        using var client = new HikvisionClient(Conn, handler);

        Assert.False((await client.GetIpFilterAsync()).Supported);
    }

    [Fact]
    public async Task AFlagThatSaysYesAndNoDocumentIsAFailedRead()
    {
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.AbsolutePath == "/ISAPI/System/Network/capabilities"
                ? MockHttpHandler.Xml(
                    $"<NetworkCap xmlns=\"{Ns}\"><isSupportIPFilter>true</isSupportIPFilter></NetworkCap>")
                : MockHttpHandler.Xml("<x/>", HttpStatusCode.InternalServerError));
        using var client = new HikvisionClient(Conn, handler);

        await Assert.ThrowsAsync<NvrException>(() => client.GetIpFilterAsync());
    }

    // ----- the fleet sweep -----

    [Fact]
    public void TheAuditCountsAndGroupsAddressesAcrossRecorders()
    {
        var audit = IpFilterAudit.Build([
            new IpFilterAuditRow { DeviceName = "A", State = Deny(true, "203.0.113.7", "198.51.100.1") },
            new IpFilterAuditRow { DeviceName = "B", State = Deny(false, "203.0.113.7") },
            new IpFilterAuditRow { DeviceName = "C", State = IpFilterState.NotSupported() },
            IpFilterAuditRow.Unimplemented("D", "Dahua"),
            IpFilterAuditRow.Failed("E", "timeout"),
        ]);

        Assert.Equal(("203.0.113.7", 2), (audit.Addresses[0].Address, audit.Addresses[0].Devices.Count));
        Assert.True(audit.IsPartial);
        Assert.Equal("5 recorder(s): 2 have an IP filter, 1 using it, 2 distinct address(es) blocked, " +
            "1 not implemented, 1 could not be read.", audit.Summary);
        Assert.Contains("OFF", audit.Rows[1].Verdict);
    }
}
