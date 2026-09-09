using System.Net;
using DVRTool.Core;
using DVRTool.Vendors.Hikvision;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// Hikvision's own configuration, over fixtures transcribed from the discovery captures of
/// 2026-09-09 (addresses, serials and MACs redacted). The traps these pin are the ones that
/// fail silently: two spellings of the default attribute in one document, a plain-text scalar
/// where XML is expected, capabilities at a different depth from values, and an NTP document
/// whose M-series-only fields a synthesized PUT would drop.
/// </summary>
public class HikvisionDeviceConfigTests
{
    private static readonly NvrConnection Conn = new()
    {
        Host = "192.0.2.10",
        Username = "admin",
        Password = "p@ss:word",
    };

    private const string Ns = "http://www.hikvision.com/ver20/XMLSchema";

    private const string TimeXml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Time version="2.0" xmlns="{Ns}">
        <timeMode opt="NTP,manual,platform">NTP</timeMode>
        <localTime>2026-09-09T07:24:29-05:00</localTime>
        <timeZone>CST+5:00:00DST01:00:00,M3.2.0/02:00:00,M11.1.0/02:00:00</timeZone>
        <timeType opt="local,UTC">local</timeType>
        </Time>
        """;

    /// <summary>The I-series NTP document: no portType, no customPortNo.</summary>
    private const string NtpListXml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <NTPServerList version="2.0" xmlns="{Ns}">
        <NTPServer>
        <id>1</id>
        <addressingFormatType>hostname</addressingFormatType>
        <hostName>time.windows.com</hostName>
        <portNo>123</portNo>
        <synchronizeInterval>60</synchronizeInterval>
        </NTPServer>
        </NTPServerList>
        """;

    /// <summary>
    /// The M-series single-server document, which carries three fields the I-series one does
    /// not. A hand-built minimal PUT drops them on every save.
    /// </summary>
    private const string NtpServerXmlMSeries = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <NTPServer version="2.0" xmlns="{Ns}">
        <id>1</id>
        <addressingFormatType>hostname</addressingFormatType>
        <hostName>time.windows.com</hostName>
        <portNo>123</portNo>
        <portType>default</portType>
        <customPortNo>123</customPortNo>
        <synchronizeInterval>60</synchronizeInterval>
        <hostNameExampleList><hostNameExample>time.nist.gov</hostNameExample></hostNameExampleList>
        </NTPServer>
        """;

    /// <summary>All seven service ports, one document. HTTPS ships disabled.</summary>
    private const string PortsXml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <AdminAccessProtocolList version="2.0" xmlns="{Ns}">
        <AdminAccessProtocol><id>1</id><enabled>true</enabled><protocol>HTTP</protocol><portNo>80</portNo></AdminAccessProtocol>
        <AdminAccessProtocol><id>2</id><enabled>true</enabled><protocol>RTSP</protocol><portNo>554</portNo></AdminAccessProtocol>
        <AdminAccessProtocol><id>3</id><enabled>false</enabled><protocol>HTTPS</protocol><portNo>443</portNo>
        <redirectToHttps>false</redirectToHttps><TLS1_1Enable>false</TLS1_1Enable><TLS1_2Enable>true</TLS1_2Enable>
        </AdminAccessProtocol>
        <AdminAccessProtocol><id>4</id><enabled>true</enabled><protocol>DEV_MANAGE</protocol><portNo>8000</portNo></AdminAccessProtocol>
        <AdminAccessProtocol><id>5</id><enabled>true</enabled><protocol>WebSocket</protocol><portNo>7681</portNo></AdminAccessProtocol>
        <AdminAccessProtocol><id>6</id><enabled>true</enabled><protocol>IOT</protocol><portNo>30999</portNo></AdminAccessProtocol>
        <AdminAccessProtocol><id>7</id><enabled>true</enabled><protocol>SDK_OVER_TLS</protocol><portNo>8443</portNo>
        <streamOverTls>false</streamOverTls>
        </AdminAccessProtocol>
        </AdminAccessProtocolList>
        """;

