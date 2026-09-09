using System.Net;
using DVRTool.Core;
using DVRTool.Vendors.Dahua;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// Dahua's own configuration, over fixtures transcribed from the 2026-09-09 read of a
/// DH-NVR608H-128-4KS3/I (addresses and MACs redacted). What these pin is mostly what this
/// vendor does <em>not</em> say: no offset on its clock, no ranges anywhere, and only one
/// readable port — with a 403 that is indistinguishable from a permission denial, which is why
/// a config name is never guessed.
/// </summary>
public class DahuaDeviceConfigTests
{
    private static readonly NvrConnection Conn = new()
    {
        Host = "192.0.2.20",
        Username = "admin",
        Password = "p@ss",
    };

    private const string CurrentTime = "result=2026-09-09 06:24:26\r\n";

    /// <summary>The zone lives here — <b>not</b> in <c>Locales</c> — as an index plus a label.</summary>
    private const string NtpConfig = """
        table.NTP.Address=time.windows.com
        table.NTP.Enable=true
        table.NTP.Port=123
        table.NTP.ServerList[0].Address=time.windows.com
        table.NTP.ServerList[0].Enable=true
        table.NTP.ServerList[0].Port=123
        table.NTP.ServerList[1].Address=pool.ntp.org
        table.NTP.ServerList[1].Enable=false
        table.NTP.ServerList[1].Port=123
        table.NTP.ServerList[2].Address=
        table.NTP.TimeZone=25
        table.NTP.TimeZoneDesc=Easterntime
        table.NTP.UpdatePeriod=60
        """;

    /// <summary>DST only, and as absolute dates with a year rather than a recurring rule.</summary>
    private const string LocalesConfig = """
        table.Locales.DSTEnable=false
        table.Locales.DSTStart.Year=2026
        table.Locales.DSTStart.Month=3
        table.Locales.DSTStart.Week=2
        table.Locales.DSTStart.Day=0
        table.Locales.DSTStart.Hour=2
        table.Locales.DSTEnd.Year=2026
        table.Locales.DSTEnd.Month=11
        table.Locales.DSTEnd.Week=1
        table.Locales.DSTEnd.Day=0
        table.Locales.DSTEnd.Hour=2
        """;

    /// <summary>Six interfaces, four of them unconfigured bonds, and a named default.</summary>
    private const string NetworkConfig = """
        table.Network.DefaultInterface=eth0
        table.Network.Domain=
        table.Network.Hostname=NVR608H
        table.Network.eth0.IPAddress=192.0.2.20
        table.Network.eth0.SubnetMask=255.255.255.0
        table.Network.eth0.DefaultGateway=192.0.2.1
        table.Network.eth0.DhcpEnable=false
        table.Network.eth0.DnsAutoGet=false
        table.Network.eth0.DnsServers[0]=192.0.2.1
        table.Network.eth0.DnsServers[1]=0.0.0.0
        table.Network.eth0.MTU=1500
        table.Network.eth0.PhysicalAddress=00:00:5e:00:53:20
        table.Network.eth0.Type=
        table.Network.eth1.IPAddress=0.0.0.0
        table.Network.eth1.SubnetMask=0.0.0.0
        table.Network.eth1.DefaultGateway=0.0.0.0
        table.Network.eth1.DhcpEnable=true
        table.Network.eth1.MTU=1500
        table.Network.eth1.PhysicalAddress=00:00:5e:00:53:21
        table.Network.bond0.IPAddress=0.0.0.0
        table.Network.bond0.DhcpEnable=false
        table.Network.bond1.IPAddress=0.0.0.0
        table.Network.bond1.DhcpEnable=false
        table.Network.bond2.IPAddress=0.0.0.0
        table.Network.bond2.DhcpEnable=false
        table.Network.bond3.IPAddress=0.0.0.0
        table.Network.bond3.DhcpEnable=false
        """;

    private const string RtspConfig = """
        table.RTSP.Enable=true
        table.RTSP.Port=554
        table.RTSP.RTP.StartPort=20000
        table.RTSP.RTP.EndPort=40000
        """;

    private const string GeneralConfig = """
        table.General.MachineName=Site B NVR
        table.General.LockLoginEnable=true
        table.General.LockLoginTimes=5
        table.General.LoginFailLockTime=1800
        """;

