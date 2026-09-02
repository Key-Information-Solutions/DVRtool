using System.Net;
using System.Net.Http.Headers;
using DVRTool.Core;
using DVRTool.Vendors.NxWitness;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The Nx Cloud relay path: host recognition, and the handler that follows the relay's 307
/// to its regional node once and then keeps every request — Authorization header included —
/// on that node. Shapes are the ones the Site D relay answered on 2026-09-02.
/// </summary>
[Collection("NxRelayNodes")]
public class NxCloudRelayTests
{
    private const string Sid = "66666666-7777-8888-9999-aaaaaaaaaaaa";
    private const string Relay = Sid + ".relay.vmsproxy.com";
    private const string Node = Sid + ".relay-us-mia-1-prod-dp.vmsproxy.com";
    private const string NodeB = Sid + ".relay-us-nyc-1-prod-dp.vmsproxy.com";

    private static NvrConnection Conn(string host = Relay) => new()
    {
        Host = host,
        HttpPort = NxCloudRelay.Port,
        RtspPort = 7001,
        SdkPort = 0,
        Username = "admin",
        Password = "secret",
        UseTls = true,
    };

    private static HttpResponseMessage RedirectTo(string host, string pathAndQuery)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        response.Headers.Location = new Uri($"https://{host}:443{pathAndQuery}");
        return response;
    }

    [Theory]
    [InlineData(Relay, true)]
    [InlineData(Node, true)]
    [InlineData(" " + Relay + " ", true)]
    [InlineData("198.51.100.10", false)]
    [InlineData("vmsproxy.com.example.net", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsRelayHost_RecognizesTheRelayAndItsNodes(string? host, bool expected) =>
        Assert.Equal(expected, NxCloudRelay.IsRelayHost(host));

    [Fact]
    public void HostFor_AndCloudSystemIdOf_RoundTrip()
    {
        Assert.Equal(Relay, NxCloudRelay.HostFor("{66666666-7777-8888-9999-AAAAAAAAAAAA}"));
        Assert.Equal(Sid, NxCloudRelay.CloudSystemIdOf(Relay));
        Assert.Equal(Sid, NxCloudRelay.CloudSystemIdOf(Node));
        Assert.Null(NxCloudRelay.CloudSystemIdOf("10.0.0.5"));
    }

    [Fact]
    public async Task Client_ThroughTheRelay_LearnsTheNodeOnce_AndSendsEverythingThereWithTheToken()
    {
        NxRelayHandler.ForgetNodes();
        var inner = new MockHttpHandler((req, _) =>
        {
            string path = req.RequestUri!.PathAndQuery;
            if (req.RequestUri.Host == Relay)
                return RedirectTo(Node, path);           // the relay only ever redirects
            Assert.Equal(Node, req.RequestUri.Host);
            if (req.Method == HttpMethod.Post && path == "/rest/v3/login/sessions")
                return MockHttpHandler.Text("""{"token":"vms-relay-token","expiresInS":8640000,"username":"admin"}""");
            if (req.Method == HttpMethod.Delete && path.StartsWith("/rest/v3/login/sessions/"))
                return new HttpResponseMessage(HttpStatusCode.OK);
            if (path == "/api/moduleInformation")
                return MockHttpHandler.Text("""{"reply":{"id":"{11111111-2222-3333-4444-555555555555}","name":"TESTRACK1","systemName":"Site D","brand":"dwspectrum","version":"6.1.1.42624","type":"Media Server"}}""");
            return MockHttpHandler.Text("[]");
        });
        var conn = Conn();
        var client = new NxWitnessClient(conn, new NxRelayHandler(new Uri(conn.HttpBase), inner));

        Assert.True(client.ViaCloudRelay);
        var info = await client.GetDeviceInfoAsync();
        client.Dispose();

        Assert.Equal("11111111-2222-3333-4444-555555555555", info.SerialNumber);
        Assert.Equal("DW Spectrum Media Server", info.Model);

        // One anonymous probe to the relay, then the login and every later call on the node —
        // the token on each of them, which is exactly what an automatic redirect would drop.
        var seen = inner.Requests.Select(r =>
            $"{r.Request.Method} {r.Request.RequestUri!.Host}{r.Request.RequestUri.AbsolutePath}").ToList();
        Assert.Equal($"GET {Relay}/api/moduleInformation", seen[0]);
        Assert.Equal($"POST {Node}/rest/v3/login/sessions", seen[1]);
        Assert.Equal($"GET {Node}/api/moduleInformation", seen[2]);
        Assert.Equal($"DELETE {Node}/rest/v3/login/sessions/vms-relay-token", seen[3]);
        Assert.Equal("vms-relay-token", inner.Requests[2].Request.Headers.Authorization!.Parameter);
        Assert.Single(inner.Requests, r => r.Request.RequestUri!.Host == Relay);
    }

    [Fact]
    public async Task Client_ThroughTheRelay_RefusesStreamUrls_WithTheReason()
    {
        NxRelayHandler.ForgetNodes();
        var inner = new MockHttpHandler((req, _) => req.RequestUri!.Host == Relay
            ? RedirectTo(Node, req.RequestUri.PathAndQuery)
            : req.RequestUri.PathAndQuery == "/rest/v3/login/sessions"
                ? MockHttpHandler.Text("""{"token":"vms-x"}""")
                : MockHttpHandler.Text("""[{"id":"{aaaaaaaa-0000-0000-0000-000000000001}","name":"Cam","schedule":{"isEnabled":true,"tasks":[]}}]"""));
        var conn = Conn();
        using var client = new NxWitnessClient(conn, new NxRelayHandler(new Uri(conn.HttpBase), inner));

        var channels = await client.GetChannelsAsync();
        Assert.Single(channels);

        var ex = Assert.Throws<NotSupportedException>(() => client.GetLiveUri(1));
        Assert.Contains("DW Cloud relay", ex.Message);
        Assert.Throws<NotSupportedException>(() =>
            client.GetPlaybackUri(1, new DateTime(2026, 9, 1), new DateTime(2026, 9, 2)));
    }

    [Fact]
    public async Task Handler_WhenTheNodeMoves_RetriesAGetOnTheNewNode_ButNotAPost()
    {
        NxRelayHandler.ForgetNodes();
        var inner = new MockHttpHandler((req, _) =>
        {
            string path = req.RequestUri!.PathAndQuery;
            if (req.RequestUri.Host == Relay)
                return RedirectTo(Node, path);
            if (req.RequestUri.Host == Node)
                return RedirectTo(NodeB, path);           // the first node has gone away
            return MockHttpHandler.Text("""{"moved":true}""");
        });
        var relayUri = new Uri($"https://{Relay}:443");
        using var http = new HttpClient(new NxRelayHandler(relayUri, inner)) { BaseAddress = relayUri };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vms-t");

        using var get = await http.GetAsync("/rest/v3/system/info");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var last = inner.Requests[^1].Request;
        Assert.Equal(NodeB, last.RequestUri!.Host);
        Assert.Equal("vms-t", last.Headers.Authorization!.Parameter);

        // The node is remembered: the next request goes straight to it.
        int before = inner.Requests.Count;
        using var again = await http.GetAsync("/rest/v3/system/info");
        Assert.Equal(before + 1, inner.Requests.Count);
        Assert.Equal(NodeB, inner.Requests[^1].Request.RequestUri!.Host);

        // A body cannot be sent twice, so a redirect on a POST is handed back as it is.
        NxRelayHandler.ForgetNodes();
        using var post = await http.PostAsync("/rest/v3/login/sessions", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.TemporaryRedirect, post.StatusCode);
        Assert.Equal(HttpMethod.Post, inner.Requests[^1].Request.Method);
        Assert.Equal(Node, inner.Requests[^1].Request.RequestUri!.Host);
    }
}