    /// <summary>
    /// The capabilities sibling — and the trap: ids 1/2/5/6 spell the default <c>def=</c>,
    /// ids 4/7 spell it <c>default=</c>, in the same document, on both firmwares.
    /// </summary>
    private const string PortCapsXml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <AdminAccessProtocolList version="2.0" xmlns="{Ns}">
        <AdminAccessProtocol><id>1</id><protocol>HTTP</protocol><portNo min="1" max="65535" def="80">80</portNo></AdminAccessProtocol>
        <AdminAccessProtocol><id>2</id><protocol>RTSP</protocol><portNo min="1024" max="65535" def="554">554</portNo></AdminAccessProtocol>
        <AdminAccessProtocol><id>3</id><protocol>HTTPS</protocol><portNo min="1" max="65535" def="443">443</portNo></AdminAccessProtocol>
        <AdminAccessProtocol><id>4</id><protocol>DEV_MANAGE</protocol><portNo min="2000" max="65535" default="8000">8000</portNo></AdminAccessProtocol>
        <AdminAccessProtocol><id>5</id><protocol>WebSocket</protocol><portNo min="7681" max="7681" def="7681">7681</portNo></AdminAccessProtocol>
        <AdminAccessProtocol><id>6</id><protocol>IOT</protocol><portNo min="1024" max="65535" def="30999">30999</portNo></AdminAccessProtocol>
        <AdminAccessProtocol><id>7</id><protocol>SDK_OVER_TLS</protocol><portNo min="2000" max="65535" default="8443">8443</portNo></AdminAccessProtocol>
        </AdminAccessProtocolList>
        """;

    private const string InterfacesXml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <NetworkInterfaceList version="2.0" xmlns="{Ns}">
        <NetworkInterface>
        <id>1</id>
        <IPAddress>
        <ipVersion>v4</ipVersion>
        <addressingType>static</addressingType>
        <ipAddress>192.0.2.10</ipAddress>
        <subnetMask>255.255.255.0</subnetMask>
        <DefaultGateway><ipAddress>192.0.2.1</ipAddress></DefaultGateway>
        <PrimaryDNS><ipAddress>192.0.2.1</ipAddress></PrimaryDNS>
        <SecondaryDNS><ipAddress>198.51.100.53</ipAddress></SecondaryDNS>
        <DNSEnable>false</DNSEnable>
        </IPAddress>
        <Link>
        <MACAddress>00:00:5e:00:53:00</MACAddress>
        <autoNegotiation>true</autoNegotiation>
        <speed>1000</speed>
        <duplex>full</duplex>
        <MTU>1500</MTU>
        </Link>
        </NetworkInterface>
        </NetworkInterfaceList>
        """;

    /// <summary>
    /// The only document carrying the <c>opt=</c> attributes and the MTU bounds — the
    /// per-interface capabilities sub-nodes are 403 <c>notSupport</c>.
    /// </summary>
    private const string InterfaceCapsXml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <NetworkInterface version="2.0" xmlns="{Ns}">
        <IPAddress>
        <ipVersion opt="v4,v6,dual">v4</ipVersion>
        <addressingType opt="apipa,dynamic,static">static</addressingType>
        <DNSEnable opt="true,false">false</DNSEnable>
        </IPAddress>
        <Link>
        <MTU min="500" max="1500">1500</MTU>
        <speed opt="10,100,1000">1000</speed>
        <duplex opt="full,half">full</duplex>
        </Link>
        </NetworkInterface>
        """;

    private const string DeviceInfoXml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <DeviceInfo version="2.0" xmlns="{Ns}">
        <deviceName>the lab recorder</deviceName>
        <model>DS-7716NI-I4/16P</model>
        <serialNumber>DS-7716NI-I4-16P0000000000000000000000</serialNumber>
        <firmwareVersion>V4.61.030</firmwareVersion>
        </DeviceInfo>
        """;

    private const string OkStatus = $"""
        <ResponseStatus version="2.0" xmlns="{Ns}">
        <statusCode>1</statusCode><statusString>OK</statusString>
        </ResponseStatus>
        """;

