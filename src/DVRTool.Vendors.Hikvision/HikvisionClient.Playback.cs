using DVRTool.Core;

namespace DVRTool.Vendors.Hikvision;

/// <summary>
/// The <see cref="IPlaybackClient"/> face of the ISAPI client: recorded footage as a
/// progressive body over the web port, and the playback calendar.
/// </summary>
/// <remarks>
/// The body is the export path's — <c>/ISAPI/ContentMgmt/download</c> with a time-window
/// playbackURI — which the device answers with an MPEG program stream regardless of the
/// name it would give the file. That is the same container the SDK live path hands LibVLC,
/// so the same decoder settings apply (<c>avcodec-threads=1</c>). The recorder clips the
/// window to real footage, so a request into a gap starts at the next recording.
/// </remarks>
public sealed partial class HikvisionClient : IPlaybackClient
{
    public async Task<PlaybackStream> OpenPlaybackAsync(int channel, DateTime start,
        DateTime end, StreamType stream = StreamType.Main, CancellationToken ct = default)
    {
        string playbackUri =
            $"rtsp://{Connection.Host}:{Connection.RtspPort}" +
            $"/Streaming/tracks/{TrackId(channel, stream)}" +
            $"?starttime={FormatRtspTime(start)}&endtime={FormatRtspTime(end)}";
        var (response, body) = await OpenDownloadAsync(playbackUri, ct);
        // The body opens with a 64-byte envelope of Hikvision's own ("IMKH" at offset 24, seen
        // on Site C 2026-09-04) before the first pack header. ffmpeg scans past it, which is
        // why exports remux fine; VLC's PS demuxer wants the pack header first, so it is
        // dropped here. If it is ever absent the body is passed through as it came.
        body.SkipTo(PsPackHeader);
        return new PlaybackStream(body, PlaybackContainer.MpegPs, start, end, owner: response);
    }

    private static ReadOnlySpan<byte> PsPackHeader => [0x00, 0x00, 0x01, 0xBA];

    public Task<IReadOnlyList<int>> GetRecordedDaysAsync(int channel, int year, int month,
        CancellationToken ct = default) =>
        GetRecordedDaysForTrackAsync(TrackId(channel, StreamType.Main), year, month, ct);
}
