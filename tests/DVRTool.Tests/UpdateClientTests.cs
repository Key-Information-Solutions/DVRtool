using System.Net;
using System.Security.Cryptography;
using System.Text;
using DVRTool.Core.Updates;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The release channel faked end to end: Releases API → manifest + signature → MSI bytes.
/// The keys are generated per test, so the manifest is signed by a key that is NOT the
/// shipped one — which is exactly the case the client must refuse — and the accept path
/// therefore goes through the client with the test key supplied.
/// </summary>
public class UpdateClientTests
{
    private const string Repo = "Key-Information-Solutions/DVRtool";
    private static readonly byte[] MsiBytes = Encoding.ASCII.GetBytes("MSI-BYTES-" + new string('x', 5000));

    private sealed class Channel
    {
        public byte[] Priv, Pub;
        public byte[] ManifestJson = [];
        public byte[] Sig = [];
        public byte[] Msi = MsiBytes;
        public string Version = "1.1.0";
        public bool SigMissing;
        public long? ReleaseMsiSizeOverride;
        public HttpStatusCode ApiStatus = HttpStatusCode.OK;

        public Channel()
        {
            (Priv, Pub) = UpdateSigning.GenerateKeyPair();
            Rebuild();
        }

        public void Rebuild()
        {
            var manifest = new UpdateManifest(Version, $"DVRTool-{Version}.msi", Msi.Length,
                Convert.ToHexString(SHA256.HashData(Msi)).ToLowerInvariant(),
                $"https://github.com/{Repo}/releases/tag/v{Version}", DateTimeOffset.UtcNow, null,
                UpdateSigning.KeyIdOf(Pub));
            ManifestJson = manifest.ToJsonBytes();
            Sig = UpdateSigning.Sign(ManifestJson, Priv);
        }

        private string Api => $$"""
            {
              "tag_name": "v{{Version}}",
              "html_url": "https://github.com/{{Repo}}/releases/tag/v{{Version}}",
              "prerelease": false,
              "assets": [
                { "name": "DVRTool-{{Version}}.msi", "size": {{ReleaseMsiSizeOverride ?? Msi.Length}},
                  "browser_download_url": "https://github.com/{{Repo}}/releases/download/v{{Version}}/DVRTool-{{Version}}.msi" },
                { "name": "update.json", "size": {{ManifestJson.Length}},
                  "browser_download_url": "https://github.com/{{Repo}}/releases/download/v{{Version}}/update.json" }
                {{(SigMissing ? "" : $$$""", { "name": "update.json.sig", "size": 89, "browser_download_url": "https://github.com/{{{Repo}}}/releases/download/v{{{Version}}}/update.json.sig" }""")}}
              ]
            }
            """;

        public HttpResponseMessage Respond(HttpRequestMessage req, string _)
        {
            string path = req.RequestUri!.AbsolutePath;
            if (req.RequestUri.Host == "api.github.com")
                return new HttpResponseMessage(ApiStatus)
                {
                    Content = new StringContent(Api, Encoding.UTF8, "application/json"),
                };
            if (path.EndsWith("/update.json"))
                return Bytes(ManifestJson);
            if (path.EndsWith("/update.json.sig"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Convert.ToBase64String(Sig) + "\n"),
                };
            if (path.EndsWith(".msi"))
                return Bytes(Msi);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Bytes(byte[] b) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
    }

    /// <summary>A client over the fake channel; trusts the shipped key unless told otherwise.</summary>
    private sealed class TestClient(Channel ch, Version installed, bool trustChannelKey = false)
    {
        public readonly MockHttpHandler Handler = new(ch.Respond);
        public UpdateClient Client => new(installed, Handler, Repo, trustChannelKey ? ch.Pub : null);
    }

    [Fact]
    public async Task Newer_signed_release_is_offered_with_its_installer_url()
    {
        var ch = new Channel();
        var test = new TestClient(ch, new Version(1, 0, 0), trustChannelKey: true);
        using var client = test.Client;
        var result = await client.CheckAsync(null, CancellationToken.None);
        var available = Assert.IsType<UpdateCheck.Available>(result);
        Assert.Equal(new Version(1, 1, 0), available.Manifest.Version);
        Assert.EndsWith("/DVRTool-1.1.0.msi", available.MsiUrl.AbsolutePath);
        Assert.Contains("/releases/tag/v1.1.0", available.ReleaseUrl.ToString());
        // The check downloads the two small assets, never the MSI.
        Assert.DoesNotContain(test.Handler.Requests, r => r.Request.RequestUri!.AbsolutePath.EndsWith(".msi"));
        Assert.All(test.Handler.Requests, r => Assert.Contains("DVRTool/1.0.0", r.Request.Headers.UserAgent.ToString()));
    }

