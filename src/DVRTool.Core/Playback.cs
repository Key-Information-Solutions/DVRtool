namespace DVRTool.Core;

/// <summary>The container a recorder streams recorded footage in over its web port.</summary>
public enum PlaybackContainer
{
    /// <summary>Hikvision ISAPI download: an MPEG program stream, whatever the device calls it.</summary>
    MpegPs,

    /// <summary>Dahua loadfile.cgi: DHAV (.dav), which LibVLC cannot demux on its own.</summary>
    Dhav,

    /// <summary>Nx Witness / DW Spectrum /media/{id}.mkv: Matroska.</summary>
    Matroska,
}

/// <summary>
/// Recorded footage arriving as a progressive HTTP body — the export path's bytes, handed
/// to a decoder instead of a file.
/// </summary>
/// <remarks>
/// This is the transport that makes playback work at the sites we actually have. RTSP
/// playback needs the RTSP port forwarded, and across the fleet it almost never is; the web
/// port is forwarded everywhere, because that is how the recorder is administered. Every
/// vendor already streams a time range over it for export (<see cref="INvrClient.DownloadAsync"/>),
/// and a decoder fed that body plays it at the footage's own pace — the body arrives faster
/// than real time and the reader simply blocks the socket when it has read ahead far enough.
/// A seek is a new request from a new time, never a seek inside this body.
/// </remarks>
public sealed class PlaybackStream : IDisposable
{
    private readonly IDisposable? _owner;

    public PlaybackStream(Stream body, PlaybackContainer container, DateTime requestedStart,
        DateTime requestedEnd, IDisposable? owner = null)
    {
        Body = body;
        Container = container;
        RequestedStart = requestedStart;
        RequestedEnd = requestedEnd;
        _owner = owner;
    }

    /// <summary>Forward-only; reads block until the recorder sends more.</summary>
    public Stream Body { get; }

    public PlaybackContainer Container { get; }

    /// <summary>The time the body was asked to start at. The device clips it to real footage.</summary>
    public DateTime RequestedStart { get; }

    public DateTime RequestedEnd { get; }

    public void Dispose()
    {
        Body.Dispose();
        _owner?.Dispose();
    }
}

/// <summary>Recorded-footage playback over the web port, per vendor.</summary>
public interface IPlaybackClient
{
    /// <summary>
    /// Opens the recorder's footage for a window as a progressive body. Throws
    /// <see cref="NvrException"/> when the device refuses or sends nothing at all.
    /// </summary>
    Task<PlaybackStream> OpenPlaybackAsync(int channel, DateTime start, DateTime end,
        StreamType stream = StreamType.Main, CancellationToken ct = default);

    /// <summary>
    /// The days of one month (1-based day numbers) that hold footage for a channel — the
    /// playback calendar. Empty when the month has none.
    /// </summary>
    Task<IReadOnlyList<int>> GetRecordedDaysAsync(int channel, int year, int month,
        CancellationToken ct = default);
}

