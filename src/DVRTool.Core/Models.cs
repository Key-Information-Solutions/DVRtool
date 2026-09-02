namespace DVRTool.Core;

public enum Vendor
{
    Hikvision,
    Dahua,

    /// <summary>
    /// Network Optix's VMS and its OEM builds — DW Spectrum (Digital Watchdog), Wisenet WAVE,
    /// and Nx Witness itself. A software recorder rather than an appliance: one port (7001
    /// from the factory) carries HTTPS, HTTP and RTSP together, and there is no vendor SDK
    /// port at all.
    /// </summary>
    NxWitness,
}

/// <summary>
/// The strings a vendor is spelled as — in <c>devices.json</c>, on <c>--vendor</c>, and in
/// the desktop dialog — kept in one place so the two front ends cannot drift apart.
/// </summary>
public static class VendorNames
{
    /// <summary>What <c>--vendor</c> accepts, for usage text.</summary>
    public const string CliChoices = "hikvision|dahua|nx";

    /// <summary>The canonical stored spelling: what a saved device and a script use.</summary>
    public static string Key(Vendor vendor) => vendor switch
    {
        Vendor.Dahua => "dahua",
        Vendor.NxWitness => "nx",
        _ => "hikvision",
    };

    /// <summary>The operator-facing label, as the desktop dialog lists it.</summary>
    public static string Display(Vendor vendor) => vendor switch
    {
        Vendor.Dahua => "Dahua / Amcrest",
        Vendor.NxWitness => "DW Spectrum / Nx Witness",
        _ => "Hikvision / LTS",
    };

    /// <summary>
    /// Parses any accepted spelling — the canonical key, the OEM brands an installer is
    /// likely to type, and the old GUI spellings. Case-insensitive.
    /// </summary>
    public static bool TryParse(string? text, out Vendor vendor)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "hikvision" or "hik" or "lts":
                vendor = Vendor.Hikvision;
                return true;
            case "dahua" or "amcrest":
                vendor = Vendor.Dahua;
                return true;
            case "nx" or "nxwitness" or "nx-witness" or "nx_witness" or "dw" or "dwspectrum"
                or "dw-spectrum" or "dw_spectrum" or "spectrum" or "wave":
                vendor = Vendor.NxWitness;
                return true;
            default:
                vendor = Vendor.Hikvision;
                return false;
        }
    }
}

/// <summary>
/// Factory port numbers, by vendor. Hikvision and Dahua ship HTTP on 80, HTTPS on 443 and
/// RTSP on 554 and disagree only on the SDK port — but they disagree by a lot, and handing a
/// Dahua recorder Hikvision's 8000 makes the connectivity check report healthy hardware as
/// dead. Nx Witness / DW Spectrum is different again: everything is on 7001 and there is
/// no SDK port.
/// </summary>
public static class VendorPorts
{
    /// <summary>Hikvision's "Server Port": where HCNetSDK answers.</summary>
    public const int HikvisionSdk = 8000;

    /// <summary>
    /// Dahua's "TCP Port": where DHNetSDK answers, and what SmartPSS and DSS connect on.
    /// </summary>
    /// <remarks>
    /// Dahua publishes a second SDK port alongside it — "UDP Port", 37778 by default — for
    /// the same SDK's datagram login mode and for broadcast device discovery. DVRTool drives
    /// Dahua over HTTP CGI and RTSP and uses neither, so it is deliberately not carried; see
    /// <c>docs/device-ports.md</c>.
    /// </remarks>
    public const int DahuaSdk = 37777;

    /// <summary>
    /// The Nx Witness / DW Spectrum media server port. One listener sniffs the protocol, so
    /// HTTPS, plain HTTP and RTSP all arrive here — the web port and the RTSP port of an Nx
    /// record are the same number.
    /// </summary>
    public const int NxWitnessServer = 7001;

    /// <summary>
    /// False for a vendor with no SDK port at all (Nx). Such a record carries
    /// <see cref="Sdk"/> = 0, and nothing may dial or probe it.
    /// </summary>
    public static bool HasSdkPort(Vendor vendor) => vendor != Vendor.NxWitness;

    /// <summary>The factory SDK port for <paramref name="vendor"/>; 0 when it has none.</summary>
    public static int Sdk(Vendor vendor) => vendor switch
    {
        Vendor.Dahua => DahuaSdk,
        Vendor.NxWitness => 0,
        _ => HikvisionSdk,
    };

    /// <summary>The factory web (HTTP API) port for <paramref name="vendor"/>.</summary>
    public static int Web(Vendor vendor, bool tls) =>
        vendor == Vendor.NxWitness ? NxWitnessServer : tls ? 443 : 80;

    /// <summary>The factory RTSP port for <paramref name="vendor"/>.</summary>
    public static int Rtsp(Vendor vendor) => vendor == Vendor.NxWitness ? NxWitnessServer : 554;

    /// <summary>
    /// True when the vendor's API is HTTPS from the factory. Nx serves its REST API over TLS
    /// with a self-signed certificate (pinned trust-on-first-use, like every other vendor's
    /// HTTPS); Hikvision and Dahua default to plain HTTP.
    /// </summary>
    public static bool DefaultsToTls(Vendor vendor) => vendor == Vendor.NxWitness;
}

public enum StreamType
{
    Main = 0,
    Sub = 1,
}

public enum RecordingType
{
    Unknown,
    Continuous,
    Motion,
    Alarm,
    Event,
    Manual,
}

/// <summary>Connection settings for one NVR.</summary>
public sealed record NvrConnection
{
    public required string Host { get; init; }
    public int HttpPort { get; init; } = 80;
    public int RtspPort { get; init; } = 554;

    /// <summary>
    /// The vendor's private SDK port (Hikvision HCNetSDK, Dahua DHNetSDK). Operators can
    /// and do change it from the device's own network menu, so it is carried per system
    /// rather than assumed. 0 on a vendor that has no such port (Nx Witness / DW Spectrum).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing on the HTTP/RTSP paths reads this: it exists for the SDK transports, which
    /// are the only way to reach some features (see
    /// <see cref="AccessPanelConnection.SdkPort"/> for the same field on door panels).
    /// </para>
    /// <para>
    /// The default here is <em>Hikvision's</em> — the vendors do not agree on this port, and
    /// a record cannot pick for itself because it does not carry a vendor. Anything building
    /// a Dahua connection must set this from <see cref="VendorPorts.Sdk"/>; leaving it at the
    /// default points the port check at 8000, which means nothing on a Dahua recorder.
    /// </para>
    /// </remarks>
    public int SdkPort { get; init; } = VendorPorts.HikvisionSdk;

    public required string Username { get; init; }
    public required string Password { get; init; }
    public bool UseTls { get; init; }

    public string HttpBase => $"{(UseTls ? "https" : "http")}://{Host}:{HttpPort}";
}

public sealed record DeviceInfo(string Name, string Model, string SerialNumber, string FirmwareVersion);

/// <summary>A video channel, identified by its 1-based display number.</summary>
public sealed record Channel(int Id, string Name, bool? Online = null);

/// <summary>
/// One recorded clip on the NVR's disk. Times are NVR-local wall-clock
/// (Kind = Unspecified) — exactly what the device reported.
/// </summary>
public sealed record RecordingSegment
{
    public required int Channel { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public RecordingType Type { get; init; } = RecordingType.Unknown;

    /// <summary>Vendor-native handle: Hikvision playbackURI, Dahua FilePath, Nx device id.</summary>
    public string? NativeId { get; init; }

    public long? SizeBytes { get; init; }

    public TimeSpan Duration => End - Start;
}
