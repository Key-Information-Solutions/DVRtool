namespace DVRTool.Core;

public enum Vendor
{
    Hikvision,
    Dahua,
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
