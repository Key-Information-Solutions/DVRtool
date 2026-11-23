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
    /// <summary>
    /// Set once this recorder has refused a time-only playbackURI, so later seeks go
    /// straight to the per-file path instead of paying two failed requests each time.
    /// </summary>
    private volatile bool _timeOnlyDownloadRefused;

    /// <summary>How far before the asked-for time a per-file search looks, because the
    /// search clips a file's start to the window and the file may have begun earlier.</summary>
    private static readonly TimeSpan FileSearchLeadIn = TimeSpan.FromHours(2);

    public async Task<PlaybackStream> OpenPlaybackAsync(int channel, DateTime start,
        DateTime end, StreamType stream = StreamType.Main, CancellationToken ct = default)
    {
        if (!_timeOnlyDownloadRefused)
        {
            string playbackUri =
                $"rtsp://{Connection.Host}:{Connection.RtspPort}" +
                $"/Streaming/tracks/{TrackId(channel, stream)}" +
                $"?starttime={FormatRtspTime(start)}&endtime={FormatRtspTime(end)}";
            try
            {
                var (response, body) = await OpenDownloadAsync(playbackUri, ct);
                // The body opens with a 64-byte envelope of Hikvision's own ("IMKH" at offset 24,
                // seen on Site C 2026-09-04) before the first pack header. ffmpeg scans past it,
                // which is why exports remux fine; VLC's PS demuxer wants the pack header first,
                // so it is dropped here. If it is ever absent the body is passed through as it came.
                body.SkipTo(PsPackHeader);
                return new PlaybackStream(body, PlaybackContainer.MpegPs, start, end, owner: response);
            }
            catch (NvrException ex) when (IsTimeOnlyUriRefusal(ex))
            {
                _timeOnlyDownloadRefused = true;
            }
        }
        return await OpenPlaybackByFileAsync(channel, start, end, stream, ct);
    }

    /// <summary>
    /// Playback on firmware that only downloads by file (DS-7608NI-Q2, Site I 2026-11:
    /// a playbackURI without <c>name=</c>/<c>size=</c> is 400 badXmlContent on GET and POST).
    /// With them it sends the WHOLE file and ignores the times, so the body is one recording
    /// file and says so: its start and end are the file's, not the ones asked for, and the
    /// caller re-anchors its clock to them. Playing the next file is the caller's normal
    /// end-of-body continuation.
    /// </summary>
    private async Task<PlaybackStream> OpenPlaybackByFileAsync(int channel, DateTime start,
        DateTime end, StreamType stream, CancellationToken ct)
    {
        var segments = await SearchAsync(channel, stream, start - FileSearchLeadIn, end, ct);
        var file = segments
            .Where(s => s.End > start && !string.IsNullOrEmpty(s.NativeId))
            .OrderBy(s => s.Start)
            .FirstOrDefault()
            ?? throw new NvrException(
                $"No recording on channel {channel} between {start:yyyy-MM-dd HH:mm:ss} and {end:HH:mm:ss}.");

        var (response, body) = await OpenDownloadAsync(file.NativeId!, ct);
        body.SkipTo(PsPackHeader);
        return new PlaybackStream(body, PlaybackContainer.MpegPs, file.Start, file.End, owner: response);
    }

    /// <summary>The recorder rejected the request's form, not the footage: 400 badXmlContent.</summary>
    private static bool IsTimeOnlyUriRefusal(NvrException ex) =>
        ex.Message.Contains("badXmlContent", StringComparison.OrdinalIgnoreCase) ||
        (ex.ResponseBody?.Contains("badXmlContent", StringComparison.OrdinalIgnoreCase) ?? false);

    private static ReadOnlySpan<byte> PsPackHeader => [0x00, 0x00, 0x01, 0xBA];

    public Task<IReadOnlyList<int>> GetRecordedDaysAsync(int channel, int year, int month,
        CancellationToken ct = default) =>
        GetRecordedDaysForTrackAsync(TrackId(channel, StreamType.Main), year, month, ct);
}
