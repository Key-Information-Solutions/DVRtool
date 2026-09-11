using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace DVRTool.Core.Updates;

/// <summary>
/// Talks to the release channel: finds the latest GitHub Release, fetches and verifies its
/// signed manifest, and downloads the MSI it names — checking size and SHA-256 before handing
/// the path back.
/// </summary>
/// <remarks>
/// The Releases API is the directory, not the authority. Anonymous <c>/releases/latest</c>
/// skips pre-releases and drafts, which is the one-site test path (publish as pre-release,
/// install by hand, then promote). Its 60 requests/hour per address is far above one check a
/// day. Every asset is fetched by the URL the API gave, followed through GitHub's redirect to
/// its object store by HttpClient's default redirect handling. Nothing here elevates or
/// installs; see <see cref="UpdateInstaller"/>.
/// </remarks>
public sealed class UpdateClient : IDisposable
{
    public const string DefaultRepository = "Key-Information-Solutions/DVRtool";

    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly Version _installed;
    private readonly byte[] _trustedKey;

    /// <param name="handler">A test handler; null builds a real one.</param>
    /// <param name="repository">"owner/name" on github.com.</param>
    /// <param name="trustedPublicKey">The key manifests must be signed by; null is the
    /// shipped <see cref="UpdateSigning.PublicKey"/>. Tests pass their own.</param>
    public UpdateClient(Version? installed = null, HttpMessageHandler? handler = null,
        string repository = DefaultRepository, byte[]? trustedPublicKey = null)
    {
        _installed = installed ?? ProductVersion.Current;
        _repository = repository;
        _trustedKey = trustedPublicKey ?? UpdateSigning.PublicKey;
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(30); // the MSI download; the check has its own budget
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("DVRTool", ProductVersion.Format(_installed)));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public Uri LatestReleaseUri => new($"https://api.github.com/repos/{_repository}/releases/latest");

    /// <summary>Where downloaded installers go; one file per version.</summary>
    public static string DownloadDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DVRTool",
        "updates");

    /// <summary>
    /// The full check. Never throws: every failure is an <see cref="UpdateCheck.Failed"/> with
    /// a one-line reason.
    /// </summary>
    public async Task<UpdateCheck> CheckAsync(Version? skipped, CancellationToken ct)
    {
        try
        {
            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkCts.CancelAfter(TimeSpan.FromSeconds(30));
            var token = checkCts.Token;

            var release = await ReadLatestReleaseAsync(token);
            if (release is null)
                return new UpdateCheck.Failed("No release has been published yet.");

            if (!release.Assets.TryGetValue(UpdateManifest.FileNameOnRelease, out var manifestAsset) ||
                !release.Assets.TryGetValue(UpdateManifest.SignatureFileName, out var sigAsset))
                return new UpdateCheck.Failed(
                    $"Release {release.Tag} carries no signed manifest ({UpdateManifest.FileNameOnRelease} + .sig).");

            byte[] manifestBytes = await _http.GetByteArrayAsync(manifestAsset.Url, token);
            byte[] sigBytes = await _http.GetByteArrayAsync(sigAsset.Url, token);
            byte[]? signature = UpdateSigning.DecodeSignatureFile(sigBytes);
            if (signature is null || !UpdateSigning.Verify(manifestBytes, signature, _trustedKey))
                return new UpdateCheck.Failed(
                    $"Release {release.Tag}'s manifest is NOT signed by the DVRTool release key. Not offered.",
                    SignatureRejected: true);

            UpdateManifest manifest;
            try
            {
                manifest = UpdateManifest.Parse(manifestBytes);
            }
            catch (FormatException ex)
            {
                return new UpdateCheck.Failed($"Release {release.Tag}'s manifest is malformed: {ex.Message}");
            }

            if (!release.Assets.TryGetValue(manifest.FileName, out var msiAsset))
                return new UpdateCheck.Failed(
                    $"Release {release.Tag} does not carry the installer its manifest names ({manifest.FileName}).");
            if (msiAsset.Size > 0 && msiAsset.Size != manifest.Size)
                return new UpdateCheck.Failed(
                    $"Release {release.Tag}: installer size on the release ({msiAsset.Size}) differs from the signed manifest ({manifest.Size}).");

            return UpdateDecision.Decide(_installed, manifest, msiAsset.Url, release.HtmlUrl, skipped);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheck.Failed("Timed out reaching github.com (30 s).");
        }
        catch (HttpRequestException ex)
        {
            return new UpdateCheck.Failed(ex.StatusCode is { } code
                ? $"github.com answered {(int)code} {code}."
                : $"Could not reach github.com: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return new UpdateCheck.Failed($"Unexpected answer from the Releases API: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads the installer for <paramref name="update"/> into <see cref="DownloadDirectory"/>
    /// and returns its path once size and SHA-256 match the signed manifest. A mismatch deletes
    /// the file and throws <see cref="UpdateVerificationException"/>. An already-present file
    /// that verifies is reused without a download.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateCheck.Available update, IProgress<long>? progress,
        CancellationToken ct, string? directory = null)
    {
        directory ??= DownloadDirectory;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, update.Manifest.FileName);

        if (File.Exists(path) && await VerifiesAsync(path, update.Manifest, ct))
        {
            progress?.Report(update.Manifest.Size);
            return path;
        }

        using var response = await _http.GetAsync(update.MsiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        long written = await AtomicDownload.WriteAsync(body, path, progress, ct);

        if (written != update.Manifest.Size)
        {
            TryDelete(path);
            throw new UpdateVerificationException(
                $"Downloaded {written} bytes; the signed manifest says {update.Manifest.Size}.");
        }
        if (!await VerifiesAsync(path, update.Manifest, ct))
        {
            TryDelete(path);
            throw new UpdateVerificationException(
                "The downloaded installer's SHA-256 does not match the signed manifest. It was deleted.");
        }
        return path;
    }

    /// <summary>Size and SHA-256 of <paramref name="path"/> against the manifest.</summary>
    public static async Task<bool> VerifiesAsync(string path, UpdateManifest manifest, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != manifest.Size)
                return false;
            await using var stream = File.OpenRead(path);
            byte[] hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hash).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record ReleaseAsset(Uri Url, long Size);

    private sealed record Release(string Tag, Uri HtmlUrl, Dictionary<string, ReleaseAsset> Assets);

    /// <summary>Null when the repo has no published (non-pre, non-draft) release.</summary>
    private async Task<Release?> ReadLatestReleaseAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(LatestReleaseUri, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "?" : "?";
        var html = root.TryGetProperty("html_url", out var h) && Uri.TryCreate(h.GetString(), UriKind.Absolute, out var hu)
            ? hu
            : new Uri($"https://github.com/{_repository}/releases");
        var assets = new Dictionary<string, ReleaseAsset>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;
                if (name is { Length: > 0 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    uri.Scheme == Uri.UriSchemeHttps)
                    assets[name] = new ReleaseAsset(uri, size);
            }
        }
        return new Release(tag, html, assets);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>A downloaded installer did not match its signed manifest.</summary>
public sealed class UpdateVerificationException(string message) : Exception(message);