    /// <summary>
    /// The body every unknown config name answers with — the same body a genuine permission
    /// denial returns, which is the whole reason names are never guessed.
    /// </summary>
    private const string AuthorityFailure = """
        {"error":{"code":403,"message":"Authority:check failure."},"result":false}
        """;

    private static MockHttpHandler Handler(
        Dictionary<string, string>? overrides = null, List<string>? writes = null)
    {
        var configs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NTP"] = NtpConfig,
            ["Locales"] = LocalesConfig,
            ["Network"] = NetworkConfig,
            ["RTSP"] = RtspConfig,
            ["General"] = GeneralConfig,
        };
        foreach (var (name, body) in overrides ?? [])
            configs[name] = body;

        return new MockHttpHandler((req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string query = req.RequestUri!.Query;

            if (query.Contains("action=getCurrentTime"))
                return MockHttpHandler.Text(CurrentTime);
            if (query.Contains("action=setCurrentTime") || query.Contains("action=setConfig"))
            {
                writes?.Add(req.RequestUri!.PathAndQuery);
                return MockHttpHandler.Text("OK\r\n");
            }
            if (path.EndsWith("netApp.cgi", StringComparison.Ordinal))
                return MockHttpHandler.Text("""
                    netInterface[0].Name=eth0
                    netInterface[0].Speed=1000
                    netInterface[0].Status=up
                    netInterface[1].Name=eth1
                    netInterface[1].Status=down
                    """);

            const string marker = "name=";
            int at = query.IndexOf(marker, StringComparison.Ordinal);
            string name = at < 0 ? "" : query[(at + marker.Length)..].Split('&')[0];
            return configs.TryGetValue(name, out string? body)
                ? MockHttpHandler.Text(body)
                : MockHttpHandler.Text(AuthorityFailure, HttpStatusCode.Forbidden);
        });
    }

    [Fact]
    public async Task GetClock_HasNoOffsetAtAll()
    {
        var handler = Handler();
        using var client = new DahuaClient(Conn, handler);

        var clock = await client.GetClockAsync();

        Assert.Single(handler.Requests);
        Assert.Equal(new DateTime(2026, 9, 9, 6, 24, 26), clock.WallClock);
        Assert.Equal(DateTimeKind.Unspecified, clock.WallClock.Kind);

        // Null rather than TimeSpan.Zero: this vendor states nothing, and "it claims UTC"
        // would be an invention.
        Assert.Null(clock.DeclaredOffset);
        Assert.Null(clock.DeclaredOffsetText);
    }

    [Fact]
    public async Task TheZoneComesFromNtp_AndDstFromLocales()
    {
        using var client = new DahuaClient(Conn, Handler());

        var config = await client.GetConfigurationAsync();

        // 25 / Easterntime with DST off is the exact pairing that had this recorder stamping
        // every recording an hour early.
        Assert.Equal("25 (Easterntime)", config.Clock.VendorZoneLabel);
        Assert.False(config.Clock.DstEnabled);
        Assert.True(config.NtpEnabled);

        // The DST window is reported as the device's own fields, verbatim: it is stated as
        // absolute dates with a year rather than a recurring rule, and the live 608H fills
        // Week and Day in a combination its documentation does not explain — so nothing here
        // interprets them.
        var dst = Assert.Single(config.Notes, n => n.Group == "DST");
        Assert.Contains("Year=2026 Month=3 Week=2 Day=0 Hour=2", dst.Value);
        Assert.Contains("Year=2026 Month=11 Week=1 Day=0 Hour=2", dst.Value);
    }

    [Fact]
    public async Task NtpServersAreDeduplicatedAndTheIntervalIsMinutes()
    {
        using var client = new DahuaClient(Conn, Handler());

        var config = await client.GetConfigurationAsync();

        // The primary is repeated as the first list entry; one row per address.
        Assert.Equal(["time.windows.com", "pool.ntp.org"],
            config.NtpServers!.Select(s => s.Address));
        Assert.Equal(TimeSpan.FromMinutes(60), config.NtpInterval);
    }

    [Fact]
    public async Task OnlyRtspHasAPort_AndItsBoundIsOurs()
    {
        using var client = new DahuaClient(Conn, Handler());

        var config = await client.GetConfigurationAsync();

        // Ports is in scope and the list holds one entry: the web port is not reachable by
        // name on this vendor at all.
        Assert.True(config.Scope.Ports);
        var port = Assert.Single(config.Ports);
        Assert.Equal(ServiceKind.Rtsp, port.Kind);
        Assert.Equal(554, port.Port);
        Assert.True(port.Enabled);
        Assert.Equal("20000", port.Extras["RTP.StartPort"]);

        // The device declares no range, so the front end must be able to tell that the bound
        // it shows is DVRTool's own.
        Assert.Null(port.Range);
        var (range, fromDevice) = port.EffectiveRange;
        Assert.False(fromDevice);
        Assert.Equal(ServicePortRange.Fallback, range);
    }

    [Fact]
    public async Task TheDefaultInterfaceIsNamed_NotAFixedKey()
    {
        using var client = new DahuaClient(Conn, Handler());

        var config = await client.GetConfigurationAsync();

        Assert.Equal(6, config.Interfaces!.Count);
        var lan = config.DefaultInterface!;
        Assert.Equal("eth0", lan.Name);
        Assert.True(lan.IsDefault);
        Assert.Equal("192.0.2.20", lan.IpAddress);
        Assert.Equal("192.0.2.1", lan.Gateway);
        Assert.Equal(AddressingType.Static, lan.AddressingType);

        // 0.0.0.0 is not a DNS server.
        Assert.Equal(["192.0.2.1"], lan.Dns);

        // Link state is enrichment off netApp.cgi.
        Assert.Equal(1000, lan.LinkSpeedMbps);
        Assert.True(lan.LinkUp);
        Assert.False(config.Interfaces!.Single(i => i.Name == "eth1").LinkUp);

        // Nothing here is declared by the device, and the front end must not present our
        // choices as the recorder's.
        Assert.Empty(lan.AddressingOptions);
        Assert.Null(lan.MtuRange);
    }

    [Fact]
    public async Task TheLockoutPolicyIsReadableRatherThanFolklore()
    {
        using var client = new DahuaClient(Conn, Handler());

        var config = await client.GetConfigurationAsync();

        Assert.Equal("Site B NVR", config.DeviceName);
        Assert.Contains(config.Notes, n => n.Group == "Login lockout" && n.Value == "1800 s");
        Assert.Contains(config.Notes, n =>
            n.Group == "Login lockout" && n.Value == "5 failed logins");
    }

    [Fact]
    public async Task A403IsReportedAsAFailedPart_NotRetriedUnderAnotherName()
    {
        // "Authority:check failure." is a permission denial AND an unknown config name at
        // once. It is surfaced as a failed part, and no other name is tried for the same
        // thing — a wrong guess is indistinguishable from a real denial, and the box locks out
        // after five.
        using var client = new DahuaClient(Conn, new MockHttpHandler((req, _) =>
        {
            string query = req.RequestUri!.Query;
            if (query.Contains("action=getCurrentTime"))
                return MockHttpHandler.Text(CurrentTime);
            if (query.Contains("name=RTSP"))
                return MockHttpHandler.Text(AuthorityFailure, HttpStatusCode.Forbidden);
            if (query.Contains("name=NTP"))
                return MockHttpHandler.Text(NtpConfig);
            return MockHttpHandler.Text(AuthorityFailure, HttpStatusCode.Forbidden);
        }));

        var config = await client.GetConfigurationAsync();

        Assert.Empty(config.Ports);
        Assert.Contains(config.Failures, f => f.Label == "the RTSP port");
        Assert.Contains("Authority:check failure",
            config.Failures.Single(f => f.Label == "the RTSP port").Value);

        // The clock still came back: a part that fails costs that part and nothing else.
        Assert.Equal(new DateTime(2026, 9, 9, 6, 24, 26), config.Clock.WallClock);
    }

    [Fact]
    public async Task NoConfigNameIsEverGuessed()
    {
        var handler = Handler();
        using var client = new DahuaClient(Conn, handler);

        await client.GetConfigurationAsync();

        // Every name asked for is one the discovery pass observed answering. A speculative
        // HTTP/HTTPS/ClientPort probe would look like a permissions problem in a support call.
        var names = handler.Requests
            .Select(r => r.Request.RequestUri!.Query)
            .Where(q => q.Contains("name="))
            .Select(q => q[(q.IndexOf("name=", StringComparison.Ordinal) + 5)..].Split('&')[0])
            .Distinct()
            .ToList();
        Assert.Equal(
            ["NTP", "Locales", "RTSP", "Network", "General", "DDNS", "UPnP", "T2UServer"],
            names);
    }

    [Fact]
    public async Task AWriteBeforeAnyReadIsRefused()
    {
        using var client = new DahuaClient(Conn, Handler());

        var ex = await Assert.ThrowsAsync<NvrException>(() =>
            client.SetNtpAsync(new NtpSettings(Address: "time.nist.gov")));

        Assert.Contains("read the configuration before writing it", ex.Message);
    }

    [Fact]
    public async Task ANtpWriteSetsOnlyWhatWasAsked()
    {
        var writes = new List<string>();
        using var client = new DahuaClient(Conn, Handler(writes: writes));

        await client.GetConfigurationAsync();
        await client.SetNtpAsync(new NtpSettings(Address: "time.nist.gov",
            Interval: TimeSpan.FromMinutes(15)));

        var write = Assert.Single(writes);
        Assert.Contains("NTP.Address=time.nist.gov", write);
        Assert.Contains("NTP.UpdatePeriod=15", write);
        Assert.DoesNotContain("NTP.Enable", write);
        Assert.DoesNotContain("TimeZone", write);
    }

    [Fact]
    public async Task ADstWriteGoesToLocales()
    {
        var writes = new List<string>();
        using var client = new DahuaClient(Conn, Handler(writes: writes));

        await client.GetConfigurationAsync();
        await client.SetTimeAsync(new TimeSettings(DstEnabled: true));

        Assert.Contains("Locales.DSTEnable=true", Assert.Single(writes));
    }

    [Fact]
    public async Task AZoneWrittenAsTextIsRefused_BecauseTheZoneIsAnIndex()
    {
        var writes = new List<string>();
        using var client = new DahuaClient(Conn, Handler(writes: writes));

        await client.GetConfigurationAsync();
        var change = await client.SetTimeAsync(
            new TimeSettings(VendorZoneLabel: "America/New_York"));

        Assert.False(change.Changed);
        Assert.Contains("vendor index", change.Note);
        Assert.Empty(writes);
    }

    [Fact]
    public async Task SyncNowIsRefusedWhileNtpIsOn_AndSaysWhatToFixInstead()
    {
        using var client = new DahuaClient(Conn, Handler());

        await client.GetConfigurationAsync();
        var change = await client.SyncTimeNowAsync();

        Assert.False(change.Changed);
        Assert.Contains("overwritten at the next sync", change.Note);
        Assert.Contains("Easterntime", change.Note);
        Assert.Contains("DST off", change.Note);
    }

    [Fact]
    public async Task SyncNowStampsTheClockWhenNothingElseIsKeepingIt()
    {
        var writes = new List<string>();
        using var client = new DahuaClient(Conn,
            Handler(new() { ["NTP"] = NtpConfig.Replace("Enable=true", "Enable=false") },
                writes));

        await client.GetConfigurationAsync();
        await client.SyncTimeNowAsync();

        Assert.Contains("action=setCurrentTime", Assert.Single(writes));
    }

    [Fact]
    public async Task AWriteIsRefusedWhenTheConfigMovedUnderneath()
    {
        string ntp = NtpConfig;
        using var client = new DahuaClient(Conn, new MockHttpHandler((req, _) =>
        {
            string query = req.RequestUri!.Query;
            if (query.Contains("action=getCurrentTime"))
                return MockHttpHandler.Text(CurrentTime);
            if (query.Contains("action=setConfig"))
                return MockHttpHandler.Text("OK");
            if (query.Contains("name=NTP"))
                return MockHttpHandler.Text(ntp);
            return MockHttpHandler.Text(AuthorityFailure, HttpStatusCode.Forbidden);
        }));

        await client.GetConfigurationAsync();
        ntp = NtpConfig.Replace("time.windows.com", "pool.ntp.org");

        var ex = await Assert.ThrowsAsync<NvrException>(() =>
            client.SetNtpAsync(new NtpSettings(Port: 1123)));

        Assert.Contains("changed on the device since it was read", ex.Message);
    }
}
