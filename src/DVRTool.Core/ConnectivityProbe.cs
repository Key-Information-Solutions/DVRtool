using System.Net.Sockets;
using System.Text;

namespace DVRTool.Core;

/// <summary>Which of a system's three ports a probe result describes.</summary>
public enum ProbeTarget
{
    /// <summary>The HTTP/HTTPS API port — ISAPI on Hikvision, the CGI tree on Dahua.</summary>
    Web,
    RtspPort,
    SdkPort,
}

public enum ProbeStatus
{
    /// <summary>The service answered the way that port's service is supposed to.</summary>
    Ok,

    /// <summary>
    /// The port is open and accepted a connection, and a TCP handshake is the most this
    /// probe ever claims for it. Used for the SDK port, whose protocol is proprietary —
    /// nothing is wrong here, so it is not a caution.
    /// </summary>
    Listening,

    /// <summary>
    /// The expected service is alive and answered, but the deeper check did not confirm.
    /// An RTSP server that answers OPTIONS and then refuses to DESCRIBE channel 1 lands
    /// here: the port is fine, the stream is unproven.
    /// </summary>
    Partial,

    /// <summary>Port reachable, service answered, but it rejected who we are.</summary>
    AuthFailed,

    /// <summary>Something answered but did not speak the expected protocol.</summary>
    WrongService,

    /// <summary>
    /// The right kind of service answered, and the login worked — but the device behind it
    /// is not the one this record is pinned to. The port is fine; the wiring is not.
    /// </summary>
    WrongDevice,

    /// <summary>Reached the host, but the port is closed (RST).</summary>
    Refused,

    /// <summary>No answer inside the probe's deadline — the usual signature of a firewall drop.</summary>
    Timeout,

    /// <summary>Name resolution or routing failed; the host itself is not reachable.</summary>
    Unreachable,
}

/// <summary>How loudly a result deserves to be shown.</summary>
public enum ProbeSeverity
{
    /// <summary>Nothing to do.</summary>
    Pass,

    /// <summary>
    /// Something answered, so the port is open to <em>something</em> — but not the full
    /// expected answer. Worth an operator's eye, not an alarm: an error reply still proves
    /// the path through the firewall exists, which is most of what this test is for.
    /// </summary>
    Caution,

    /// <summary>Nothing answered at all: refused, dropped, or unroutable.</summary>
    Fail,
}

/// <summary>One port's verdict. <paramref name="Detail"/> is operator-facing prose.</summary>
public sealed record ProbeResult(
    ProbeTarget Target,
    int Port,
    ProbeStatus Status,
    string Detail,
    TimeSpan Elapsed)
{
    /// <summary>
    /// Who answered the web probe, when it got far enough to ask. Carried so a front end can
    /// pin the serial it just learned onto the record being tested.
    /// </summary>
    public IdentityCheck? Identity { get; init; }

    /// <summary>
    /// Silence is the only <em>port</em> failure: anything that replied — even an error, even
    /// the wrong protocol — proves the port is open and reachable, which is the question an
    /// installer is actually asking, so it caps out at a caution. The one exception is
    /// <see cref="ProbeStatus.WrongDevice"/>, which is not a port verdict at all — the port
    /// is perfect and the answer is still wrong, and that must never render as a pass.
    /// </summary>
    public ProbeSeverity Severity => Status switch
    {
        ProbeStatus.Ok or ProbeStatus.Listening => ProbeSeverity.Pass,
        ProbeStatus.Refused or ProbeStatus.Timeout or ProbeStatus.Unreachable
            or ProbeStatus.WrongDevice => ProbeSeverity.Fail,
        _ => ProbeSeverity.Caution,
    };

    /// <summary>True when the port needs no attention at all.</summary>
    public bool IsGood => Severity == ProbeSeverity.Pass;
}

