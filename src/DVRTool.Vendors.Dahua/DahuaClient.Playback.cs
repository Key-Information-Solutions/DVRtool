using DVRTool.Core;

namespace DVRTool.Vendors.Dahua;

/// <summary>
/// The <see cref="IPlaybackClient"/> face of the Dahua CGI client.
/// </summary>
/// <remarks>
/// <para>
/// The body is <c>loadfile.cgi</c>'s — the same call the export uses — and arrives as DHAV,
/// which LibVLC cannot demux; the front end routes it through <see cref="ContainerPipe"/>
/// (ffmpeg) on the way to the decoder. <c>subtype</c> selects the stream, 1-based channel
/// as everywhere on loadfile.
/// </para>
/// <para>
/// Dahua has no calendar call, so the recorded days of a month are bucketed from one
/// <c>mediaFileFind</c> over the month. A continuous camera lists roughly a file an hour,
/// so a month is a few pages of a hundred.
/// </para>
/// </remarks>
public sealed partial class DahuaClient : IPlaybackClient
{
    /// <summary>
    /// The longest span one <c>loadfile.cgi</c> request may cover. Site B
    /// (DH-NVR608H-128-4KS3/I, 2026-09-04) answers 6 h with footage and 8 h — and anything
    /// longer, crossing midnight or not — with 400 Bad Request; the exact ceiling between the
    /// two is not known, so the verified value is used. The front end asks again from where
    /// the body ended, so a longer watch is several requests, not one.
    /// </summary>
    public static readonly TimeSpan MaxLoadfileWindow = TimeSpan.FromHours(6);

    public async Task<PlaybackStream> OpenPlaybackAsync(int channel, DateTime start,
        DateTime end, StreamType stream = StreamType.Main, CancellationToken ct = default)
    {
        if (end - start > MaxLoadfileWindow)
            end = start + MaxLoadfileWindow;
        string path =
            $"/cgi-bin/loadfile.cgi?action=startLoad&channel={channel}" +
            $"&startTime={Uri.EscapeDataString(FormatCgiTime(start))}" +
            $"&endTime={Uri.EscapeDataString(FormatCgiTime(end))}" +
            $"&subtype={(int)stream}";

        var resp = await _downloadHttp.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            if (!resp.IsSuccessStatusCode)
            {
                string errText = await resp.Content.ReadAsStringAsync(ct);
                throw new NvrException(
                    $"loadfile.cgi failed: {(int)resp.StatusCode} {resp.ReasonPhrase}",
                    errText, (int)resp.StatusCode);
            }
            var source = await resp.Content.ReadAsStreamAsync(ct);
            var body = await PrefixedStream.PeekAsync(source, PlaybackPeekBytes, ct)
                ?? throw new NvrException(
                    "loadfile.cgi returned an empty stream — no footage in that range?");
            var stream_ = new PlaybackStream(body, PlaybackContainer.Dhav, start, end, owner: resp);
            resp = null;
            return stream_;
        }
        finally
        {
            resp?.Dispose();
        }
    }

    /// <summary>Enough to prove the recorder is sending video, not so much that a slow link stalls the start.</summary>
    private const int PlaybackPeekBytes = 16 * 1024;

    public async Task<IReadOnlyList<int>> GetRecordedDaysAsync(int channel, int year, int month,
        CancellationToken ct = default)
    {
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var segments = await SearchAsync(channel, first, first.AddMonths(1), ct);
        return RecordedDays(segments, year, month);
    }

    /// <summary>Which days of the month the segments touch, 1-based and ascending.</summary>
    internal static IReadOnlyList<int> RecordedDays(IEnumerable<RecordingSegment> segments,
        int year, int month)
    {
        var days = new SortedSet<int>();
        int last = DateTime.DaysInMonth(year, month);
        foreach (var s in segments)
        {
            // A file ending exactly at midnight belongs to the day before it.
            var end = s.End > s.Start ? s.End.AddTicks(-1) : s.Start;
            for (var d = s.Start.Date; d <= end.Date; d = d.AddDays(1))
            {
                if (d.Year == year && d.Month == month && d.Day <= last)
                    days.Add(d.Day);
            }
        }
        return days.ToList();
    }
}