    [Fact]
    public async Task Same_version_is_up_to_date_and_a_skipped_one_is_skipped()
    {
        var ch = new Channel();
        using var client = new TestClient(ch, new Version(1, 1, 0), trustChannelKey: true).Client;
        Assert.IsType<UpdateCheck.UpToDate>(await client.CheckAsync(null, CancellationToken.None));

        using var older = new TestClient(ch, new Version(1, 0, 0), trustChannelKey: true).Client;
        Assert.IsType<UpdateCheck.Skipped>(await older.CheckAsync(new Version(1, 1, 0), CancellationToken.None));
    }

    [Fact]
    public async Task Tampered_manifest_after_signing_is_refused()
    {
        var ch = new Channel();
        ch.ManifestJson = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(ch.ManifestJson).Replace("\"version\": \"1.1.0\"", "\"version\": \"9.9.9\""));
        using var client = new TestClient(ch, new Version(1, 0, 0), trustChannelKey: true).Client;
        var failed = Assert.IsType<UpdateCheck.Failed>(await client.CheckAsync(null, CancellationToken.None));
        Assert.True(failed.SignatureRejected);
    }

    [Fact]
    public async Task Installer_size_on_release_must_match_the_signed_manifest()
    {
        var ch = new Channel();
        ch.ReleaseMsiSizeOverride = ch.Msi.Length + 1;
        using var client = new TestClient(ch, new Version(1, 0, 0), trustChannelKey: true).Client;
        var failed = Assert.IsType<UpdateCheck.Failed>(await client.CheckAsync(null, CancellationToken.None));
        Assert.Contains("size", failed.Reason);
    }

    [Fact]
    public async Task Manifest_signed_by_another_key_is_refused_even_when_everything_else_is_right()
    {
        var ch = new Channel();
        using var client = new TestClient(ch, new Version(1, 0, 0)).Client;
        var result = await client.CheckAsync(null, CancellationToken.None);
        var failed = Assert.IsType<UpdateCheck.Failed>(result);
        Assert.True(failed.SignatureRejected);
        Assert.Contains("NOT signed", failed.Reason);
    }

    [Fact]
    public async Task Missing_signature_asset_is_a_plain_failure_and_never_an_offer()
    {
        var ch = new Channel { SigMissing = true };
        using var client = new TestClient(ch, new Version(1, 0, 0)).Client;
        var result = await client.CheckAsync(null, CancellationToken.None);
        var failed = Assert.IsType<UpdateCheck.Failed>(result);
        Assert.Contains("no signed manifest", failed.Reason);
    }

    [Fact]
    public async Task Api_errors_are_one_line_failures()
    {
        var ch = new Channel { ApiStatus = HttpStatusCode.Forbidden };
        using var client = new TestClient(ch, new Version(1, 0, 0)).Client;
        var failed = Assert.IsType<UpdateCheck.Failed>(await client.CheckAsync(null, CancellationToken.None));
        Assert.Contains("403", failed.Reason);

        ch.ApiStatus = HttpStatusCode.NotFound;
        Assert.Contains("No release", Assert.IsType<UpdateCheck.Failed>(
            await client.CheckAsync(null, CancellationToken.None)).Reason);
    }

    [Fact]
    public async Task Download_verifies_size_and_hash_and_deletes_a_wrong_body()
    {
        var ch = new Channel();
        var manifest = UpdateManifest.Parse(ch.ManifestJson);
        var available = new UpdateCheck.Available(new Version(1, 0, 0), manifest,
            new Uri($"https://github.com/{Repo}/releases/download/v1.1.0/DVRTool-1.1.0.msi"),
            new Uri($"https://github.com/{Repo}/releases/tag/v1.1.0"));
        string dir = Path.Combine(Path.GetTempPath(), "dvrtool-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new TestClient(ch, new Version(1, 0, 0)).Client;
            long last = 0;
            var progress = new SynchronousProgress(v => last = v);
            string path = await client.DownloadAsync(available, progress, CancellationToken.None, dir);
            Assert.Equal(MsiBytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(MsiBytes.Length, last);

            // Same file again: reused, no second download.
            string again = await client.DownloadAsync(available, null, CancellationToken.None, dir);
            Assert.Equal(path, again);

            // Serve a body that does not match the signed manifest.
            File.Delete(path);
            ch.Msi = Encoding.ASCII.GetBytes("MSI-BYTES-" + new string('y', 5000)); // same size, other hash
            var ex = await Assert.ThrowsAsync<UpdateVerificationException>(
                () => client.DownloadAsync(available, null, CancellationToken.None, dir));
            Assert.Contains("SHA-256", ex.Message);
            Assert.False(File.Exists(path));

            ch.Msi = Encoding.ASCII.GetBytes("short");
            ex = await Assert.ThrowsAsync<UpdateVerificationException>(
                () => client.DownloadAsync(available, null, CancellationToken.None, dir));
            Assert.Contains("bytes", ex.Message);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