/// <summary>
/// Reachability checks for the three ports an NVR record carries. Deliberately shallow:
/// the point is to tell an installer which port a site's firewall or NAT forgot, before a
/// three-hour export stalls or the Access tab fails to log in. Nothing here logs in over
/// RTSP or the SDK, and no credentials leave the process except on the web probe, which
/// is the only one that needs them.
/// </summary>
public static class ConnectivityProbe
{
    private static readonly TimeSpan WebTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Probes the web, RTSP and SDK ports concurrently. The tasks come back in a fixed
    /// order (web, RTSP, SDK) so a caller can bind each to a UI row before awaiting, and
    /// render them as they land instead of waiting on the slowest.
    /// </summary>
    /// <param name="clientFactory">
    /// Supplies the vendor client for the web probe. Taken as a factory rather than a live
    /// client so the probe owns its disposal, and so a caller that only wants the port
    /// checks can pass null.
    /// </param>
    /// <param name="expectedSerial">
    /// The serial this record is bound to, when it has one. The web probe reports a
    /// different device as a failure — the port being open is no comfort when what is behind
    /// it is another site's recorder.
    /// </param>
    /// <param name="expectedBy">Whose expectation <paramref name="expectedSerial"/> is.</param>
    public static (Task<ProbeResult> Web, Task<ProbeResult> Rtsp, Task<ProbeResult> Sdk) StartAll(
        NvrConnection conn, Func<INvrClient>? clientFactory, CancellationToken ct = default,
        string? expectedSerial = null, string? expectedBy = null,
        DeviceIdentityStore? identities = null)
    {
        // The vendor's own channel-1 live URL, so the RTSP probe can DESCRIBE a path the
        // device actually serves instead of guessing one. Pure string building — no I/O.
        // A client that cannot name a stream without first reading the device (Nx addresses
        // cameras by id, and a fresh client has no list yet) says so by throwing; the RTSP
        // probe then stops at "an RTSP server answered", which is all it can honestly claim.
        Uri? streamTarget = null;
        if (clientFactory is not null)
        {
            using var urlSource = clientFactory();
            try
            {
                streamTarget = urlSource.GetLiveUri(1, StreamType.Main, includeCredentials: false);
            }
            catch (InvalidOperationException)
            {
                streamTarget = null;
            }
        }

        return (
            clientFactory is null
                ? Task.FromResult(new ProbeResult(ProbeTarget.Web, conn.HttpPort, ProbeStatus.WrongService,
                    "No vendor client available.", TimeSpan.Zero))
                : ProbeWebAsync(conn, clientFactory, ct, expectedSerial, expectedBy, identities),
            ProbeRtspAsync(conn, streamTarget, ct),
            ProbeSdkAsync(conn, ct));
    }

    /// <summary>
    /// The deepest of the three: a real API call, so success also proves TLS (including the
    /// pinned certificate), the credentials, and — the part authentication cannot answer —
    /// that the device on the far end is the one this record means.
    /// </summary>
    public static async Task<ProbeResult> ProbeWebAsync(
        NvrConnection conn, Func<INvrClient> clientFactory, CancellationToken ct = default,
        string? expectedSerial = null, string? expectedBy = null,
        DeviceIdentityStore? identities = null)
    {
        var started = DateTime.UtcNow;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(WebTimeout);
            using var client = clientFactory();
            var info = await client.GetDeviceInfoAsync(timeout.Token);
            string model = info.Model.Length > 0 ? info.Model : "unknown model";
            string reached = $"{model} (serial {info.SerialNumber}, fw {info.FirmwareVersion})";

            // The login succeeded, which on a fleet sharing one account says nothing about
            // *which* system answered. This is the check that does.
            var identity = DeviceIdentityGuard.Check(
                DeviceIdentityGuard.AddressOf(conn), info, expectedSerial, expectedBy, identities);
            var status = identity.Verdict switch
            {
                IdentityVerdict.Mismatch => ProbeStatus.WrongDevice,
                // An unpinnable device is reported as such rather than as a clean pass: the
                // port works and the identity question went unanswered, which is a caution.
                IdentityVerdict.Unverifiable => ProbeStatus.Partial,
                _ => ProbeStatus.Ok,
            };
            string detail = identity.Verdict switch
            {
                IdentityVerdict.Match => reached,
                IdentityVerdict.Mismatch => identity.Message,
                _ => $"{reached} — {identity.Message}",
            };
            return Done(ProbeTarget.Web, conn.HttpPort, status, detail, started) with
            {
                Identity = identity,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Done(ProbeTarget.Web, conn.HttpPort, ProbeStatus.Timeout,
                $"No response in {WebTimeout.TotalSeconds:0}s.", started);
        }
        catch (NvrException ex) when (ex.StatusCode is 401 or 403)
        {
            return Done(ProbeTarget.Web, conn.HttpPort, ProbeStatus.AuthFailed,
                "Port open, but the username or password was rejected.", started);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var (status, detail) = ClassifyWebFailure(ex, conn);
            return Done(ProbeTarget.Web, conn.HttpPort, status, detail, started);
        }
    }