/// <summary>
/// The visible span of a timeline and the mapping between its pixels and wall-clock time.
/// Immutable; zooming and panning produce new windows.
/// </summary>
public readonly record struct TimelineWindow(DateTime Start, TimeSpan Length)
{
    /// <summary>Spans the operator can zoom through, widest first.</summary>
    public static readonly TimeSpan[] ZoomLevels =
    [
        TimeSpan.FromHours(24),
        TimeSpan.FromHours(12),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(3),
        TimeSpan.FromHours(1),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(5),
    ];

    /// <summary>The whole of one calendar day.</summary>
    public static TimelineWindow Day(DateTime date) =>
        new(date.Date, TimeSpan.FromHours(24));

    public DateTime End => Start + Length;

    public bool Contains(DateTime t) => t >= Start && t < End;

    /// <summary>The time under pixel <paramref name="x"/> of a control <paramref name="width"/> wide.</summary>
    public DateTime TimeAt(double x, double width)
    {
        if (width <= 0)
            return Start;
        double fraction = Math.Clamp(x / width, 0.0, 1.0);
        return Start + TimeSpan.FromTicks((long)(Length.Ticks * fraction));
    }

    /// <summary>The pixel column of <paramref name="t"/>; outside [0, width) when off-screen.</summary>
    public double PixelOf(DateTime t, double width) =>
        Length.Ticks == 0 ? 0 : (t - Start).Ticks * width / Length.Ticks;

    /// <summary>
    /// Zooms one level in (<paramref name="steps"/> &gt; 0) or out, keeping the time under
    /// <paramref name="anchorFraction"/> of the width where it is. Clamped to the zoom levels
    /// and to the day the window started in, so a day timeline never drifts into the next.
    /// </summary>
    public TimelineWindow Zoom(int steps, double anchorFraction)
    {
        int level = NearestLevel(Length);
        int target = Math.Clamp(level + steps, 0, ZoomLevels.Length - 1);
        if (target == level)
            return this;
        var anchor = Start + TimeSpan.FromTicks((long)(Length.Ticks * Math.Clamp(anchorFraction, 0, 1)));
        var length = ZoomLevels[target];
        var start = anchor - TimeSpan.FromTicks((long)(length.Ticks * Math.Clamp(anchorFraction, 0, 1)));
        return new TimelineWindow(start, length).ClampToDay(Start.Date);
    }

    /// <summary>Slides the window; clamped to the day it started in.</summary>
    public TimelineWindow Pan(TimeSpan by) =>
        new TimelineWindow(Start + by, Length).ClampToDay(Start.Date);

    /// <summary>Keeps the window inside one calendar day (a 24 h window is the day itself).</summary>
    public TimelineWindow ClampToDay(DateTime day)
    {
        var dayStart = day.Date;
        var dayEnd = dayStart.AddDays(1);
        var length = Length > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : Length;
        var start = Start;
        if (start + length > dayEnd)
            start = dayEnd - length;
        if (start < dayStart)
            start = dayStart;
        return new TimelineWindow(start, length);
    }

    /// <summary>
    /// The spacing of labelled ticks so that neighbours sit at least <paramref name="minPixels"/>
    /// apart — chosen from the clock's own round steps, never a fraction of one.
    /// </summary>
    public TimeSpan TickInterval(double width, double minPixels = 80)
    {
        if (width <= 0)
            return TimeSpan.FromHours(1);
        foreach (var step in TickSteps)
        {
            double px = step.Ticks * width / Length.Ticks;
            if (px >= minPixels)
                return step;
        }
        return TickSteps[^1];
    }

    private static readonly TimeSpan[] TickSteps =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(3),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(12),
    ];

    /// <summary>Labelled tick times inside the window, on round multiples of the interval.</summary>
    public IEnumerable<DateTime> Ticks(double width, double minPixels = 80)
    {
        var step = TickInterval(width, minPixels);
        long first = (long)Math.Ceiling((double)Start.Ticks / step.Ticks) * step.Ticks;
        for (long t = first; t < End.Ticks; t += step.Ticks)
            yield return new DateTime(t, DateTimeKind.Unspecified);
    }

    /// <summary>Label format that shows what the tick spacing resolves.</summary>
    public string TickFormat(double width, double minPixels = 80) =>
        TickInterval(width, minPixels) < TimeSpan.FromMinutes(1) ? "HH:mm:ss" : "HH:mm";

    private static int NearestLevel(TimeSpan length)
    {
        int best = 0;
        long bestDiff = long.MaxValue;
        for (int i = 0; i < ZoomLevels.Length; i++)
        {
            long diff = Math.Abs(ZoomLevels[i].Ticks - length.Ticks);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = i;
            }
        }
        return best;
    }
}

/// <summary>One merged run of footage, and what recorded it if every piece agreed.</summary>
public sealed record FootageSpan(DateTime Start, DateTime End, RecordingType Type)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>Where footage exists, as the timeline and the gap-skip need it.</summary>
public static class FootageCoverage
{
    /// <summary>
    /// Merges touching or overlapping segments into runs. Segments closer than
    /// <paramref name="joinGap"/> are joined — recorders split a continuous day into files
    /// with second-scale seams that are not gaps a viewer should stop at.
    /// </summary>
    public static IReadOnlyList<FootageSpan> Merge(IEnumerable<RecordingSegment> segments,
        TimeSpan? joinGap = null)
    {
        var gap = joinGap ?? TimeSpan.FromSeconds(2);
        var result = new List<FootageSpan>();
        foreach (var s in segments.Where(s => s.End > s.Start).OrderBy(s => s.Start))
        {
            if (result.Count > 0 && s.Start <= result[^1].End + gap)
            {
                var last = result[^1];
                result[^1] = last with
                {
                    End = s.End > last.End ? s.End : last.End,
                    Type = last.Type == s.Type ? last.Type : RecordingType.Unknown,
                };
            }
            else
            {
                result.Add(new FootageSpan(s.Start, s.End, s.Type));
            }
        }
        return result;
    }

    /// <summary>True when <paramref name="t"/> falls inside recorded footage.</summary>
    public static bool Contains(IReadOnlyList<FootageSpan> coverage, DateTime t) =>
        coverage.Any(c => t >= c.Start && t < c.End);

    /// <summary>
    /// Where playback should start for a request at <paramref name="t"/>: <paramref name="t"/>
    /// itself inside footage, else the start of the next run after it, else null when nothing
    /// is recorded from there on.
    /// </summary>
    public static DateTime? NextFootageAt(IReadOnlyList<FootageSpan> coverage, DateTime t)
    {
        foreach (var c in coverage.OrderBy(c => c.Start))
        {
            if (t >= c.Start && t < c.End)
                return t;
            if (c.Start > t)
                return c.Start;
        }
        return null;
    }

