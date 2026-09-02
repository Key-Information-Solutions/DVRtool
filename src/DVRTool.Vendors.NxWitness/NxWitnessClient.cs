using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DVRTool.Core;

namespace DVRTool.Vendors.NxWitness;

/// <summary>
/// Nx Witness / DW Spectrum client: the media server's REST API (bearer-token sessions, JSON,
/// HTTPS on 7001 with a self-signed certificate pinned trust-on-first-use) for device info,
/// cameras, footage periods and the <c>/media/</c> export, plus RTSP on the same port for
/// live view and playback-by-time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Channel numbers.</b> Nx addresses cameras by GUID and has no channel numbers of its
/// own, so this client numbers the camera list 1..N sorted by name (then id) — the order the
/// Nx client's resource tree shows — and keeps that list for the life of the instance so
/// every call agrees on which camera a number means. The two URL builders are synchronous
/// and can only resolve a number after some call has read the list; a fresh client that is
/// asked for a URL first throws <see cref="InvalidOperationException"/> rather than guessing.
/// </para>
/// <para>
/// <b>Identity.</b> The serial pinned per address is the media server's id from the
/// anonymous <c>/api/moduleInformation</c>; the login is still performed first, because the
/// connectivity probe and the identity guard use <see cref="GetDeviceInfoAsync"/> as the
/// proof that the credentials work.
/// </para>
/// <para>
/// <b>Time.</b> Nx speaks UTC milliseconds everywhere. The rest of DVRTool speaks recorder-
/// local wall clock, so values are rendered in <see cref="Zone"/> (the operator's own zone by
/// default — the honest choice for a fleet inside one state, and the one the Nx client uses
/// too). See <c>docs/nx-witness-storage.md</c>.
/// </para>
/// </remarks>
public sealed partial class NxWitnessClient : INvrClient
{
    private const string LoginPath = "/rest/v3/login/sessions";
    private const string DevicesPath = "/rest/v3/devices";

    private readonly HttpClient _http;
    private readonly HttpClient _downloadHttp;
    private readonly bool _ownsDownloadHttp;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private string? _token;
    private string? _serverId;

    // This instance's camera list (numbering is positional, so one list per instance), and
    // the list this process last saw for the same address before this instance read its own —
    // the write path compares the two, see GuardCameraListUnchanged.
    private IReadOnlyList<NxCamera>? _cameras;
    private IReadOnlyList<NxCamera>? _camerasBefore;

    private static readonly ConcurrentDictionary<string, IReadOnlyList<NxCamera>> LastKnownCameras =
        new(StringComparer.OrdinalIgnoreCase);

    public Vendor Vendor => Vendor.NxWitness;
    public NvrConnection Connection { get; }

    /// <summary>
    /// The zone Nx's UTC timestamps are rendered in and local windows are read from. The
    /// operator's own by default; tests pin it.
    /// </summary>
    internal TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    public NxWitnessClient(NvrConnection connection)
    {
        Connection = connection;
        _http = NvrHttp.Create(connection, handler: CreateHandler(connection));
        // Exports can run for many minutes; rely on the CancellationToken instead of Timeout.
        _downloadHttp = NvrHttp.Create(connection, Timeout.InfiniteTimeSpan, CreateHandler(connection));
        _ownsDownloadHttp = true;
    }

    /// <summary>Test seam: routes all traffic through the supplied handler.</summary>
    public NxWitnessClient(NvrConnection connection, HttpMessageHandler handler)
    {
        Connection = connection;
        _http = NvrHttp.Create(connection, handler: handler);
        _downloadHttp = _http;
        _ownsDownloadHttp = false;
    }

    /// <summary>
    /// No digest credentials on the handler: Nx authenticates with a bearer token (digest is
    /// off by default for Nx users, and a digest challenge would only add a wasted round trip).
    /// The certificate pin is the same trust-on-first-use pin every vendor's HTTPS gets.
    /// </summary>
    private static HttpMessageHandler CreateHandler(NvrConnection conn)
    {
        var handler = new HttpClientHandler();
        if (conn.UseTls)
            handler.ServerCertificateCustomValidationCallback =
                CertificatePins.CreateValidator(conn.Host, conn.HttpPort);
        return handler;
    }

    // ----- session -----

