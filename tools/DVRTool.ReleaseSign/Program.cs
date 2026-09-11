using System.Security.Cryptography;
using System.Text;
using DVRTool.Core.Updates;

// Release-machine tool: holds the update signing key and produces update.json + update.json.sig
// for one MSI. Never shipped; release.ps1 drives it. See docs/updates.md.

const string Usage = """
    DVRTool.ReleaseSign — the update-manifest signing key and signer

    Usage:
      keygen  [--key <path>]                 create a key pair (refuses to overwrite)
      pubkey  [--key <path>]                 print the public key hex and key id
      sign    --msi <path> --version <x.y.z> --out <dir> [--notes <url>] [--key <path>]
                                             write <dir>\update.json and update.json.sig
      verify  --manifest <path> [--sig <path>] [--pubkey <hex>]
                                             check a manifest against the shipped key

    The private key is DPAPI-protected to the Windows user that created it (like the saved
    device passwords) and lives outside the repo, default:
      %USERPROFILE%\.dvrtool-release\update-signing.key
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return 0;
}

var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (int i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"error: unexpected argument '{args[i]}'");
        return 2;
    }
    string key = args[i][2..];
    string value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
        ? args[++i]
        : "";
    opts[key] = value;
}

string keyPath = opts.GetValueOrDefault("key") is { Length: > 0 } k
    ? k
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dvrtool-release", "update-signing.key");

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "keygen":
        {
            if (File.Exists(keyPath))
            {
                Console.Error.WriteLine($"error: {keyPath} already exists — not overwriting a signing key.");
                return 1;
            }
            var (priv, pub) = UpdateSigning.GenerateKeyPair();
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            File.WriteAllBytes(keyPath, ProtectedData.Protect(priv, null, DataProtectionScope.CurrentUser));
            File.WriteAllText(keyPath + ".pub", Convert.ToHexString(pub).ToLowerInvariant() + "\n");
            CryptographicOperations.ZeroMemory(priv);
            Console.WriteLine($"Private key (DPAPI, this Windows user only): {keyPath}");
            Console.WriteLine($"Public key:  {Convert.ToHexString(pub).ToLowerInvariant()}");
            Console.WriteLine($"Key id:      {UpdateSigning.KeyIdOf(pub)}");
            Console.WriteLine();
            Console.WriteLine("Paste the public key into UpdateSigning.PublicKeyHex (src/DVRTool.Core/Updates/UpdateSigning.cs)");
            Console.WriteLine("and BACK UP the private key file: without it no future release can be signed.");
            return 0;
        }
        case "pubkey":
        {
            byte[] priv = LoadPrivateKey(keyPath);
            var pub = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(priv, 0)
                .GeneratePublicKey().GetEncoded();
            Console.WriteLine(Convert.ToHexString(pub).ToLowerInvariant());
            Console.WriteLine($"key id {UpdateSigning.KeyIdOf(pub)}" +
                              (UpdateSigning.PublicKeyHex.Equals(Convert.ToHexString(pub), StringComparison.OrdinalIgnoreCase)
                                  ? " — matches the key compiled into this build"
                                  : " — DOES NOT match UpdateSigning.PublicKeyHex in this build"));
            return 0;
        }
        case "sign":
        {
            string msi = Require(opts, "msi");
            string versionText = Require(opts, "version");
            string outDir = Require(opts, "out");
            if (!ProductVersion.TryParse(versionText, out var version))
                throw new ArgumentException($"--version '{versionText}' is not numeric x.y.z");
            if (!File.Exists(msi))
                throw new FileNotFoundException("MSI not found", msi);

            byte[] priv = LoadPrivateKey(keyPath);
            var pub = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(priv, 0)
                .GeneratePublicKey().GetEncoded();
            if (!UpdateSigning.PublicKeyHex.Equals(Convert.ToHexString(pub), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The private key at " + keyPath + " does not match UpdateSigning.PublicKeyHex compiled into " +
                    "this tool. A manifest signed with it would be rejected by every installed DVRTool.");

            byte[] hash;
            using (var stream = File.OpenRead(msi))
                hash = SHA256.HashData(stream);
            var manifest = new UpdateManifest(
                ProductVersion.Format(version),
                Path.GetFileName(msi),
                new FileInfo(msi).Length,
                Convert.ToHexString(hash).ToLowerInvariant(),
                opts.GetValueOrDefault("notes") is { Length: > 0 } n ? n : null,
                DateTimeOffset.UtcNow,
                MinimumVersion: null,
                KeyId: UpdateSigning.KeyIdOf(pub));

            byte[] json = manifest.ToJsonBytes();
            byte[] sig = UpdateSigning.Sign(json, priv);
            CryptographicOperations.ZeroMemory(priv);
            if (!UpdateSigning.Verify(json, sig, pub))
                throw new InvalidOperationException("Self-check failed: signature did not verify.");
            _ = UpdateManifest.Parse(json); // round-trips

            Directory.CreateDirectory(outDir);
            string manifestPath = Path.Combine(outDir, UpdateManifest.FileNameOnRelease);
            string sigPath = Path.Combine(outDir, UpdateManifest.SignatureFileName);
            File.WriteAllBytes(manifestPath, json);
            File.WriteAllText(sigPath, Convert.ToBase64String(sig) + "\n");
            Console.WriteLine(manifestPath);
            Console.WriteLine(sigPath);
            Console.WriteLine(Encoding.UTF8.GetString(json));
            return 0;
        }
        case "verify":
        {
            string manifestPath = Require(opts, "manifest");
            string sigPath = opts.GetValueOrDefault("sig") is { Length: > 0 } s ? s : manifestPath + ".sig";
            byte[] pub = opts.GetValueOrDefault("pubkey") is { Length: > 0 } hex
                ? Convert.FromHexString(hex)
                : UpdateSigning.PublicKey;
            byte[] json = File.ReadAllBytes(manifestPath);
            byte[]? sig = UpdateSigning.DecodeSignatureFile(File.ReadAllBytes(sigPath));
            bool ok = sig is not null && UpdateSigning.Verify(json, sig, pub);
            Console.WriteLine(ok ? "OK: signature verifies" : "FAIL: signature does not verify");
            if (ok)
            {
                var m = UpdateManifest.Parse(json);
                Console.WriteLine($"  version {m.VersionText}  file {m.FileName}  size {m.Size}  sha256 {m.Sha256}");
            }
            return ok ? 0 : 1;
        }
        default:
            Console.Error.WriteLine($"error: unknown command '{args[0]}'\n");
            Console.WriteLine(Usage);
            return 2;
    }
}
catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException
                               or CryptographicException or FormatException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static string Require(Dictionary<string, string> opts, string name) =>
    opts.GetValueOrDefault(name) is { Length: > 0 } v
        ? v
        : throw new ArgumentException($"--{name} is required");

static byte[] LoadPrivateKey(string path)
{
    if (!File.Exists(path))
        throw new FileNotFoundException($"No signing key at {path}. Run `keygen` (once) or pass --key.", path);
    byte[] priv = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
    if (priv.Length != UpdateSigning.KeyLength)
        throw new InvalidOperationException("Key file is not a 32-byte Ed25519 private key.");
    return priv;
}