    /// <summary>The run containing <paramref name="t"/>, or null.</summary>
    public static FootageSpan? SpanAt(IReadOnlyList<FootageSpan> coverage, DateTime t) =>
        coverage.FirstOrDefault(c => t >= c.Start && t < c.End);

    /// <summary>Total recorded time.</summary>
    public static TimeSpan Total(IReadOnlyList<FootageSpan> coverage) =>
        TimeSpan.FromTicks(coverage.Sum(c => c.Duration.Ticks));
}

/// <summary>
/// Turns the decoder's media clock into a wall-clock playhead.
/// </summary>
/// <remarks>
/// The body a seek opens starts at the time that was asked for — or, since the recorder clips
/// the request to what exists, at the first footage after it, which is why a seek is snapped to
/// <see cref="FootageCoverage.NextFootageAt"/> before it is requested. From then on the decoder
/// reports how far into the body it is, and the playhead is that offset added to the anchor. The
/// media clock already runs at the playback rate, so rate is not applied here; it is kept only so
/// the display can say what speed the picture is moving at.
/// </remarks>
public sealed class PlaybackClock
{
    /// <summary>The wall-clock time the current body begins at; null until a seek.</summary>
    public DateTime? Anchor { get; private set; }

    /// <summary>The decoder's last reported offset into the body.</summary>
    public TimeSpan Elapsed { get; private set; }

    public float Rate { get; set; } = 1f;

    /// <summary>A new body from <paramref name="start"/>: the playhead jumps there.</summary>
    public void Reanchor(DateTime start)
    {
        Anchor = start;
        Elapsed = TimeSpan.Zero;
    }

    /// <summary>
    /// Takes the decoder's media time. Negative or zero values are what LibVLC reports before
    /// the first picture and are read as "still at the anchor".
    /// </summary>
    public void Update(long mediaTimeMs)
    {
        if (mediaTimeMs > 0)
            Elapsed = TimeSpan.FromMilliseconds(mediaTimeMs);
    }

    public void Clear()
    {
        Anchor = null;
        Elapsed = TimeSpan.Zero;
    }

    /// <summary>Where the picture on screen is in wall-clock time, or null when nothing is playing.</summary>
    public DateTime? Position => Anchor is DateTime a ? a + Elapsed : null;
}

/// <summary>
/// A stream whose first bytes were already read (to see whether there were any) followed by
/// the rest of the source, so a peek costs the consumer nothing.
/// </summary>
public sealed class PrefixedStream : Stream
{
    private readonly byte[] _prefix;
    private int _prefixOffset;
    private readonly int _prefixCount;
    private readonly Stream _rest;

    public PrefixedStream(byte[] prefix, int count, Stream rest)
    {
        _prefix = prefix;
        _prefixCount = count;
        _rest = rest;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_prefixOffset < _prefixCount)
        {
            int n = Math.Min(count, _prefixCount - _prefixOffset);
            Array.Copy(_prefix, _prefixOffset, buffer, offset, n);
            _prefixOffset += n;
            return n;
        }
        return _rest.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_prefixOffset < _prefixCount)
        {
            int n = Math.Min(buffer.Length, _prefixCount - _prefixOffset);
            _prefix.AsMemory(_prefixOffset, n).CopyTo(buffer);
            _prefixOffset += n;
            return n;
        }
        return await _rest.ReadAsync(buffer, ct);
    }

    /// <summary>
    /// Drops everything before the first occurrence of <paramref name="marker"/> in the
    /// unread part of the prefix. True when it was found; false leaves the stream untouched.
    /// For a recorder that wraps its media in a header of its own before the first sync word.
    /// </summary>
    public bool SkipTo(ReadOnlySpan<byte> marker)
    {
        int at = _prefix.AsSpan(_prefixOffset, _prefixCount - _prefixOffset).IndexOf(marker);
        if (at < 0)
            return false;
        _prefixOffset += at;
        return true;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _rest.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>
    /// Reads the first bytes of <paramref name="source"/>. Returns null when the source ends
    /// before delivering any — a recorder that accepted a request and sent nothing — otherwise
    /// a stream that replays what was read and continues with the rest.
    /// </summary>
    public static async Task<PrefixedStream?> PeekAsync(Stream source, int peekBytes,
        CancellationToken ct)
    {
        var buffer = new byte[peekBytes];
        int total = 0;
        while (total < peekBytes)
        {
            int n = await source.ReadAsync(buffer.AsMemory(total, peekBytes - total), ct);
            if (n == 0)
                break;
            total += n;
        }
        return total == 0 ? null : new PrefixedStream(buffer, total, source);
    }
}