    /// <summary>
    /// Logs in once per client — <c>POST /rest/v3/login/sessions</c> → token — and puts it on
    /// every later request as <c>Authorization: Bearer</c>. A rejected login is an
    /// <see cref="NvrException"/> carrying the 401/403, which is what the connectivity probe
    /// keys "credentials rejected" on.
    /// </summary>
    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_token is not null)
            return;
        await _loginGate.WaitAsync(ct);
        try
        {
            if (_token is not null)
                return;
            if (Connection.Password.Length == 0)
                throw new NvrException(
                    "Nx Witness / DW Spectrum needs a password: beyond the server's module " +
                    "information, its REST API answers nothing anonymously.");

            string body = JsonSerializer.Serialize(new
            {
                username = Connection.Username,
                password = Connection.Password,
                setCookie = false,
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(LoginPath, content, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                string reason = NxJson.ErrorString(text);
                throw new NvrException(
                    resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? $"login rejected for '{Connection.Username}': {(int)resp.StatusCode}" +
                          (reason.Length > 0 ? $" {reason}" : "")
                        : $"POST {LoginPath} → {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                          (reason.Length > 0 ? $": {reason}" : ""),
                    text, (int)resp.StatusCode);
            }

            string token = ParseToken(text)
                ?? throw new NvrException("the login reply carried no session token", text);
            _token = token;
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!ReferenceEquals(_downloadHttp, _http))
                _downloadHttp.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
        }
        finally
        {
            _loginGate.Release();
        }
    }

    internal static string? ParseToken(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return NxJson.Str(root, "token")
                ?? (NxJson.Prop(root, "reply") is { } reply ? NxJson.Str(reply, "token") : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> GetTextAsync(string path, CancellationToken ct, bool authenticated = true)
    {
        if (authenticated)
            await EnsureSessionAsync(ct);
        using var resp = await _http.GetAsync(path, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string reason = NxJson.ErrorString(text);
            throw new NvrException(
                $"GET {path} → {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                (reason.Length > 0 ? $": {reason}" : ""),
                text, (int)resp.StatusCode);
        }
        return text;
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct,
        bool authenticated = true)
    {
        string text = await GetTextAsync(path, ct, authenticated);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new NvrException(
                $"GET {path}: non-JSON response (web login page? wrong port? not an Nx server?)",
                text);
        }
    }

    private async Task<string> PatchJsonAsync(string path, JsonNode body, CancellationToken ct)
    {
        await EnsureSessionAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(request, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string reason = NxJson.ErrorString(text);
            throw new NvrException(
                $"PATCH {path} → {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                (reason.Length > 0 ? $": {reason}" : ""),
                text, (int)resp.StatusCode);
        }
        return text;
    }

    // ----- INvrClient -----

    public async Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        // moduleInformation is anonymous, so the session is opened first: this is the call the
        // connectivity probe and the identity guard use to prove the credentials, and an
        // anonymous answer would pass a wrong password off as a healthy login.
        await EnsureSessionAsync(ct);
        using var doc = await GetJsonAsync("/api/moduleInformation", ct, authenticated: false);
        var info = ParseModuleInformation(doc.RootElement);
        _serverId ??= info.SerialNumber;
        return info;
    }

    /// <summary>
    /// <c>reply.id</c> is the media server's own GUID — stable across renames, re-addressing
    /// and upgrades, which is what a pin needs — and doubles as the serial. The model is the
    /// brand ("DW Spectrum Media Server"), since a software recorder has no hardware model.
    /// </summary>
    internal static DeviceInfo ParseModuleInformation(JsonElement root)
    {
        var reply = NxJson.Prop(root, "reply") ?? root;
        string serverName = NxJson.Str(reply, "name") ?? "";
        string systemName = NxJson.Str(reply, "systemName") ?? "";
        string name = serverName.Length > 0 && systemName.Length > 0 &&
                      !serverName.Equals(systemName, StringComparison.OrdinalIgnoreCase)
            ? $"{serverName} ({systemName})"
            : serverName.Length > 0 ? serverName : systemName;
        string brand = BrandName(NxJson.Str(reply, "brand"), NxJson.Str(reply, "customization"));
        string type = NxJson.Str(reply, "type") ?? "Media Server";
        return new DeviceInfo(
            name,
            $"{brand} {type}".Trim(),
            NxJson.StripBraces(NxJson.Str(reply, "id")),
            NxJson.Str(reply, "version") ?? "");
    }

    internal static string BrandName(string? brand, string? customization)
    {
        string key = (string.IsNullOrWhiteSpace(brand) ? customization : brand)?.Trim() ?? "";
        return key.ToLowerInvariant() switch
        {
            "dwspectrum" or "digitalwatchdog" => "DW Spectrum",
            "hdwitness" or "nxwitness" or "default" or "networkoptix" or "" => "Nx Witness",
            "wave" or "hanwha" => "Wisenet WAVE",
            _ => key,
        };
    }

    public async Task<IReadOnlyList<Channel>> GetChannelsAsync(CancellationToken ct = default)
    {
        var cameras = await LoadCamerasAsync(ct);
        return cameras.Select((c, i) => new Channel(i + 1, c.Name, c.Online)).ToList();
    }

    /// <summary>
    /// Reads <c>/rest/v3/devices</c> and numbers the cameras. Every read refreshes the list —
    /// schedules and names change — and records it as what this process last saw for the
    /// address, for the write path's consistency check.
    /// </summary>
    private async Task<IReadOnlyList<NxCamera>> LoadCamerasAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(DevicesPath, ct);
        var cameras = ParseCameras(doc.RootElement);
        string address = DeviceIdentityGuard.AddressOf(Connection);
        if (_cameras is null && LastKnownCameras.TryGetValue(address, out var before))
            _camerasBefore = before;
        _cameras = cameras;
        LastKnownCameras[address] = cameras;
        return cameras;
    }

    /// <summary>Cameras only (no I/O modules), sorted by name then id — channel N is element N−1.</summary>
    internal static IReadOnlyList<NxCamera> ParseCameras(JsonElement root)
    {
        var list = new List<NxCamera>();
        var array = root.ValueKind == JsonValueKind.Array ? root : NxJson.Prop(root, "reply") ?? default;
        if (array.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in array.EnumerateArray())
                if (NxCamera.Parse(e) is { } camera && !camera.IsIoModule)
                    list.Add(camera);
        }
        return list
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<NxCamera> ResolveAsync(int channel, CancellationToken ct)
    {
        var cameras = _cameras ?? await LoadCamerasAsync(ct);
        if (channel < 1 || channel > cameras.Count)
            throw new NvrException(
                $"channel {channel}: this system lists {cameras.Count} camera(s). Channel " +
                "numbers on Nx are positions in the camera list sorted by name — see " +
                "`dvrtool channels`.");
        return cameras[channel - 1];
    }

    /// <summary>
    /// The synchronous resolver for the URL builders: this instance's list, else what the
    /// process last read for the address (the desktop app builds URLs on the client that
    /// listed the channels, so the first branch is the normal one).
    /// </summary>
    private NxCamera ResolveKnown(int channel)
    {
        var cameras = _cameras;
        if (cameras is null)
            LastKnownCameras.TryGetValue(DeviceIdentityGuard.AddressOf(Connection), out cameras);
        if (cameras is null)
            throw new InvalidOperationException(
                "Nx cameras are addressed by id, not number: the camera list has to be read " +
                "(GetChannelsAsync) before a stream URL can be built for a channel number.");
        if (channel < 1 || channel > cameras.Count)
            throw new InvalidOperationException(
                $"channel {channel}: this system lists {cameras.Count} camera(s).");
        return cameras[channel - 1];
    }

    public async Task<IReadOnlyList<RecordingSegment>> SearchAsync(
        int channel, DateTime start, DateTime end, CancellationToken ct = default)
    {
        var camera = await ResolveAsync(channel, ct);
        // detailLevelMs=1: adjacent chunks merge into one period, gaps stay gaps — the
        // recorder's own idea of a "segment". Nx does not say what triggered a period.
        var periods = await GetFootageAsync(camera, ToUnixMs(start), ToUnixMs(end),
            detailLevelMs: 1, ct);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return periods
            .OrderBy(p => p.StartMs)
            .Select(p => new RecordingSegment
            {
                Channel = channel,
                Start = FromUnixMs(p.StartMs),
                End = FromUnixMs(p.OpenEnded ? Math.Max(nowMs, p.StartMs) : p.StartMs + p.DurationMs),
                Type = RecordingType.Unknown,
                NativeId = camera.Id,
            })
            .ToList();
    }

    /// <summary><c>GET /rest/v3/devices/{id}/footage</c> for a window.</summary>
    private async Task<List<NxFootagePeriod>> GetFootageAsync(NxCamera camera, long startMs,
        long endMs, long detailLevelMs, CancellationToken ct)
    {
        string path = $"{DevicesPath}/{camera.Id}/footage?startTimeMs={startMs}" +
                      $"&endTimeMs={endMs}&detailLevelMs={detailLevelMs}";
        using var doc = await GetJsonAsync(path, ct);
        return ParseFootage(doc.RootElement);
    }

    /// <summary>
    /// v3 answers a bare array of <c>{startTimeMs, durationMs}</c>. The numbers may arrive as
    /// strings, older shapes wrap the list in <c>reply</c> or <c>periods</c>, and the legacy
    /// call listed <c>[start, duration]</c> pairs — all accepted.
    /// </summary>
    internal static List<NxFootagePeriod> ParseFootage(JsonElement root)
    {
        var element = root;
        if (element.ValueKind == JsonValueKind.Object)
            element = NxJson.Prop(element, "reply") ?? NxJson.Prop(element, "periods") ?? default;
        var list = new List<NxFootagePeriod>();
        if (element.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in element.EnumerateArray())
        {
            long? start = null, duration = null;
            if (item.ValueKind == JsonValueKind.Object)
            {
                start = NxJson.Int64(item, "startTimeMs") ?? NxJson.Int64(item, "startTime");
                duration = NxJson.Int64(item, "durationMs") ?? NxJson.Int64(item, "duration");
            }
            else if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
            {
                start = NxJson.AsInt64(item[0]);
                duration = NxJson.AsInt64(item[1]);
            }
            if (start is long s)
                list.Add(new NxFootagePeriod(s, duration ?? -1));
        }
        return list;
    }

    public Uri GetLiveUri(int channel, StreamType stream = StreamType.Main,
        bool includeCredentials = false)
    {
        var camera = ResolveKnown(channel);
        return new Uri($"{RtspBase(includeCredentials)}/{camera.Id}?stream={(int)stream}");
    }

    public Uri GetPlaybackUri(int channel, DateTime start, DateTime end,
        StreamType stream = StreamType.Main, bool includeCredentials = false)
    {
        var camera = ResolveKnown(channel);
        return new Uri($"{RtspBase(includeCredentials)}/{camera.Id}" +
                       $"?pos={ToUnixMs(start)}&endPos={ToUnixMs(end)}&stream={(int)stream}");
    }

    private string RtspBase(bool includeCredentials) =>
        $"rtsp://{CredentialPrefix(includeCredentials)}{DeviceAddress.ForUrl(Connection.Host)}:{Connection.RtspPort}";

    private string CredentialPrefix(bool include) => include
        ? $"{Uri.EscapeDataString(Connection.Username)}:{Uri.EscapeDataString(Connection.Password)}@"
        : "";

    public async Task DownloadAsync(int channel, DateTime start, DateTime end,
        string destinationPath, IProgress<long>? bytesProgress = null, CancellationToken ct = default)
    {
        var camera = await ResolveAsync(channel, ct);
        await EnsureSessionAsync(ct);

        // /media/{id}.mkv streams the archive between pos and endPos as Matroska — the one
        // container the server muxes without seeking back to finish an index, so a transfer
        // cut short still plays. Remux to MP4 separately (Remux), as for every vendor.
        string path = $"/media/{camera.Id}.mkv?pos={ToUnixMs(start)}&endPos={ToUnixMs(end)}";
        using var resp = await _downloadHttp.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string errText = await resp.Content.ReadAsStringAsync(ct);
            string reason = NxJson.ErrorString(errText);
            throw new NvrException(
                $"GET {path} → {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                (reason.Length > 0 ? $": {reason}" : ""),
                errText, (int)resp.StatusCode);
        }

        await using var source = await resp.Content.ReadAsStreamAsync(ct);
        long total = await AtomicDownload.WriteAsync(source, destinationPath, bytesProgress, ct);
        if (total == 0)
            throw new NvrException("the server sent an empty export — no footage in that range?");
    }

    public Task DownloadSegmentAsync(RecordingSegment segment, string destinationPath,
        IProgress<long>? bytesProgress = null, CancellationToken ct = default) =>
        DownloadAsync(segment.Channel, segment.Start, segment.End, destinationPath,
            bytesProgress, ct);

    // ----- time -----

    /// <summary>A recorder-local wall-clock time (Kind ignored) → Nx's UTC milliseconds.</summary>
    internal long ToUnixMs(DateTime local)
    {
        var t = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(t, Zone.GetUtcOffset(t)).ToUnixTimeMilliseconds();
    }

    /// <summary>Nx's UTC milliseconds → wall clock in <see cref="Zone"/>, Kind Unspecified like every other vendor's times.</summary>
    internal DateTime FromUnixMs(long ms) => DateTime.SpecifyKind(
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), Zone).DateTime,
        DateTimeKind.Unspecified);

    // ----- lifetime -----

    public void Dispose()
    {
        // Sessions outlive the process unless deleted, and the desktop app opens a client per
        // read — so the session is closed. Dispose is synchronous and often on the UI thread,
        // so the real client does it fire-and-forget on a short-lived HttpClient of its own;
        // the test seam runs it inline through the scripted handler so tests can see it.
        if (_token is { } token)
        {
            string path = $"{LoginPath}/{token}";
            if (_ownsDownloadHttp)
            {
                var conn = Connection;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var closer = NvrHttp.Create(conn, TimeSpan.FromSeconds(5), CreateHandler(conn));
                        closer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        using var resp = await closer.DeleteAsync(path);
                    }
                    catch (Exception)
                    {
                        // Best effort; the server expires idle sessions on its own.
                    }
                });
            }
            else
            {
                try
                {
                    using var resp = _http.DeleteAsync(path).GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // Best effort.
                }
            }
            _token = null;
        }
        _http.Dispose();
        if (_ownsDownloadHttp)
            _downloadHttp.Dispose();
        _loginGate.Dispose();
    }
}
