using DVRTool.Vendors.HikvisionIvms;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The iVMS <c>Card.CardNo</c> decoder. The vectors are encoded forms of plain fob numbers
/// (public door-credential numbers, not secrets); the recovery and full 149/149 validation
/// live in docs/ivms-integration-findings.md.
/// </summary>
public class IvmsCardCipherTests
{
    [Theory]
    [InlineData("vHq6kh3kFpg=", "1366")]  // spans thousands..units
    [InlineData("vnqQkR70Fpg=", "9853")]
    [InlineData("vES6khzCFpg=", "2368")]
    [InlineData("v1SUkx7CFpg=", "7770")]
    [InlineData("v3qckxzCFpg=", "5978")]
    [InlineData("vGqQkh70Fpg=", "863")]   // 3-digit
    public void DecodesKnownFobs(string encoded, string expected)
    {
        Assert.True(IvmsCardCipher.TryDecode(encoded, out string fob));
        Assert.Equal(expected, fob);
    }

    [Fact]
    public void DropsLeadingZerosSoItJoinsTheRoster()
    {
        // Stored as "0109" on the panel; decode yields the normalized integer form.
        Assert.True(IvmsCardCipher.TryDecode("vGqy1xzSFpg=", out string fob));
        Assert.Equal("109", fob);
    }

    [Fact]
    public void RefusesTheHighFiveDigitVariant()
    {
        // b6 = 0x62 marks the high/5-digit layout this decoder does not attempt.
        string encoded = System.Convert.ToBase64String(
            [0xBC, 0x44, 0xB2, 0x92, 0x1D, 0xE9, 0x62, 0x98]);
        Assert.False(IvmsCardCipher.TryDecode(encoded, out string fob));
        Assert.Equal("", fob);
    }

    [Fact]
    public void RefusesAnUnmappedMiddleByteRatherThanGuessing()
    {
        // Valid frame (b6/b7) but b2 (hundreds) is a byte no digit maps to.
        string encoded = System.Convert.ToBase64String(
            [0xBC, 0x7A, 0x00, 0x92, 0x1D, 0xE4, 0x16, 0x98]);
        Assert.False(IvmsCardCipher.TryDecode(encoded, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 @@@")]
    [InlineData("YWJj")]              // valid base64 but only 3 bytes
    public void RefusesJunkInput(string? encoded)
    {
        Assert.False(IvmsCardCipher.TryDecode(encoded, out string fob));
        Assert.Equal("", fob);
    }
}
