using DVRTool.Core;

namespace DVRTool.Vendors.NxWitness;

/// <summary>
/// The <see cref="IPlaybackClient"/> face of the Nx client.
/// </summary>
/// <remarks>
/// <para>
/// The body is the export's <c>/media/{id}.mkv?pos=&amp;endPos=</c> — Matroska, which LibVLC
/// demuxes natively — over the same HTTPS session as everything else, which is why playback
/// works through the DW Cloud relay where RTSP cannot. The stream parameter is not honoured:
/// Nx serves the archive as recorded, and it archives the primary stream.
/// </para>
/// <para>
/// The calendar comes from the footage call at a one-hour detail level over the month, so
/// a month is one request and adjacent chunks collapse into a handful of periods. Times are
/// UTC on the wire and bucketed into days in the operator's zone, like every other Nx time
/// this client shows.
/// </para>
/// </remarks>
public sealed partial class NxWitnessClient : IPlaybackClient
{
    public async Task<PlaybackStream> OpenPlaybackAsync(int channel, DateTime start,
        DateTime end, StreamType stream = StreamType.Main, CancellationToken ct = default)
    {
        var camera = await ResolveAsync(channel, ct);
        await EnsureSessionAsync(ct);

        string path = $"/media/{camera.Id}.mkv?pos={ToUnixMs(start)}&endPos={ToUnixMs(end)}";
        var resp = await _downloadHttp.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            if (!resp.IsSuccessStatusCode)
            {
                string errText = await resp.Content.ReadAsStringAsync(ct);
                string reason = NxJson.ErrorString(errText);
                throw new NvrException(
                    $"GET {path} → {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                    (reason.Length > 0 ? $": {reason}" : ""),
                    errText, (int)resp.StatusCode);
            }
            var source = await resp.Content.ReadAsStreamAsync(ct);
            var body = await PrefixedStream.PeekAsync(source, 16 * 1024, ct)
                ?? throw new NvrException("the server sent no media — no footage in that range?");
            var playback = new PlaybackStream(body, PlaybackContainer.Matroska, start, end, owner: resp);
            resp = null;
            return playback;
        }
        finally
        {
            resp?.Dispose();
        }
    }

    public async Task<IReadOnlyList<int>> GetRecordedDaysAsync(int channel, int year, int month,
        CancellationToken ct = default)
    {
        var camera = await ResolveAsync(channel, ct);
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var periods = await GetFootageAsync(camera, ToUnixMs(first), ToUnixMs(first.AddMonths(1)),
            detailLevelMs: 3_600_000, ct);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var days = new SortedSet<int>();
        int last = DateTime.DaysInMonth(year, month);
        foreach (var p in periods)
        {
            var start = FromUnixMs(p.StartMs);
            long endMs = p.OpenEnded ? Math.Max(nowMs, p.StartMs) : p.StartMs + p.DurationMs;
            var end = FromUnixMs(Math.Max(endMs - 1, p.StartMs));
            for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
            {
                if (d.Year == year && d.Month == month && d.Day <= last)
                    days.Add(d.Day);
            }
        }
        return days.ToList();
    }
}
