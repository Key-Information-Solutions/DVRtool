using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DVRTool.Core;
using DVRTool.Vendors.Hikvision;
using Xunit;

namespace DVRTool.Tests;

public class ConnectivityProbeTests
{
    private sealed record FakeRequest(string Method, string Uri, Dictionary<string, string> Headers);

    /// <summary>
    /// A loopback stand-in for a device's RTSP service. Holds the connection open across
    /// requests the way real firmware does, so the probe's OPTIONS-then-DESCRIBE exchange
    /// is exercised on one socket. A null responder accepts and stays mute — what a port
    /// forwarded to a dead service looks like from outside.
    /// </summary>
    private sealed class FakeService : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serving;
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<FakeRequest, (string Reply, string? Trailer)>? _respond;
        private readonly List<FakeRequest> _seen = [];

        public FakeService(Func<FakeRequest, (string Reply, string? Trailer)>? respond = null)
        {
            _respond = respond;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serving = ServeAsync();
        }

        public int Port { get; }

        /// <summary>Requests the probe actually sent, in order.</summary>
        public IReadOnlyList<FakeRequest> Seen
        {
            get { lock (_seen) return _seen.ToArray(); }
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                while (_respond is not null)
                {
                    var request = await ReadRequestAsync(stream);
                    if (request is null)
                        break;
                    lock (_seen)
                        _seen.Add(request);

                    var (reply, trailer) = _respond(request);
                    await Write(stream, reply);
                    if (trailer is not null)
                    {
                        // Deliberately a second segment: the probe must stitch a torn
                        // status line rather than judge the service on the first read.
                        await Task.Delay(50, _stop.Token);
                        await Write(stream, trailer);
                    }
                }
                // Hold the socket up until teardown so nothing is lost to an early close.
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (IOException) { }
        }

