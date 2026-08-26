using System.Globalization;

namespace DVRTool.Vendors.HikvisionIvms;

/// <summary>
/// Decodes iVMS's <c>Card.CardNo</c> field into the plaintext fob number the panels use.
/// </summary>
/// <remarks>
/// <para>
/// iVMS stores each card number as base64 of <b>8 bytes</b> under a per-decimal-digit
/// substitution — <em>not</em> a block cipher (AES/DES) and not XOR: each output byte is a
/// fixed function of a single decimal digit of the number, plus a constant frame. The layout
/// (little end last) is:
/// </para>
/// <list type="table">
///   <item><term>b0,b1</term><description>thousands digit (0–9)</description></item>
///   <item><term>b2</term><description>hundreds digit</description></item>
///   <item><term>b3</term><description>tens digit</description></item>
///   <item><term>b4,b5</term><description>units digit</description></item>
///   <item><term>b6</term><description>0x16 for a ≤4-digit fob; other values mark a
///     high/5-digit variant this decoder does not attempt</description></item>
///   <item><term>b7</term><description>0x98, a constant tag</description></item>
/// </list>
/// <para>
/// The substitution tables below are <b>codec tables, not secrets</b>: they are app-global
/// (identical across every iVMS/NVMS install, like a base64 alphabet), so they are baked in
/// here the same way any decoder bakes in its format. The per-install <em>secret</em> — the
/// SQLCipher DB key — is never hardcoded; it stays operator-supplied via
/// <see cref="IvmsKeyStore"/>. See <c>docs/ivms-integration-findings.md</c> for how the tables
/// were recovered and validated (149/149 against the live panel fob set).
/// </para>
/// <para>
/// <b>Fail-safe by design.</b> <see cref="TryDecode"/> returns <c>false</c> — never a
/// best-guess number — for any input it cannot decode exactly (wrong length, unexpected
/// frame byte, or a byte outside the tables). A wrong fob would revoke the wrong door, so an
/// unrecognised value must surface as "unknown", not as a plausible-looking mistake.
/// </para>
/// </remarks>
public static class IvmsCardCipher
{
    /// <summary>b6 value for the plain ≤4-digit layout; the only variant decoded here.</summary>
    private const byte Tag4Digit = 0x16;

    /// <summary>b7 is a constant frame byte on every card; a mismatch means "not this format".</summary>
    private const byte ConstFrame = 0x98;

    // Forward tables: decimal digit -> encoded byte(s). Recovered and validated in
    // docs/ivms-integration-findings.md. Kept as the spec; inverses are built once below.
    private static readonly byte[] Hundreds =
        [0xB6, 0xB2, 0xBE, 0xBA, 0xA6, 0xA2, 0xAE, 0x94, 0x90, 0x9C];   // b2
    private static readonly byte[] Tens =
        [0xD7, 0xD8, 0xD9, 0xDA, 0x90, 0x91, 0x92, 0x93, 0x94, 0x95];   // b3
    private static readonly (byte B4, byte B5)[] Units =
    [
        (0x1E, 0xC2), (0x1E, 0xD2), (0x1E, 0xE4), (0x1E, 0xF4), (0x1D, 0xC2),
        (0x1D, 0xD2), (0x1D, 0xE4), (0x1D, 0xF4), (0x1C, 0xC2), (0x1C, 0xD2),
    ];                                                                   // b4,b5
    private static readonly (byte B0, byte B1)[] Thousands =
    [
        (0xBC, 0x6A), (0xBC, 0x7A), (0xBC, 0x44), (0xBC, 0x54), (0xBF, 0x6A),
        (0xBF, 0x7A), (0xBF, 0x44), (0xBF, 0x54), (0xBE, 0x6A), (0xBE, 0x7A),
    ];                                                                   // b0,b1

    private static readonly Dictionary<byte, int> InvHundreds = Invert(Hundreds);
    private static readonly Dictionary<byte, int> InvTens = Invert(Tens);
    private static readonly Dictionary<(byte, byte), int> InvUnits = InvertPairs(Units);
    private static readonly Dictionary<(byte, byte), int> InvThousands = InvertPairs(Thousands);

    /// <summary>
    /// Decodes an iVMS <c>Card.CardNo</c> (base64) into the plaintext fob number, as the
    /// decimal string the panels use (no leading zeros — the roster normalises those away).
    /// Returns <c>false</c>, leaving <paramref name="fob"/> empty, for anything it cannot
    /// decode exactly.
    /// </summary>
    public static bool TryDecode(string? base64CardNo, out string fob)
    {
        fob = "";
        if (string.IsNullOrWhiteSpace(base64CardNo))
            return false;

        byte[] b;
        try
        {
            b = Convert.FromBase64String(base64CardNo.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        if (b.Length != 8 || b[7] != ConstFrame || b[6] != Tag4Digit)
            return false;

        if (!InvThousands.TryGetValue((b[0], b[1]), out int thousands) ||
            !InvHundreds.TryGetValue(b[2], out int hundreds) ||
            !InvTens.TryGetValue(b[3], out int tens) ||
            !InvUnits.TryGetValue((b[4], b[5]), out int units))
            return false;

        int number = (thousands * 1000) + (hundreds * 100) + (tens * 10) + units;
        fob = number.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static Dictionary<byte, int> Invert(byte[] table)
    {
        var inverse = new Dictionary<byte, int>(table.Length);
        for (int digit = 0; digit < table.Length; digit++)
            inverse[table[digit]] = digit;
        return inverse;
    }

    private static Dictionary<(byte, byte), int> InvertPairs((byte, byte)[] table)
    {
        var inverse = new Dictionary<(byte, byte), int>(table.Length);
        for (int digit = 0; digit < table.Length; digit++)
            inverse[table[digit]] = digit;
        return inverse;
    }
}
