using System.Text;
using DVRTool.Core.Updates;
using Xunit;

namespace DVRTool.Tests;

public class UpdateSigningTests
{
    [Fact]
    public void Signature_verifies_and_a_flipped_byte_does_not()
    {
        var (priv, pub) = UpdateSigning.GenerateKeyPair();
        byte[] message = Encoding.UTF8.GetBytes("{\"version\":\"1.2.3\"}");
        byte[] sig = UpdateSigning.Sign(message, priv);

        Assert.Equal(UpdateSigning.SignatureLength, sig.Length);
        Assert.True(UpdateSigning.Verify(message, sig, pub));

        byte[] tampered = (byte[])message.Clone();
        tampered[^2] ^= 0x01;
        Assert.False(UpdateSigning.Verify(tampered, sig, pub));

        byte[] badSig = (byte[])sig.Clone();
        badSig[10] ^= 0x80;
        Assert.False(UpdateSigning.Verify(message, badSig, pub));
    }

    [Fact]
    public void A_different_key_is_refused()
    {
        var (priv, _) = UpdateSigning.GenerateKeyPair();
        var (_, otherPub) = UpdateSigning.GenerateKeyPair();
        byte[] message = Encoding.UTF8.GetBytes("hello");
        Assert.False(UpdateSigning.Verify(message, UpdateSigning.Sign(message, priv), otherPub));
    }

    [Fact]
    public void Shipped_public_key_is_a_real_key()
    {
        Assert.Equal(32, UpdateSigning.PublicKey.Length);
        Assert.Equal(8, UpdateSigning.KeyId.Length);
        // The shipped key must not sign for anyone here: a random key's signature is refused.
        var (priv, _) = UpdateSigning.GenerateKeyPair();
        byte[] message = Encoding.UTF8.GetBytes("x");
        Assert.False(UpdateSigning.Verify(message, UpdateSigning.Sign(message, priv)));
    }

    [Fact]
    public void Signature_file_accepts_raw_hex_and_base64_only()
    {
        var (priv, _) = UpdateSigning.GenerateKeyPair();
        byte[] sig = UpdateSigning.Sign([1, 2, 3], priv);
        Assert.Equal(sig, UpdateSigning.DecodeSignatureFile(sig));
        Assert.Equal(sig, UpdateSigning.DecodeSignatureFile(Encoding.ASCII.GetBytes(Convert.ToHexString(sig) + "\n")));
        Assert.Equal(sig, UpdateSigning.DecodeSignatureFile(Encoding.ASCII.GetBytes(Convert.ToBase64String(sig))));
        Assert.Null(UpdateSigning.DecodeSignatureFile(Encoding.ASCII.GetBytes("not a signature")));
        Assert.Null(UpdateSigning.DecodeSignatureFile(new byte[63]));
    }

    [Fact]
    public void Wrong_length_inputs_never_throw()
    {
        Assert.False(UpdateSigning.Verify([1], new byte[63], new byte[32]));
        Assert.False(UpdateSigning.Verify([1], new byte[64], new byte[31]));
    }
}
