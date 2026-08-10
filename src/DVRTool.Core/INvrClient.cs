namespace DVRTool.Core;

/// <summary>
/// Vendor-neutral NVR client. All DateTimes are NVR-local wall-clock time
/// (Kind = Unspecified); channel numbers are 1-based display numbers.
/// </summary>
public interface INvrClient : IDisposable
{
    Vendor Vendor { get; }
    NvrConnection Connection { get; }

    Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Channel>> GetChannelsAsync(CancellationToken ct = default);

    /// <summary>Search the NVR's on-disk recordings for one channel in a time window.</summary>
    Task<IReadOnlyList<RecordingSegment>> SearchAsync(
        int channel, DateTime start, DateTime end, CancellationToken ct = default);

    /// <summary>RTSP URI for the channel's live stream.</summary>
    Uri GetLiveUri(int channel, StreamType stream = StreamType.Main, bool includeCredentials = false);

    /// <summary>RTSP URI that plays recorded footage for a time span straight off the NVR.</summary>
    Uri GetPlaybackUri(int channel, DateTime start, DateTime end,
        StreamType stream = StreamType.Main, bool includeCredentials = false);

    /// <summary>
    /// Download recorded footage for an arbitrary time span to a local file, in the
    /// NVR's native container (Hikvision: MP4/PS; Dahua: DAV — remux separately).
    /// </summary>
    Task DownloadAsync(int channel, DateTime start, DateTime end, string destinationPath,
        IProgress<long>? bytesProgress = null, CancellationToken ct = default);

    /// <summary>Download one segment returned by <see cref="SearchAsync"/>.</summary>
    Task DownloadSegmentAsync(RecordingSegment segment, string destinationPath,
        IProgress<long>? bytesProgress = null, CancellationToken ct = default);
}
