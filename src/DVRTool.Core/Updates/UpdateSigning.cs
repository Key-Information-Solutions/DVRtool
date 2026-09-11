using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace DVRTool.Core.Updates;

/// <summary>
/// Ed25519 over the update manifest. The public key ships inside the app; the private key
/// lives on the release machine and nowhere else (<c>tools/DVRTool.ReleaseSign</c>).
/// </summary>
/// <remarks>
/// This is the trust root for updates. GitHub is where releases are fetched from, not why they
/// are believed: a release whose manifest does not verify against <see cref="PublicKey"/> is
/// never offered, whatever host it came from and whatever TLS said. <c>keyId</c> in the
/// manifest is the first 8 hex digits of the public key, so a future rotation can ship a
/// second key and tell them apart; only one key is accepted today.
/// </remarks>
public static class UpdateSigning
{
    /// <summary>The release signing public key, 32 bytes, hex (key id a68b6dde, generated 2026-09-11).</summary>
    public const string PublicKeyHex = "a68b6ddeabc8219ad8f6e29fdb0fa4bb25c49607ba41e28e24460af070e70b70";

    public static byte[] PublicKey => Convert.FromHexString(PublicKeyHex);

    public static string KeyId => KeyIdOf(PublicKey);

    public static string KeyIdOf(ReadOnlySpan<byte> publicKey) =>
        Convert.ToHexString(publicKey[..4]).ToLowerInvariant();

    public const int SignatureLength = 64;
    public const int KeyLength = 32;

    /// <summary>True only if <paramref name="signature"/> is a valid signature over
    /// <paramref name="message"/> by the shipped key. Never throws.</summary>
    public static bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature) =>
        Verify(message, signature, PublicKey);

    public static bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> publicKey)
    {
        if (signature.Length != SignatureLength || publicKey.Length != KeyLength)
            return false;
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey.ToArray(), 0));
            byte[] msg = message.ToArray();
            verifier.BlockUpdate(msg, 0, msg.Length);
            return verifier.VerifySignature(signature.ToArray());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Signs; only the release tool calls this.</summary>
    public static byte[] Sign(ReadOnlySpan<byte> message, ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != KeyLength)
            throw new ArgumentException("Ed25519 private key must be 32 bytes.", nameof(privateKey));
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey.ToArray(), 0));
        byte[] msg = message.ToArray();
        signer.BlockUpdate(msg, 0, msg.Length);
        return signer.GenerateSignature();
    }

    /// <summary>A fresh key pair: (private 32 bytes, public 32 bytes).</summary>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var priv = new Ed25519PrivateKeyParameters(new SecureRandom());
        return (priv.GetEncoded(), priv.GeneratePublicKey().GetEncoded());
    }

    /// <summary>
    /// The .sig file's content: the raw 64 bytes, or their hex or base64 text. Null when it is
    /// none of those.
    /// </summary>
    public static byte[]? DecodeSignatureFile(ReadOnlySpan<byte> content)
    {
        if (content.Length == SignatureLength)
            return content.ToArray();
        string text = Encoding.ASCII.GetString(content).Trim();
        if (text.Length == SignatureLength * 2 && text.All(Uri.IsHexDigit))
            return Convert.FromHexString(text);
        try
        {
            byte[] b64 = Convert.FromBase64String(text);
            return b64.Length == SignatureLength ? b64 : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
