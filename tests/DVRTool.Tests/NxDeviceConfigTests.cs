using System.Net;
using DVRTool.Core;
using DVRTool.Vendors.NxWitness;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// Nx / DW Spectrum's answer to "read this recorder's configuration", which is mostly "there
/// isn't one" — and the point of these tests is that "there isn't one" is a <em>designed
/// state</em>, not a failed read. Fixtures follow the 2026-09-09 reads of a DW Spectrum
/// 6.1.1.42624 system (identifiers replaced).
/// </summary>
public class NxDeviceConfigTests
{
    private const string Token = "vms-0123456789abcdef";

    private static NvrConnection Conn(string host = "10.0.0.90") => new()
    {
        Host = host,
        HttpPort = 7001,
        RtspPort = 7001,
        SdkPort = 0,
        Username = "admin",
        Password = "secret",
        UseTls = true,
    };

    private const string LoginReply = """
        {"ageS":0,"expiresInS":2592000,"token":"vms-0123456789abcdef","username":"admin"}
        """;

    // 2026-09-09T11:23:43Z, the reading the discovery pass took.
    private const string SystemInfo = """
        {"cloudId":"66666666-7777-8888-9999-aaaaaaaaaaaa","localId":"{12121212-3434-5656-7878-909090909090}",
         "name":"TESTRACK1","systemName":"SiteD","synchronizedTimeMs":"1788953023000",
         "version":"6.1.1.42624","devices":64,"servers":1}
        """;

    /// <summary>
    /// The time-related keys out of the 115 <c>/rest/v3/system/settings</c> returns. This is a
    /// distributed-clock configuration: <c>primaryTimeServer</c> names which server in the
    /// system is the clock master by GUID, and all-zero means "follow the internet".
    /// </summary>
    private const string Settings = """
        {"timeSynchronizationEnabled":true,
         "primaryTimeServer":"{00000000-0000-0000-0000-000000000000}",
         "syncTimeEpsilon":200,"syncTimeExchangePeriod":600000,
         "osTimeChangeCheckPeriodMs":5000,
         "maxDifferenceBetweenSynchronizedAndInternetTime":2000,
         "maxDifferenceBetweenSynchronizedAndLocalTimeMs":2000,
         "cameraSettingsOptimization":true}
        """;

    private const string Servers = """
        [{"id":"{11111111-2222-3333-4444-555555555555}","name":"TESTRACK1","status":"Online",
          "endpoints":["10.0.0.90:7001","192.0.2.90:7001","127.0.0.1:7001","[::1]:7001"]}]
        """;

