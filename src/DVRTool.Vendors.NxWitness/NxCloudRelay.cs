using System.Collections.Concurrent;

namespace DVRTool.Vendors.NxWitness;

/// <summary>
/// The Nx Cloud relay: <c>https://{cloudSystemId}.relay.vmsproxy.com</c> proxies HTTPS to a
/// cloud-connected system with no port forward at all — the route the DW / Nx desktop and
/// mobile clients fall back to, and the one that reaches a site whose router never forwarded
/// 7001. DW Cloud is Nx Cloud under another name, so the same domain serves DW Spectrum.
/// </summary>
/// <remarks>
/// Verified on Site D (Site D) 2026-09-02: the relay answers a <c>307</c> to a regional node
/// (<c>…relay-us-mia-1-prod-dp.vmsproxy.com</c>) that must be followed once, with the request's
/// own headers; it presents a Let's Encrypt wildcard certificate that rotates every 90 days
/// (so the trust-on-first-use pin does not apply — chain validation does); it carries HTTPS
/// only, never RTSP; and a <em>local</em> account logs in through it exactly as on the LAN
/// (<c>POST /rest/v3/login/sessions</c>), so no cloud account is needed. The identity pin is
/// unaffected: the server GUID comes through unchanged.
/// </remarks>
public static class NxCloudRelay
{
    /// <summary>The relay domain; a system's relay host is its cloud system id under it.</summary>
    public const string Domain = "relay.vmsproxy.com";

    /// <summary>The relay speaks HTTPS on the standard port.</summary>
    public const int Port = 443;

    /// <summary>
    /// True for a relay host (<c>{id}.relay.vmsproxy.com</c>) and for the regional nodes it
    /// redirects to (<c>{id}.relay-us-mia-1-prod-dp.vmsproxy.com</c>), which an operator may
    /// paste after seeing one in a redirect.
    /// </summary>
    public static bool IsRelayHost(string? host) =>
        host is not null &&
        host.Trim().EndsWith(".vmsproxy.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The relay host for a cloud system id (braces tolerated).</summary>
    public static string HostFor(string cloudSystemId) =>
        $"{NxJson.StripBraces(cloudSystemId).ToLowerInvariant()}.{Domain}";

    /// <summary>The cloud system id a relay host names, or null for any other host.</summary>
    public static string? CloudSystemIdOf(string? host)
    {
        if (!IsRelayHost(host))
            return null;
        string first = host!.Trim().Split('.')[0];
        return first.Length > 0 ? first : null;
    }
}

/// <summary>
/// Follows the relay's redirect to its regional node once, then sends every request to that
/// node directly — with the request's own headers intact, which is the whole point: .NET
/// drops <c>Authorization</c> on a cross-host redirect, and the node is a different host.
/// </summary>
/// <remarks>
/// The node is learned with an anonymous <c>GET /api/moduleInformation</c> against the relay
/// host and remembered process-wide per relay host, so the desktop app's many short-lived
/// clients pay for the lookup once. A redirect on a later request means the node moved: it
/// is re-learned and the request retried once when it can be (no body to re-send); a POST
/// or PATCH in that window surfaces the redirect as an error and the caller's retry lands
/// on the new node.
/// </remarks>
internal sealed class NxRelayHandler : DelegatingHandler
{
    private static readonly ConcurrentDictionary<string, Uri> Nodes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Uri _relay;

    public NxRelayHandler(Uri relayOrigin, HttpMessageHandler inner)
        : base(inner)
    {
        _relay = new Uri(relayOrigin.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>Test seam: forget every learned node.</summary>
    internal static void ForgetNodes() => Nodes.Clear();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken ct)
    {
        var node = await ResolveNodeAsync(ct);
        if (request.RequestUri is { } uri && IsRelayOrigin(uri))
            request.RequestUri = new Uri(node, uri.PathAndQuery);

        var response = await base.SendAsync(request, ct);
        if (!IsRedirect(response) || RedirectTarget(response) is not { } moved)
            return response;

        Nodes[_relay.Host] = moved;
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head &&
            request.Method != HttpMethod.Delete)
            return response; // a body cannot be sent twice; the caller sees the redirect

        response.Dispose();
        using var retry = new HttpRequestMessage(request.Method,
            new Uri(moved, request.RequestUri!.PathAndQuery));
        foreach (var header in request.Headers)
            retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return await base.SendAsync(retry, ct);
    }

    private async Task<Uri> ResolveNodeAsync(CancellationToken ct)
    {
        if (Nodes.TryGetValue(_relay.Host, out var known))
            return known;
        using var probe = new HttpRequestMessage(HttpMethod.Get, new Uri(_relay, "/api/moduleInformation"));
        using var response = await base.SendAsync(probe, ct);
        var node = IsRedirect(response) ? RedirectTarget(response) ?? _relay : _relay;
        Nodes[_relay.Host] = node;
        return node;
    }

    /// <summary>The origin a redirect points at; null when the relay redirected within itself.</summary>
    private Uri? RedirectTarget(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null)
            return null;
        var absolute = location.IsAbsoluteUri ? location : new Uri(_relay, location);
        var origin = new Uri(absolute.GetLeftPart(UriPartial.Authority));
        return IsRelayOrigin(origin) ? null : origin;
    }

    private static bool IsRedirect(HttpResponseMessage response) =>
        (int)response.StatusCode is 301 or 302 or 307 or 308;

    private bool IsRelayOrigin(Uri uri) =>
        string.Equals(uri.Host, _relay.Host, StringComparison.OrdinalIgnoreCase);
}
