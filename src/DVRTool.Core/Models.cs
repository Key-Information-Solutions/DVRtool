namespace DVRTool.Core;

public enum Vendor
{
    Hikvision,
    Dahua,
}

/// <summary>
/// Factory port numbers, by vendor. Only the SDK port actually differs — both vendors ship
/// HTTP on 80, HTTPS on 443 and RTSP on 554 — but it differs by a lot, and handing a Dahua
/// recorder Hikvision's 8000 makes the connectivity check report healthy hardware as dead.
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

    /// <summary>The factory SDK port for <paramref name="vendor"/>.</summary>
    public static int Sdk(Vendor vendor) => vendor switch
    {
        Vendor.Dahua => DahuaSdk,
        _ => HikvisionSdk,
    };
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
    /// rather than assumed.
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

    /// <summary>Vendor-native handle: Hikvision playbackURI, Dahua FilePath.</summary>
    public string? NativeId { get; init; }

    public long? SizeBytes { get; init; }

    public TimeSpan Duration => End - Start;
}