    /// <summary>
    /// A router over the observed endpoint set. Anything not in it answers 403
    /// <c>notSupport</c> — which is what the real firmware does for every path the discovery
    /// pass found absent, and what the client must never depend on.
    /// </summary>
    private static MockHttpHandler Handler(Dictionary<string, string>? overrides = null,
        List<(string Path, string Body)>? writes = null)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/ISAPI/System/time"] = TimeXml,
            ["/ISAPI/System/time/localTime"] = "2026-09-09T07:24:29-05:00",
            ["/ISAPI/System/time/ntpServers"] = NtpListXml,
            ["/ISAPI/System/time/ntpServers/1"] = NtpServerXmlMSeries,
            ["/ISAPI/Security/adminAccesses"] = PortsXml,
            ["/ISAPI/Security/adminAccesses/capabilities"] = PortCapsXml,
            ["/ISAPI/System/Network/interfaces"] = InterfacesXml,
            ["/ISAPI/System/Network/interfaces/capabilities"] = InterfaceCapsXml,
            ["/ISAPI/System/deviceInfo"] = DeviceInfoXml,
        };
        foreach (var (path, body) in overrides ?? [])
            bodies[path] = body;

        return new MockHttpHandler((req, body) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Put)
            {
                writes?.Add((path, body));
                return MockHttpHandler.Xml(OkStatus);
            }
            if (!bodies.TryGetValue(path, out string? content))
                return MockHttpHandler.Xml($"""
                    <ResponseStatus version="2.0" xmlns="{Ns}">
                    <statusCode>4</statusCode><statusString>Invalid Operation</statusString>
                    <subStatusCode>notSupport</subStatusCode>
                    </ResponseStatus>
                    """, HttpStatusCode.Forbidden);
            return path == "/ISAPI/System/time/localTime"
                ? MockHttpHandler.Text(content)
                : MockHttpHandler.Xml(content);
        });
    }

    [Fact]
    public async Task GetClock_ReadsThePlainTextScalar_InOneRequest()
    {
        var handler = Handler();
        using var client = new HikvisionClient(Conn, handler);

        var clock = await client.GetClockAsync();

        // One GET, and it is the scalar — an XML-only reader crashes on this response.
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/ISAPI/System/time/localTime", request.Request.RequestUri!.AbsolutePath);

        Assert.Equal(new DateTime(2026, 9, 9, 7, 24, 29), clock.WallClock);
        Assert.Equal(DateTimeKind.Unspecified, clock.WallClock.Kind);

        // The offset is carried as a claim and nothing more: this firmware reported −05:00
        // while standing in −04:00.
        Assert.Equal(TimeSpan.FromHours(-5), clock.DeclaredOffset);
        Assert.Equal("-05:00", clock.DeclaredOffsetText);
    }

    [Fact]
    public async Task GetClock_AlsoAcceptsFirmwareThatAnswersXmlThere()
    {
        var handler = Handler(new()
        {
            ["/ISAPI/System/time/localTime"] =
                $"""<localTime xmlns="{Ns}">2026-09-09T07:24:29-05:00</localTime>""",
        });
        using var client = new HikvisionClient(Conn, handler);

        // The router still serves it as text/plain; XDocument.Parse decides, not the header.
        var clock = await client.GetClockAsync();

        Assert.Equal(new DateTime(2026, 9, 9, 7, 24, 29), clock.WallClock);
    }

    [Fact]
    public async Task GetTimeSource_IsOneGet_AndReadsTimeMode()
    {
        var handler = Handler();
        using var client = new HikvisionClient(Conn, handler);

        var source = await client.GetTimeSourceAsync();

        Assert.Single(handler.Requests);
        Assert.True(source.NtpEnabled);
        Assert.Equal("timeMode=NTP", source.Detail);
    }

    [Fact]
    public async Task ManualTimeMode_MeansNoTimeSource()
    {
        var handler = Handler(new()
        {
            ["/ISAPI/System/time"] = TimeXml.Replace(">NTP<", ">manual<"),
        });
        using var client = new HikvisionClient(Conn, handler);

        Assert.False((await client.GetTimeSourceAsync()).NtpEnabled);
    }

    [Fact]
    public async Task Ports_AllSeven_BothDefaultSpellingsSurvive()
    {
        using var client = new HikvisionClient(Conn, Handler());

        var config = await client.GetConfigurationAsync();

        Assert.Equal(7, config.Ports.Count);
        // Every port's default survives, whichever way its row spelled the attribute: ids
        // 4 and 7 use default=, the rest use def=, and losing the SDK ports' defaults is
        // exactly the silent failure this asserts against.
        Assert.All(config.Ports, p => Assert.NotNull(p.Range!.Default));
        Assert.Equal(8000, config.PortOf(ServiceKind.Sdk)!.Range!.Default);
        Assert.Equal(8443, config.PortOf(ServiceKind.SdkOverTls)!.Range!.Default);
        Assert.Equal(554, config.PortOf(ServiceKind.Rtsp)!.Range!.Default);

        // DEV_MANAGE is the SDK port DVRTool records per device — mapping it is what makes
        // the record-versus-device check possible — and the protocol string is kept verbatim.
        var sdk = config.PortOf(ServiceKind.Sdk)!;
        Assert.Equal("DEV_MANAGE", sdk.Protocol);
        Assert.Equal(8000, sdk.Port);
        Assert.Equal(new ValueRange(2000, 65535, 8000), sdk.Range);

        // HTTPS ships disabled, and its per-port oddments ride as extras rather than as
        // fields on every port.
        var https = config.PortOf(ServiceKind.Https)!;
        Assert.False(https.Enabled);
        Assert.Equal("true", https.Extras["TLS1_2Enable"]);
        Assert.Equal("false", https.Extras["redirectToHttps"]);
        Assert.Equal("false", config.PortOf(ServiceKind.SdkOverTls)!.Extras["streamOverTls"]);

        // A device-declared range is reported as the device's.
        Assert.True(config.PortOf(ServiceKind.Http)!.EffectiveRange.FromDevice);
    }

    [Fact]
    public async Task Interfaces_RangesComeFromTheListCapabilities()
    {
        using var client = new HikvisionClient(Conn, Handler());

        var config = await client.GetConfigurationAsync();

        var nic = Assert.Single(config.Interfaces!);
        Assert.True(nic.IsDefault);
        Assert.Equal(AddressingType.Static, nic.AddressingType);
        Assert.Equal("192.0.2.10", nic.IpAddress);
        Assert.Equal("255.255.255.0", nic.SubnetMask);
        Assert.Equal("192.0.2.1", nic.Gateway);
        Assert.Equal(["192.0.2.1", "198.51.100.53"], nic.Dns);
        Assert.False(nic.DnsAuto);
        Assert.Equal(1500, nic.Mtu);
        Assert.Equal(1000, nic.LinkSpeedMbps);
        Assert.Equal("00:00:5e:00:53:00", nic.MacAddress);

        // Values and bounds come from different URLs at different depths.
        Assert.Equal(new ValueRange(500, 1500), nic.MtuRange);
        Assert.Equal(["apipa", "dynamic", "static"], nic.AddressingOptions);
    }

    [Fact]
    public async Task WholeConfiguration_ReadsClockNtpAndName()
    {
        using var client = new HikvisionClient(Conn, Handler());

        var config = await client.GetConfigurationAsync();

        Assert.Equal(ConfigScope.Appliance, config.Scope);
        Assert.Equal(new DateTime(2026, 9, 9, 7, 24, 29), config.Clock.WallClock);
        Assert.Equal("CST+5:00:00DST01:00:00,M3.2.0/02:00:00,M11.1.0/02:00:00",
            config.Clock.VendorZoneLabel);

        // Hikvision folds DST into the zone string, so there is no switch to report — null,
        // not false.
        Assert.Null(config.Clock.DstEnabled);

        Assert.True(config.NtpEnabled);
        Assert.Equal(TimeSpan.FromMinutes(60), config.NtpInterval);
        var server = Assert.Single(config.NtpServers!);
        Assert.Equal("time.windows.com", server.Address);
        Assert.Equal(123, server.Port);
        Assert.Equal("time.windows.com, every 60 min", config.NtpSummary);

        Assert.Equal("the lab recorder", config.DeviceName);
        Assert.Equal("V4.61.030", config.FirmwareVersion);
        Assert.Empty(config.Failures);
    }

    [Fact]
    public async Task APartReadFailureIsRecorded_NotThrown()
    {
        // A recorder that answers the clock and refuses the port list is worth a partial
        // answer: the clock is the part that matters.
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.AbsolutePath switch
            {
                "/ISAPI/System/time" => MockHttpHandler.Xml(TimeXml),
                "/ISAPI/System/deviceInfo" => MockHttpHandler.Xml(DeviceInfoXml),
                _ => MockHttpHandler.Xml("<x/>", HttpStatusCode.Forbidden),
            });
        using var client = new HikvisionClient(Conn, handler);

        var config = await client.GetConfigurationAsync();

        Assert.Equal(new DateTime(2026, 9, 9, 7, 24, 29), config.Clock.WallClock);
        Assert.Empty(config.Ports);
        Assert.NotEmpty(config.Failures);
        Assert.Contains(config.Failures, f => f.Label == "service ports");
        Assert.Contains(config.Failures, f => f.Label == "the LAN address" ||
            f.Label == "interfaces");
    }

    [Fact]
    public async Task AWriteBeforeAnyReadIsRefused()
    {
        using var client = new HikvisionClient(Conn, Handler());

        var ex = await Assert.ThrowsAsync<NvrException>(() =>
            client.SetNtpAsync(new NtpSettings(Address: "time.nist.gov")));

        Assert.Contains("read the configuration before writing it", ex.Message);
    }

    [Fact]
    public async Task NtpWrite_IsTheDevicesOwnDocumentWithFieldsReplaced()
    {
        var writes = new List<(string Path, string Body)>();
        using var client = new HikvisionClient(Conn, Handler(writes: writes));

        await client.GetConfigurationAsync();
        var change = await client.SetNtpAsync(new NtpSettings(Address: "time.nist.gov",
            Interval: TimeSpan.FromMinutes(30)));

        var write = Assert.Single(writes);
        Assert.Equal("/ISAPI/System/time/ntpServers/1", write.Path);

        // The M-series-only fields survive, which is the whole reason a write is
        // read-modify-write rather than a synthesized minimal document.
        Assert.Contains("<portType", write.Body);
        Assert.Contains("<customPortNo", write.Body);
        Assert.Contains("hostNameExampleList", write.Body);

        Assert.Contains("<hostName>time.nist.gov</hostName>", write.Body);
        Assert.Contains("<synchronizeInterval>30</synchronizeInterval>", write.Body);
        Assert.Contains("<addressingFormatType>hostname</addressingFormatType>", write.Body);

        // The read-back is the fixture, which still says time.windows.com — so the change is
        // reported as rejected rather than as a success. Only the read-back catches a
        // recorder that accepts a field it does not honour.
        Assert.True(change.Rejected);
        Assert.Equal("NTP", change.Field);
    }

    [Fact]
    public async Task AnIpAddressIsWrittenAsAnIpAddress()
    {
        var writes = new List<(string Path, string Body)>();
        using var client = new HikvisionClient(Conn, Handler(writes: writes));

        await client.GetConfigurationAsync();
        await client.SetNtpAsync(new NtpSettings(Address: "192.0.2.123"));

        var write = Assert.Single(writes);
        Assert.Contains("<addressingFormatType>ipaddress</addressingFormatType>", write.Body);
        Assert.Contains("<ipAddress>192.0.2.123</ipAddress>", write.Body);
    }

    [Fact]
    public async Task AWriteIsRefusedWhenTheDocumentMovedUnderneath()
    {
        // Somebody editing the same recorder from a vendor console between the read and the
        // write. Clobbering their edit silently is worse than refusing.
        string served = NtpServerXmlMSeries;
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.AbsolutePath switch
            {
                "/ISAPI/System/time" => MockHttpHandler.Xml(TimeXml),
                "/ISAPI/System/time/ntpServers" => MockHttpHandler.Xml(NtpListXml),
                "/ISAPI/System/time/ntpServers/1" => MockHttpHandler.Xml(served),
                "/ISAPI/System/deviceInfo" => MockHttpHandler.Xml(DeviceInfoXml),
                _ => MockHttpHandler.Xml("<x/>", HttpStatusCode.Forbidden),
            });
        using var client = new HikvisionClient(Conn, handler);

        await client.GetConfigurationAsync();
        served = NtpServerXmlMSeries.Replace("time.windows.com", "pool.ntp.org");

        var ex = await Assert.ThrowsAsync<NvrException>(() =>
            client.SetNtpAsync(new NtpSettings(Port: 1123)));

        Assert.Contains("changed on the device since it was read", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task ReformattingIsNotAChange()
    {
        // Firmware reformats freely between identical reads; only content counts.
        string served = NtpServerXmlMSeries;
        var writes = new List<(string, string)>();
        var handler = new MockHttpHandler((req, body) =>
        {
            if (req.Method == HttpMethod.Put)
            {
                writes.Add((req.RequestUri!.AbsolutePath, body));
                return MockHttpHandler.Xml(OkStatus);
            }
            return req.RequestUri!.AbsolutePath switch
            {
                "/ISAPI/System/time" => MockHttpHandler.Xml(TimeXml),
                "/ISAPI/System/time/ntpServers" => MockHttpHandler.Xml(NtpListXml),
                "/ISAPI/System/time/ntpServers/1" => MockHttpHandler.Xml(served),
                "/ISAPI/System/deviceInfo" => MockHttpHandler.Xml(DeviceInfoXml),
                _ => MockHttpHandler.Xml("<x/>", HttpStatusCode.Forbidden),
            };
        });
        using var client = new HikvisionClient(Conn, handler);

        await client.GetConfigurationAsync();
        served = NtpServerXmlMSeries.Replace("\n", "").Replace("><", ">  <");

        await client.SetNtpAsync(new NtpSettings(Port: 1123));

        Assert.Single(writes);
    }

    [Fact]
    public async Task SyncNowIsRefusedOnARecorderThatSyncsFromNtp()
    {
        using var client = new HikvisionClient(Conn, Handler());

        await client.GetConfigurationAsync();
        var change = await client.SyncTimeNowAsync();

        Assert.False(change.Changed);
        Assert.Contains("syncs from NTP", change.Note);
    }

    [Fact]
    public async Task ADstWriteIsRefusedBecauseHikvisionHasNoDstSwitch()
    {
        using var client = new HikvisionClient(Conn, Handler());

        await client.GetConfigurationAsync();
        var change = await client.SetTimeAsync(new TimeSettings(DstEnabled: true));

        Assert.False(change.Changed);
        Assert.Contains("no DST switch", change.Note);
    }

    [Fact]
    public async Task ZoneAndNameWritesRoundTripTheirOwnDocuments()
    {
        var writes = new List<(string Path, string Body)>();
        using var client = new HikvisionClient(Conn, Handler(writes: writes));

        await client.GetConfigurationAsync();
        await client.SetTimeAsync(new TimeSettings(
            VendorZoneLabel: "CST+5:00:00DST01:00:00,M3.2.0/02:00:00,M11.1.0/02:00:00"));
        await client.SetDeviceNameAsync("front office");

        Assert.Equal("/ISAPI/System/time", writes[0].Path);
        Assert.Contains("<timeZone>CST+5:00:00DST01:00:00", writes[0].Body);
        Assert.Equal("/ISAPI/System/deviceInfo", writes[1].Path);
        Assert.Contains("<deviceName>front office</deviceName>", writes[1].Body);

        // The document's other fields ride along untouched.
        Assert.Contains("<serialNumber>", writes[1].Body);
    }
}