    private static MockHttpHandler Server(HashSet<string>? notFound = null)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/rest/v3/system/info"] = SystemInfo,
            ["/rest/v3/system/settings"] = Settings,
            ["/rest/v3/servers"] = Servers,
        };
        return new MockHttpHandler((req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post && path == "/rest/v3/login/sessions")
                return MockHttpHandler.Text(LoginReply);
            if (req.Method == HttpMethod.Delete)
                return MockHttpHandler.Text("{}");
            if (notFound?.Contains(path) == true || !bodies.TryGetValue(path, out string? body))
                return MockHttpHandler.Text(
                    """{"errorId":"notFound","errorString":"Not found"}""",
                    HttpStatusCode.NotFound);
            return MockHttpHandler.Text(body);
        });
    }

    private static NxWitnessClient Client(MockHttpHandler handler)
    {
        var client = new NxWitnessClient(Conn(), handler) { Zone = TimeZoneInfo.Utc };
        return client;
    }

    [Fact]
    public async Task GetClock_ReadsTheVmsClock()
    {
        var handler = Server();
        using var client = Client(handler);

        var clock = await client.GetClockAsync();

        // A UTC instant rendered in the operator's zone, like every other Nx time.
        Assert.Equal(new DateTime(2026, 9, 9, 11, 23, 43), clock.WallClock);
        Assert.Equal(DateTimeKind.Unspecified, clock.WallClock.Kind);

        // No declared offset, no vendor zone, no DST switch — none of which is a failure.
        Assert.Null(clock.DeclaredOffset);
        Assert.Null(clock.VendorZoneLabel);
        Assert.Null(clock.DstEnabled);

        Assert.Equal("/rest/v3/system/info",
            handler.Requests[^1].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task TimeSource_HasNoNtpOpinion_AndDescribesTheClockMaster()
    {
        using var client = Client(Server());

        var source = await client.GetTimeSourceAsync();

        // Null, never false: "no NTP client" and "NTP switched off" are different states, and
        // the audit must not accuse this server of having no time source.
        Assert.Null(source.NtpEnabled);
        Assert.Equal("VMS clock sync on, no clock master (follows the internet)", source.Detail);
    }

    [Fact]
    public async Task Configuration_IsClockOnly_AndSaysWhyTheRestIsAbsent()
    {
        using var client = Client(Server());

        var config = await client.GetConfigurationAsync();

        Assert.Equal(ConfigScope.ClockOnly, config.Scope);
        Assert.True(config.Scope.Clock);
        Assert.False(config.Scope.Network);
        Assert.False(config.Scope.Ntp);
        Assert.False(config.Scope.TimeZone);
        Assert.False(config.Scope.Ports);
        Assert.Contains("no device configuration to read", config.Scope.Reason);

        // Null means "this recorder has none of that", which the front ends render as "n/a"
        // with the reason. Empty or zeroed values here would read as a broken recorder.
        Assert.Null(config.Interfaces);
        Assert.Null(config.NtpServers);
        Assert.Null(config.NtpEnabled);
        Assert.Empty(config.Ports);
        Assert.Empty(config.Failures);

        Assert.Equal(new DateTime(2026, 9, 9, 11, 23, 43), config.Clock.WallClock);
        Assert.Equal("6.1.1.42624", config.FirmwareVersion);
        Assert.Equal("SiteD", config.DeviceName);
    }

    [Fact]
    public async Task Notes_CarryTheClockMasterAndTheObservedAddresses()
    {
        using var client = Client(Server());

        var config = await client.GetConfigurationAsync();

        Assert.Contains(config.Notes, n =>
            n.Label == "primaryTimeServer" && n.Value.Contains("00000000"));
        Assert.Contains(config.Notes, n => n.Label == "syncTimeEpsilon" && n.Value == "200");

        // Four endpoints, and the group name says these are observed rather than configured —
        // presenting them as configuration would invite an operator to try to change them.
        var endpoints = config.Notes.Where(n => n.Group == "Observed addresses").ToList();
        Assert.Equal(4, endpoints.Count);
        Assert.Contains(endpoints, n => n.Value.Contains("10.0.0.90:7001"));
    }

    [Fact]
    public async Task AMissingSettingsEndpointIsAFailedPart_NotAFailedRead()
    {
        using var client = Client(Server(notFound: ["/rest/v3/system/settings"]));

        var config = await client.GetConfigurationAsync();

        Assert.Equal(new DateTime(2026, 9, 9, 11, 23, 43), config.Clock.WallClock);
        Assert.Contains(config.Failures, f => f.Label == "VMS clock settings");
    }

    [Fact]
    public async Task AServerWithNoClockIsAnError_NotAZeroClock()
    {
        var handler = new MockHttpHandler((req, _) =>
            req.Method == HttpMethod.Post
                ? MockHttpHandler.Text(LoginReply)
                : req.RequestUri!.AbsolutePath == "/rest/v3/system/info"
                    ? MockHttpHandler.Text("""{"version":"6.1.1.42624"}""")
                    : MockHttpHandler.Text("{}"));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() => client.GetClockAsync());

        Assert.Contains("synchronizedTimeMs", ex.Message);
    }

    [Fact]
    public void ThereIsNoWriter()
    {
        // Deliberate: a distributed clock master is not an NTP server, and setting a zone
        // means logging into Windows on the box.
        Assert.True(typeof(IDeviceConfigClient).IsAssignableFrom(typeof(NxWitnessClient)));
        Assert.False(typeof(IDeviceConfigWriter).IsAssignableFrom(typeof(NxWitnessClient)));
    }

    [Fact]
    public async Task TheEndpointsThatDoNotExistAreNeverAsked()
    {
        var handler = Server();
        using var client = Client(handler);

        await client.GetConfigurationAsync();

        // /rest/v3/system/time and /rest/v3/servers/this/time are both 404 on this build.
        var paths = handler.Requests.Select(r => r.Request.RequestUri!.AbsolutePath).ToList();
        Assert.DoesNotContain("/rest/v3/system/time", paths);
        Assert.DoesNotContain("/rest/v3/servers/this/time", paths);
    }
}