    /// <summary>
    /// Two steps deep. OPTIONS first, where <em>any</em> well-formed RTSP status line proves
    /// a real RTSP server is behind the port — 404 to an OPTIONS on "/" is a perfectly
    /// healthy answer from firmware that only recognizes its own stream paths, so the status
    /// code is reported, never used to fail the port. Then, given the vendor's channel-1
    /// live URL, a DESCRIBE with digest auth: a 200 there means the stream really is
    /// serveable, which is what "RTSP works" is supposed to mean.
    /// </summary>
    /// <param name="streamTarget">
    /// A credential-free live URL from the vendor client. Null skips the DESCRIBE step and
    /// leaves the verdict at "an RTSP server answered".
    /// </param>
    public static async Task<ProbeResult> ProbeRtspAsync(
        NvrConnection conn, Uri? streamTarget = null, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TcpTimeout);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(conn.Host, conn.RtspPort, timeout.Token);
            var stream = tcp.GetStream();

            string serverRoot = $"rtsp://{DeviceAddress.ForUrl(conn.Host)}:{conn.RtspPort}/";
            var options = await ExchangeAsync(stream, "OPTIONS", serverRoot, cseq: 1, null, timeout.Token);

            if (options.Code is null)
                return Done(ProbeTarget.RtspPort, conn.RtspPort, ProbeStatus.WrongService,
                    options.StatusLine.Length == 0
                        ? "Port open, but the service closed the connection without answering."
                        : $"Port open, but the service did not answer RTSP (said \"{Trim(options.StatusLine, 40)}\").",
                    started);

            if (streamTarget is null)
                return Done(ProbeTarget.RtspPort, conn.RtspPort, ProbeStatus.Ok,
                    $"RTSP server answered OPTIONS ({options.Code}).", started);

            // DESCRIBE the real stream. Most firmware demands auth here even when OPTIONS was
            // open, so one 401 round trip is expected rather than a failure.
            var describe = await ExchangeAsync(
                stream, "DESCRIBE", streamTarget.AbsoluteUri, cseq: 2, null, timeout.Token);

            if (describe.Code is 401 && describe.Headers.TryGetValue("www-authenticate", out var challenge))
            {
                string? credential = BuildAuthorization(challenge, conn, "DESCRIBE", streamTarget.AbsoluteUri);
                if (credential is null)
                    return Done(ProbeTarget.RtspPort, conn.RtspPort, ProbeStatus.Partial,
                        "RTSP server answered, but asked for an authentication scheme we do not speak.",
                        started);
                describe = await ExchangeAsync(
                    stream, "DESCRIBE", streamTarget.AbsoluteUri, cseq: 3, credential, timeout.Token);
            }