        private async Task Write(Stream stream, string text)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(text), _stop.Token);
            await stream.FlushAsync(_stop.Token);
        }

        private async Task<FakeRequest?> ReadRequestAsync(Stream stream)
        {
            var head = new StringBuilder();
            var buffer = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                int read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                    return null;
                head.Append((char)buffer[0]);
            }

            string[] lines = head.ToString().Split("\r\n");
            string[] start = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            return new FakeRequest(start[0], start.Length > 1 ? start[1] : "", headers);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await _serving; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }

    private const string Realm = "IP Camera(C2891)";
    private const string Nonce = "4e4f4e43453a30";

    private static NvrConnection Conn(int rtspPort = 554, int sdkPort = 8000) => new()
    {
        Host = "127.0.0.1",
        RtspPort = rtspPort,
        SdkPort = sdkPort,
        Username = "admin",
        Password = "s3cret",
    };

    private static Uri Target(int port) => new($"rtsp://127.0.0.1:{port}/Streaming/Channels/101");

    private static string Md5(string text) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The digest the device should receive for a DESCRIBE, computed independently.</summary>
    private static string ExpectedDigest(NvrConnection conn, string uri, string? qop = null)
    {
        string ha1 = Md5($"{conn.Username}:{Realm}:{conn.Password}");
        string ha2 = Md5($"DESCRIBE:{uri}");
        return qop is null
            ? Md5($"{ha1}:{Nonce}:{ha2}")
            : Md5($"{ha1}:{Nonce}:00000001:dvrtool:auth:{ha2}");
    }

    private static string Reply(int code, string reason, string? extraHeader = null) =>
        $"RTSP/1.0 {code} {reason}\r\nCSeq: 1\r\n" + (extraHeader is null ? "" : extraHeader + "\r\n") + "\r\n";

    private static string Sdp() =>
        "RTSP/1.0 200 OK\r\nCSeq: 2\r\nContent-Type: application/sdp\r\nContent-Length: 12\r\n\r\nv=0\r\no=- 0 0\r\n";

    /// <summary>A port nothing is listening on: bind, read the number, drop it.</summary>
    private static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task Rtsp_OptionsAnswers404_ThenStreamDescribes_IsLive()
    {
        // The real DS-7716NI-I4/16P behaviour: it 404s an OPTIONS on "/" because it only
        // knows its own stream paths, then serves the channel perfectly well. Treating that
        // 404 as a failed port would call a live system broken.
        await using var service = new FakeService(req => req.Method == "OPTIONS"
            ? (Reply(404, "Not Found"), null)
            : (Sdp(), null));

        var result = await ConnectivityProbe.ProbeRtspAsync(
            Conn(rtspPort: service.Port), Target(service.Port));

        Assert.Equal(ProbeStatus.Ok, result.Status);
        Assert.Equal(ProbeSeverity.Pass, result.Severity);
        Assert.Contains("Live stream confirmed", result.Detail);
    }

    [Fact]
    public async Task Rtsp_DigestChallenge_IsAnsweredWithTheCorrectResponse()
    {
        var conn = Conn();
        string uri = "";
        await using var service = new FakeService(req =>
        {
            if (req.Method == "OPTIONS")
                return (Reply(200, "OK"), null);
            if (!req.Headers.TryGetValue("Authorization", out string? auth))
                return (Reply(401, "Unauthorized",
                    $"WWW-Authenticate: Digest realm=\"{Realm}\", nonce=\"{Nonce}\""), null);
            uri = req.Uri;
            return auth.Contains(ExpectedDigest(conn, req.Uri), StringComparison.OrdinalIgnoreCase)
                ? (Sdp(), null)
                : (Reply(401, "Unauthorized"), null);
        });

        conn = conn with { RtspPort = service.Port };
        var result = await ConnectivityProbe.ProbeRtspAsync(conn, Target(service.Port));

        Assert.Equal(ProbeStatus.Ok, result.Status);
        Assert.Equal(Target(service.Port).AbsoluteUri, uri);
        Assert.Equal(["OPTIONS", "DESCRIBE", "DESCRIBE"], service.Seen.Select(r => r.Method));
    }

    [Fact]
    public async Task Rtsp_DigestChallengeWithQop_IsAnsweredWithTheCorrectResponse()
    {
        var conn = Conn();
        await using var service = new FakeService(req =>
        {
            if (req.Method == "OPTIONS")
                return (Reply(200, "OK"), null);
            if (!req.Headers.TryGetValue("Authorization", out string? auth))
                return (Reply(401, "Unauthorized",
                    $"WWW-Authenticate: Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth\""), null);
            return auth.Contains(ExpectedDigest(conn, req.Uri, qop: "auth"), StringComparison.OrdinalIgnoreCase)
                ? (Sdp(), null)
                : (Reply(401, "Unauthorized"), null);
        });

        var result = await ConnectivityProbe.ProbeRtspAsync(
            conn with { RtspPort = service.Port }, Target(service.Port));

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task Rtsp_RealFirmwareChallengeShape_IsAccepted()
    {
        // Verbatim from a live DS-7716NI-I4/16P: hex realm, short nonce, algorithm="MD5",
        // no qop. Kept as a test so a parser change cannot silently stop matching it.
        var conn = Conn();
        await using var service = new FakeService(req =>
        {
            if (req.Method == "OPTIONS")
                return (Reply(404, "Not Found"), null);
            if (!req.Headers.TryGetValue("Authorization", out string? auth))
                return (Reply(401, "Unauthorized",
                    "WWW-Authenticate: Digest realm=\"fd65084efabf8d71c79ea288\", " +
                    "nonce=\"26717d160\", algorithm=\"MD5\""), null);

            string ha1 = Md5($"{conn.Username}:fd65084efabf8d71c79ea288:{conn.Password}");
            string expected = Md5($"{ha1}:26717d160:{Md5($"DESCRIBE:{req.Uri}")}");
            return auth.Contains(expected, StringComparison.OrdinalIgnoreCase)
                ? (Sdp(), null)
                : (Reply(401, "Unauthorized"), null);
        });

        var result = await ConnectivityProbe.ProbeRtspAsync(
            conn with { RtspPort = service.Port }, Target(service.Port));

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task Rtsp_UnsupportedDigestAlgorithm_IsPartial_NotABadPassword()
    {
        await using var service = new FakeService(req => req.Method == "OPTIONS"
            ? (Reply(200, "OK"), null)
            : (Reply(401, "Unauthorized",
                $"WWW-Authenticate: Digest realm=\"{Realm}\", nonce=\"{Nonce}\", algorithm=SHA-256"), null));

        var result = await ConnectivityProbe.ProbeRtspAsync(
            Conn(rtspPort: service.Port), Target(service.Port));

        Assert.Equal(ProbeStatus.Partial, result.Status);
        Assert.Contains("authentication scheme", result.Detail);
    }

    [Fact]
    public async Task Rtsp_BadPassword_IsAuthFailed_NotADeadPort()
    {
        await using var service = new FakeService(req => req.Method == "OPTIONS"
            ? (Reply(200, "OK"), null)
            : (Reply(401, "Unauthorized",
                $"WWW-Authenticate: Digest realm=\"{Realm}\", nonce=\"{Nonce}\""), null));

        var result = await ConnectivityProbe.ProbeRtspAsync(
            Conn(rtspPort: service.Port), Target(service.Port));

        Assert.Equal(ProbeStatus.AuthFailed, result.Status);
        // The port is demonstrably open — the fix is the password, not the firewall.
        Assert.Equal(ProbeSeverity.Caution, result.Severity);
    }

    [Fact]
    public async Task Rtsp_ChannelMissing_IsPartial_NotAFailure()
    {
        await using var service = new FakeService(req => req.Method == "OPTIONS"
            ? (Reply(200, "OK"), null)
            : (Reply(404, "Not Found"), null));

        var result = await ConnectivityProbe.ProbeRtspAsync(
            Conn(rtspPort: service.Port), Target(service.Port));

        Assert.Equal(ProbeStatus.Partial, result.Status);
        Assert.Equal(ProbeSeverity.Caution, result.Severity);
        Assert.Contains("404", result.Detail);
    }

    [Fact]
    public async Task Rtsp_WithoutAStreamTarget_StopsAtOptions()
    {
        await using var service = new FakeService(_ => (Reply(455, "Method Not Valid In This State"), null));

        var result = await ConnectivityProbe.ProbeRtspAsync(Conn(rtspPort: service.Port));

        // Any well-formed RTSP status line proves the service; the code is reported, not judged.
        Assert.Equal(ProbeStatus.Ok, result.Status);
        Assert.Contains("455", result.Detail);
        Assert.Single(service.Seen);
    }

    [Fact]
    public async Task Rtsp_HttpServerOnTheRtspPort_IsWrongServiceButStillOnlyACaution()
    {
        await using var service = new FakeService(_ => ("HTTP/1.1 400 Bad Request\r\n\r\n", null));

        var result = await ConnectivityProbe.ProbeRtspAsync(
            Conn(rtspPort: service.Port), Target(service.Port));

        Assert.Equal(ProbeStatus.WrongService, result.Status);
        Assert.Equal(ProbeSeverity.Caution, result.Severity);
    }

    [Fact]
    public async Task Rtsp_StatusLineArrivingInPieces_IsStillRead()
    {
        await using var service = new FakeService(req => req.Method == "OPTIONS"
            ? ("RTSP/1.0 2", "00 OK\r\nCSeq: 1\r\n\r\n")
            : (Sdp(), null));

        var result = await ConnectivityProbe.ProbeRtspAsync(
            Conn(rtspPort: service.Port), Target(service.Port));

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task Rtsp_MuteService_IsTimeout()
    {
        await using var service = new FakeService();
        var result = await ConnectivityProbe.ProbeRtspAsync(Conn(rtspPort: service.Port));
        Assert.Equal(ProbeStatus.Timeout, result.Status);
        Assert.Equal(ProbeSeverity.Fail, result.Severity);
    }

    [Fact]
    public async Task Rtsp_ClosedPort_IsRefused()
    {
        var result = await ConnectivityProbe.ProbeRtspAsync(Conn(rtspPort: ClosedPort()));
        Assert.Equal(ProbeStatus.Refused, result.Status);
        Assert.Equal(ProbeSeverity.Fail, result.Severity);
        Assert.Equal(ProbeTarget.RtspPort, result.Target);
    }

    [Fact]
    public async Task Sdk_ListenerPresent_PassesWithoutClaimingALogin()
    {
        // The SDK protocol is proprietary, so a handshake is the whole claim — but a port
        // that accepts connections is not something an operator needs to look at, so it
        // passes rather than nagging on every healthy system.
        await using var service = new FakeService();
        var result = await ConnectivityProbe.ProbeSdkAsync(Conn(sdkPort: service.Port));
        Assert.Equal(ProbeStatus.Listening, result.Status);
        Assert.Equal(ProbeSeverity.Pass, result.Severity);
        Assert.Contains("a login is not attempted", result.Detail);
    }

    [Fact]
    public async Task Sdk_ClosedPort_IsRefused()
    {
        var result = await ConnectivityProbe.ProbeSdkAsync(Conn(sdkPort: ClosedPort()));
        Assert.Equal(ProbeStatus.Refused, result.Status);
        Assert.Equal(ProbeTarget.SdkPort, result.Target);
    }

    [Fact]
    public async Task Sdk_UnresolvableHost_IsUnreachable()
    {
        var result = await ConnectivityProbe.ProbeSdkAsync(Conn() with { Host = "nonexistent.invalid" });
        Assert.Equal(ProbeStatus.Unreachable, result.Status);
        Assert.Equal(ProbeSeverity.Fail, result.Severity);
    }

    [Fact]
    public async Task CallerCancellation_Propagates_RatherThanBecomingAFakeVerdict()
    {
        // The dialog cancels these when it closes; a cancelled probe must not surface as a
        // port verdict, or a closed window would leave a bogus row behind.
        await using var service = new FakeService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ConnectivityProbe.ProbeSdkAsync(Conn(sdkPort: service.Port), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ConnectivityProbe.ProbeRtspAsync(Conn(rtspPort: service.Port), null, cts.Token));
    }

    [Fact]
    public async Task StartAll_DescribesTheVendorsOwnChannelOnePath()
    {
        // The DESCRIBE has to hit a path the device really serves, or every probe would
        // report a phantom 404 against a URL of our own invention.
        await using var service = new FakeService(req => req.Method == "OPTIONS"
            ? (Reply(200, "OK"), null)
            : (Sdp(), null));

        var conn = Conn(rtspPort: service.Port);
        var (_, rtsp, _) = ConnectivityProbe.StartAll(conn, () => new HikvisionClient(conn));

        Assert.Equal(ProbeStatus.Ok, (await rtsp).Status);
        Assert.Equal($"rtsp://127.0.0.1:{service.Port}/Streaming/Channels/101",
            service.Seen.Single(r => r.Method == "DESCRIBE").Uri);
    }

    [Fact]
    public async Task StartAll_ClientThatCannotNameAStreamYet_StillProbesRtsp()
    {
        // An Nx client addresses cameras by id and has no list until it has read the device,
        // so its URL builder throws on a fresh instance. The probe must take that as "no
        // DESCRIBE target" and still report the RTSP server that answered OPTIONS.
        await using var service = new FakeService(req => req.Method == "OPTIONS"
            ? (Reply(200, "OK"), null)
            : (Reply(404, "Not Found"), null));

        var conn = new NvrConnection
        {
            Host = "127.0.0.1",
            HttpPort = 1, // nothing listens; the web probe fails on its own and is not awaited here
            RtspPort = service.Port,
            SdkPort = 0,
            Username = "u",
            Password = "p",
        };
        var (_, rtsp, _) = ConnectivityProbe.StartAll(conn,
            () => new DVRTool.Vendors.NxWitness.NxWitnessClient(conn));

        var result = await rtsp;
        Assert.Equal(ProbeStatus.Ok, result.Status);
        Assert.Contains("OPTIONS", result.Detail);
        Assert.DoesNotContain(service.Seen, r => r.Method == "DESCRIBE");
    }

    [Fact]
    public async Task StartAll_WithoutAClientFactory_StillChecksTheTwoPortProbes()
    {
        await using var rtsp = new FakeService(_ => (Reply(200, "OK"), null));
        await using var sdk = new FakeService();

        var (web, rtspTask, sdkTask) = ConnectivityProbe.StartAll(
            Conn(rtspPort: rtsp.Port, sdkPort: sdk.Port), clientFactory: null);

        Assert.Equal(ProbeStatus.Ok, (await rtspTask).Status);
        Assert.Equal(ProbeStatus.Listening, (await sdkTask).Status);
        Assert.False((await web).IsGood);
    }
}
