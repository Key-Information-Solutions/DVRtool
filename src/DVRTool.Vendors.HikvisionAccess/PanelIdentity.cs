using System.Text.RegularExpressions;

namespace DVRTool.Vendors.HikvisionAccess;

/// <summary>
/// Reads model, firmware and door count out of a controller's serial number.
/// </summary>
/// <remarks>
/// <para>
/// This exists because DS-K2604 V2.0 firmware refuses the SDK capability-set query
/// (<c>NET_DVR_GetDeviceAbility</c> with <c>ACS_ABILITY</c>) outright, and
/// <c>NET_DVR_DEVICEINFO_V30</c> carries no model string for access controllers — the
/// serial is the only self-description these panels offer.
/// </para>
/// <para>
/// Pure string parsing, deliberately kept off the Windows-only SDK client so it can be
/// tested (and reasoned about) without the native library.
/// </para>
/// </remarks>
internal static class PanelIdentity
{
    /// <summary>
    /// DS-K serials read <c>BRAND-MODEL yyyyMMdd V<i>version</i> ...</c>, e.g.
    /// <c>OCB-K260420180706V020004ENC00000001</c> — an OCB-badged DS-K2604 built
    /// 2018-07-06 running 2.0.4.
    /// </summary>
    private static readonly Regex SerialPattern = new(
        @"^(?<brand>[A-Za-z]+)-(?<model>K\d+?)(?<built>\d{8})V(?<ver>\d{6})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The underlying Hikvision model, with the OEM badge noted.</summary>
    internal static string ParseModel(string serial)
    {
        var m = SerialPattern.Match(serial);
        if (!m.Success)
            return serial.Length > 0 ? serial : "unknown";
        return $"DS-{m.Groups["model"].Value} ({m.Groups["brand"].Value} OEM)";
    }

    /// <summary>
    /// Firmware version from the serial's <c>V</c> field: three 2-digit groups, so
    /// <c>V020004</c> is 2.0.4.
    /// </summary>
    /// <remarks>
    /// Rendered from the decoded integers rather than the raw digits. iVMS-4200 displays the
    /// same firmware zero-padded ("V2.0.004"); the full serial is reported alongside this so
    /// the encoded form is never lost.
    /// </remarks>
    internal static string ParseFirmware(string serial)
    {
        var m = SerialPattern.Match(serial);
        if (!m.Success)
            return "";
        string v = m.Groups["ver"].Value;
        return $"V{int.Parse(v[..2])}.{int.Parse(v[2..4])}.{int.Parse(v[4..6])}";
    }

    /// <summary>
    /// Doors the controller drives, from the last two digits of the model number: a
    /// DS-K2604 is 4-door, a DS-K2602 2-door. A hint, not a reading — null when the model
    /// does not follow the pattern.
    /// </summary>
    internal static int? ParseDoorCount(string serial)
    {
        var m = SerialPattern.Match(serial);
        if (!m.Success)
            return null;
        string model = m.Groups["model"].Value;      // e.g. K2604
        return model.Length >= 5 && int.TryParse(model[^2..], out int doors) && doors > 0
            ? doors
            : null;
    }
}