            return describe.Code switch
            {
                200 => Done(ProbeTarget.RtspPort, conn.RtspPort, ProbeStatus.Ok,
                    "Live stream confirmed — OPTIONS and DESCRIBE on channel 1 both answered.", started),

                401 or 403 => Done(ProbeTarget.RtspPort, conn.RtspPort, ProbeStatus.AuthFailed,
                    "Port open and RTSP alive, but the credentials were rejected for streaming.", started),

                // The service is unambiguously RTSP; only this one path did not resolve. That
                // is a channel or vendor-selection problem, not a port problem, so the port
                // does not get marked bad — but it must not read as a confirmed stream either.
                _ => Done(ProbeTarget.RtspPort, conn.RtspPort, ProbeStatus.Partial,
                    $"RTSP alive (OPTIONS {options.Code}), but channel 1 answered {describe.Code} " +
                    "— check the channel exists and the vendor is right.", started),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Done(ProbeTarget.RtspPort, conn.RtspPort, ProbeStatus.Timeout,
                $"No response in {TcpTimeout.TotalSeconds:0}s — usually a firewall or a missing port forward.",
                started);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var (status, detail) = ClassifySocketFailure(ex, conn.Host);
            return Done(ProbeTarget.RtspPort, conn.RtspPort, status, detail, started);
        }
    }

    /// <summary>
    /// TCP connect only. Both vendors' SDK ports speak a proprietary binary protocol, and
    /// logging in would mean dragging the native SDK into the check — so this reports a
    /// listener, never a working login.
    /// </summary>
    public static async Task<ProbeResult> ProbeSdkAsync(NvrConnection conn, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TcpTimeout);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(conn.Host, conn.SdkPort, timeout.Token);
            return Done(ProbeTarget.SdkPort, conn.SdkPort, ProbeStatus.Listening,
                "Port open and accepting connections (TCP only — a login is not attempted).", started);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Done(ProbeTarget.SdkPort, conn.SdkPort, ProbeStatus.Timeout,
                $"No response in {TcpTimeout.TotalSeconds:0}s — usually a firewall or a missing port forward.",
                started);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var (status, detail) = ClassifySocketFailure(ex, conn.Host);
            return Done(ProbeTarget.SdkPort, conn.SdkPort, status, detail, started);
        }
    }

    /// <summary>
    /// One RTSP reply. <see cref="Code"/> is null when the answer was not RTSP at all —
    /// which is the only case that says anything about <em>what</em> is on the port.
    /// </summary>
    private sealed record RtspReply(int? Code, string StatusLine, Dictionary<string, string> Headers);

    /// <summary>Sends one request and reads its reply head off the same connection.</summary>
    private static async Task<RtspReply> ExchangeAsync(
        Stream stream, string method, string uri, int cseq, string? authorization, CancellationToken ct)
    {
        var request = new StringBuilder()
            .Append($"{method} {uri} RTSP/1.0\r\n")
            .Append($"CSeq: {cseq}\r\n")
            .Append("User-Agent: DVRTool\r\n");
        if (method == "DESCRIBE")
            request.Append("Accept: application/sdp\r\n");
        if (authorization is not null)
            request.Append($"Authorization: {authorization}\r\n");
        request.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), ct);
        await stream.FlushAsync(ct);
        return await ReadReplyAsync(stream, ct);
    }

    /// <summary>
    /// Reads up to the blank line that ends the reply head. Loops on purpose: a single read
    /// can come back short, and a torn "RTSP/1.0 2" would otherwise be misjudged as a
    /// non-RTSP service — a false "port is broken" on a perfectly good link.
    /// </summary>
    private static async Task<RtspReply> ReadReplyAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0)
                break;
            filled += read;
            string sofar = Encoding.ASCII.GetString(buffer, 0, filled);
            if (sofar.Contains("\r\n\r\n", StringComparison.Ordinal) ||
                sofar.Contains("\n\n", StringComparison.Ordinal))
                return ParseReply(sofar);

            // Not RTSP and no end of head in sight — stop reading rather than block for the
            // full deadline on something that will never send a blank line.
            if (filled >= 12 && !sofar.StartsWith("RTSP/", StringComparison.Ordinal))
                break;
        }
        return ParseReply(Encoding.ASCII.GetString(buffer, 0, filled));
    }

    private static RtspReply ParseReply(string head)
    {
        var lines = head.Split('\n');
        string statusLine = lines.Length > 0 ? lines[0].Trim() : "";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            if (line.Trim().Length == 0)
                break;
            int colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        int? code = null;
        if (statusLine.StartsWith("RTSP/", StringComparison.Ordinal))
        {
            string[] parts = statusLine.Split(' ', 3);
            if (parts.Length >= 2 && int.TryParse(parts[1], out int parsed))
                code = parsed;
        }
        return new RtspReply(code, statusLine, headers);
    }

    /// <summary>
    /// Answers an RTSP 401. Digest is what both vendors' firmware asks for; Basic is
    /// accepted as a fallback because some OEM builds still offer only that.
    /// </summary>
    private static string? BuildAuthorization(
        string challenge, NvrConnection conn, string method, string uri)
    {
        if (challenge.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
        {
            var fields = ParseChallenge(challenge);
            if (!fields.TryGetValue("realm", out string? realm) ||
                !fields.TryGetValue("nonce", out string? nonce))
                return null;

            // Live panels send algorithm="MD5" or omit it. Anything else (MD5-sess, SHA-256)
            // needs a different derivation, and sending the MD5 one anyway would come back
            // 401 and be reported as a bad password — the wrong thing to send an installer
            // chasing. Bail instead, so the row says what actually happened.
            if (fields.TryGetValue("algorithm", out string? algorithm) &&
                !algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase))
                return null;

            // MD5 is not a choice here — RFC 2069/2617 digest, which is what the devices
            // speak, is defined on it.
            string ha1 = Md5($"{conn.Username}:{realm}:{conn.Password}");
            string ha2 = Md5($"{method}:{uri}");
            var header = new StringBuilder(
                $"Digest username=\"{conn.Username}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\"");

            if (fields.TryGetValue("qop", out string? qop) &&
                qop.Split(',').Any(q => q.Trim().Equals("auth", StringComparison.OrdinalIgnoreCase)))
            {
                const string cnonce = "dvrtool";
                const string nc = "00000001";
                header.Append($", qop=auth, nc={nc}, cnonce=\"{cnonce}\"");
                header.Append($", response=\"{Md5($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}")}\"");
            }
            else
            {
                header.Append($", response=\"{Md5($"{ha1}:{nonce}:{ha2}")}\"");
            }

            if (fields.TryGetValue("opaque", out string? opaque))
                header.Append($", opaque=\"{opaque}\"");
            return header.ToString();
        }

        if (challenge.StartsWith("Basic", StringComparison.OrdinalIgnoreCase))
            return "Basic " + Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{conn.Username}:{conn.Password}"));

        return null;
    }

    private static Dictionary<string, string> ParseChallenge(string challenge)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Split on commas outside quotes: realm values legitimately contain commas.
        bool inQuotes = false;
        var token = new StringBuilder();
        foreach (char c in challenge["Digest".Length..])
        {
            if (c == '"')
                inQuotes = !inQuotes;
            if (c == ',' && !inQuotes)
            {
                AddField(fields, token.ToString());
                token.Clear();
                continue;
            }
            token.Append(c);
        }
        AddField(fields, token.ToString());
        return fields;

        static void AddField(Dictionary<string, string> into, string token)
        {
            int eq = token.IndexOf('=');
            if (eq <= 0)
                return;
            into[token[..eq].Trim()] = token[(eq + 1)..].Trim().Trim('"');
        }
    }

    private static string Md5(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(text)));

    private static (ProbeStatus, string) ClassifyWebFailure(Exception ex, NvrConnection conn)
    {
        // HttpClient wraps the socket error; the useful diagnosis is underneath.
        if (Innermost<SocketException>(ex) is { } socket)
            return ClassifySocketFailure(socket, conn.Host);

        // A pinned-certificate mismatch surfaces as an auth-shaped failure, not a port one,
        // and is worth calling out by name — it is the one failure here that can mean an
        // interception rather than a misconfiguration.
        if (conn.UseTls && Innermost<System.Security.Authentication.AuthenticationException>(ex) is not null)
            return (ProbeStatus.WrongService,
                "TLS handshake failed — the certificate does not match the pinned one.");

        return (ProbeStatus.WrongService, Trim(Innermost<Exception>(ex)?.Message ?? ex.Message, 120));
    }

    private static (ProbeStatus, string) ClassifySocketFailure(Exception ex, string host)
    {
        if (Innermost<SocketException>(ex) is not { } socket)
            return (ProbeStatus.WrongService, Trim(ex.Message, 120));

        return socket.SocketErrorCode switch
        {
            SocketError.ConnectionRefused =>
                (ProbeStatus.Refused, "Host reachable, but nothing is listening on this port."),
            SocketError.TimedOut =>
                (ProbeStatus.Timeout, "Connection timed out — usually a firewall or a missing port forward."),
            SocketError.HostNotFound or SocketError.NoData =>
                (ProbeStatus.Unreachable, $"Cannot resolve \"{host}\"."),
            SocketError.HostUnreachable or SocketError.NetworkUnreachable =>
                (ProbeStatus.Unreachable, "No route to the host."),
            SocketError.ConnectionReset =>
                (ProbeStatus.WrongService, "The service reset the connection."),
            _ => (ProbeStatus.WrongService, Trim(socket.Message, 120)),
        };
    }

    private static ProbeResult Done(
        ProbeTarget target, int port, ProbeStatus status, string detail, DateTime started) =>
        new(target, port, status, detail, DateTime.UtcNow - started);

    private static T? Innermost<T>(Exception ex) where T : Exception
    {
        T? found = ex as T;
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            if (inner is T typed)
                found = typed;
        return found;
    }

    private static string Trim(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
