namespace DVRTool.Core;

/// <summary>One reading of a live player's counters, as the viewer's statistics report them.</summary>
/// <param name="TimestampMs">When the reading was taken, on a monotonic millisecond clock.</param>
/// <param name="ReadBytes">Bytes the demuxer has handed to the decoders so far — libvlc's
/// <c>demux_read_bytes</c>, which every transport feeds. (Its <c>read_bytes</c> stays 0 over
/// RTSP, where live555 is an access-demux and nothing goes through the stream layer.) libvlc
/// reports it as a 32-bit value that wraps; <see cref="LiveStats.Rates"/> undoes the wrap.</param>
/// <param name="DecodedFrames">libvlc's <c>decoded_video</c> counter as reported. This is
/// <b>not</b> a frame count: VLC 3 bumps it once for every packet the decoder accepts and again
/// for every picture it outputs, so it runs at twice the frame rate. <see cref="LiveStats.Rates"/>
/// halves it.</param>
/// <param name="DisplayedFrames">libvlc's <c>displayed_pictures</c> counter. Also not a frame
/// rate — the video output re-renders the current picture every 80 ms and counts each render —
/// so nothing here derives a rate from it.</param>
/// <param name="LostFrames">Pictures the video output dropped as late.</param>
public readonly record struct LiveStatsSample(
    long TimestampMs, long ReadBytes, long DecodedFrames, long DisplayedFrames, long LostFrames);

/// <summary>What two <see cref="LiveStatsSample"/>s a second or so apart say about the stream.</summary>
/// <param name="FramesPerSecond">Decoded frames per second — the stream's frame rate, as
/// received, whether or not every frame made it to the screen.</param>
/// <param name="BitsPerSecond">Received bitrate over the interval.</param>
/// <param name="FramesLost">Pictures dropped as late during the interval; non-zero means the
/// viewer, not the camera, is behind.</param>
public readonly record struct LiveStatsRates(double FramesPerSecond, double BitsPerSecond, long FramesLost);

/// <summary>
/// The arithmetic and wording behind the Live tab's footer stats: codec, resolution,
/// frames per second and received bitrate for the selected camera.
/// </summary>
/// <remarks>
/// <para>
/// Everything here derives from counters LibVLC keeps per media, so the numbers are what the
/// viewer actually received and decoded — the same whether the bytes came over RTSP or the
/// vendor SDK. The rates are computed from samples rather than taken from libvlc's own
/// <c>InputBitrate</c>, whose units differ between versions and which is a smoothed figure the
/// viewer cannot inspect.
/// </para>
/// <para>
/// Three things about those counters decide the arithmetic, all measured against Site C on
/// 2026-09-02 and confirmed in VLC 3.0's source. <b>The decoded-video counter counts twice per
/// frame</b>: <c>src/input/decoder.c</c> bumps it in <c>DecoderDecode</c> for every packet the
/// decoder accepts and again in <c>DecoderQueueVideo</c> for every picture it outputs, and a
/// program stream carries one packet per frame — a 20 fps HEVC stream ran the counter at 40/s,
/// a 12 fps H.264 stream at 24/s (frames counted independently with ffmpeg). <b>The displayed
/// counter is not a frame rate either</b>: the video output re-renders the current picture
/// every 80 ms (<c>VOUT_REDISPLAY_DELAY</c>) and counts each render, so the 12 fps camera
/// "displayed" 20 pictures a second. <b>And the whole statistics block is a snapshot</b> the
/// input thread refreshes at most every 250 ms (<c>MainLoopStatistics</c>), so a delta between
/// two readings one second apart covers anywhere from about 700 to 1300 ms of stream. Hence
/// <see cref="Rates"/> halves the decoded delta and <see cref="LiveStatsWindow"/> rates over
/// the last several seconds rather than the last one. Before this, a 20 fps camera read
/// 32–48 fps and a 30 fps camera "over 60".
/// </para>
/// <para>
/// Pure so it can be tested without a player. The GUI samples; this interprets.
/// </para>
/// </remarks>
public static class LiveStats
{
    /// <summary>libvlc's byte counter is a 32-bit truncation of a 64-bit total.</summary>
    private const long ByteCounterModulus = 1L << 32;

    /// <summary>
    /// How many times libvlc's decoded-video counter advances per frame: once for the packet
    /// in, once for the picture out.
    /// </summary>
    public const int DecodedCountsPerFrame = 2;

    /// <summary>
    /// True when <paramref name="current"/> cannot be compared with <paramref name="previous"/>
    /// because the counters restarted — the player was given a new media. Frame counters only
    /// ever go down on a restart; the byte counter also goes down when it wraps, by very nearly
    /// the whole modulus, so anything smaller is a restart too.
    /// </summary>
    public static bool IsRestart(LiveStatsSample previous, LiveStatsSample current)
    {
        if (current.DecodedFrames < previous.DecodedFrames || current.LostFrames < previous.LostFrames)
            return true;
        long bytes = current.ReadBytes - previous.ReadBytes;
        return bytes < 0 && bytes > -(ByteCounterModulus / 2);
    }

    /// <summary>
    /// The rates between two samples of the same stream, or null when they cannot be
    /// compared: no time passed, or the counters restarted because the player was given a
    /// new media.
    /// </summary>
    public static LiveStatsRates? Rates(LiveStatsSample previous, LiveStatsSample current)
    {
        long elapsedMs = current.TimestampMs - previous.TimestampMs;
        if (elapsedMs <= 0)
            return null;

        if (IsRestart(previous, current))
            return null;

        long counts = current.DecodedFrames - previous.DecodedFrames;
        long lost = current.LostFrames - previous.LostFrames;
        long bytes = current.ReadBytes - previous.ReadBytes;
        if (bytes < 0)
            bytes += ByteCounterModulus;

        double seconds = elapsedMs / 1000.0;
        return new LiveStatsRates(
            counts / (double)DecodedCountsPerFrame / seconds, bytes * 8 / seconds, lost);
    }

    /// <summary>"4.1 Mbps", "512 kbps".</summary>
    public static string FormatBitrate(double bitsPerSecond)
    {
        if (bitsPerSecond < 0)
            bitsPerSecond = 0;
        return bitsPerSecond >= 1_000_000
            ? $"{bitsPerSecond / 1_000_000:0.0} Mbps"
            : $"{bitsPerSecond / 1_000:0} kbps";
    }

    /// <summary>"1.5 MB", "640 KB", "12 B".</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            bytes = 0;
        if (bytes >= 1_000_000)
            return $"{bytes / 1_000_000.0:0.0} MB";
        if (bytes >= 1_000)
            return $"{bytes / 1_000.0:0} KB";
        return $"{bytes} B";
    }

    /// <summary>
    /// The name a camera's web page would use for a libvlc codec fourcc — "H.264",
    /// "H.265", "MJPEG" — or the fourcc itself for anything unfamiliar. Empty for zero.
    /// </summary>
    public static string CodecName(uint fourcc)
    {
        if (fourcc == 0)
            return "";
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            int b = (int)(fourcc >> (8 * i)) & 0xFF;
            chars[i] = b is >= 0x20 and < 0x7F ? (char)b : '?';
        }
        string raw = new string(chars).Trim();
        return raw.ToLowerInvariant() switch
        {
            "h264" or "avc1" or "x264" => "H.264",
            "hevc" or "h265" or "hvc1" or "hev1" or "x265" => "H.265",
            "mp4v" or "xvid" or "divx" => "MPEG-4",
            "mjpg" or "jpeg" or "mjpa" or "mjpb" => "MJPEG",
            "mpgv" or "mp2v" or "mp1v" => "MPEG-2",
            "av01" => "AV1",
            "vp80" => "VP8",
            "vp90" => "VP9",
            "mp4a" => "AAC",
            "mpga" => "MPEG audio",
            "alaw" => "G.711 A-law",
            "ulaw" => "G.711 µ-law",
            "g726" => "G.726",
            "s16l" => "PCM",
            _ => raw.ToUpperInvariant(),
        };
    }

    /// <summary>
    /// The footer line for one stream: codec, resolution, fps and bitrate, with the drops
    /// only when there are any — a healthy stream has nothing to say about them. The frame
    /// rate is a whole number: the counters behind it are 250 ms snapshots, so a decimal
    /// would be noise dressed as precision.
    /// </summary>
    /// <param name="codec">From <see cref="CodecName"/>; empty until the demuxer has found the track.</param>
    /// <param name="width">Picture width, or 0 when unknown.</param>
    /// <param name="height">Picture height, or 0 when unknown.</param>
    /// <param name="rates">Null before the second sample, when there is nothing to rate yet.</param>
    /// <param name="viewerDroppedBytes">
    /// Bytes the viewer discarded because it fell behind the recorder (the SDK route's
    /// callback buffer). Non-zero is a viewer problem, not a camera problem, and is named
    /// as such.
    /// </param>
    /// <param name="configuredFps">
    /// The frame rate the camera is set to, as its encoder stamps it into the stream header
    /// (the SPS timing info libvlc surfaces on the video track), or 0 when the stream does
    /// not say. Shown as the denominator — "11/12 fps" — so a tech can see at a glance
    /// whether the frames arriving are the frames the camera was told to send.
    /// </param>
    public static string Describe(string codec, int width, int height, LiveStatsRates? rates,
        long viewerDroppedBytes = 0, double configuredFps = 0)
    {
        var parts = new List<string>(6);
        if (codec.Length > 0)
            parts.Add(codec);
        if (width > 0 && height > 0)
            parts.Add($"{width}×{height}");
        if (rates is { } r)
        {
            parts.Add(configuredFps > 0
                ? $"{r.FramesPerSecond:0}/{configuredFps:0.##} fps"
                : $"{r.FramesPerSecond:0} fps");
            parts.Add(FormatBitrate(r.BitsPerSecond));
            if (r.FramesLost > 0)
                parts.Add($"{r.FramesLost} dropped");
        }
        else
        {
            parts.Add("measuring …");
        }
        if (viewerDroppedBytes > 0)
            parts.Add($"{FormatBytes(viewerDroppedBytes)} dropped by viewer");
        return string.Join("  ·  ", parts);
    }
}

/// <summary>
/// Rates over the last several seconds of <see cref="LiveStatsSample"/>s, fed one sample at
/// a time as the GUI takes them.
/// </summary>
/// <remarks>
/// A one-second delta of libvlc's counters is not one second of stream: the counters are a
/// snapshot refreshed at most every 250 ms, so consecutive one-second readings cover 700 to
/// 1300 ms of it and a steady 20 fps reads anywhere from 14 to 26. Rating the newest sample
/// against one <see cref="Span"/> back bounds that error to a quarter second in several. The
/// window also rides out the once-a-GOP keyframe that makes a one-second bitrate jump.
/// Counters that restarted (a new media) empty the window; see <see cref="LiveStats.IsRestart"/>.
/// </remarks>
public sealed class LiveStatsWindow
{
    public static readonly TimeSpan DefaultSpan = TimeSpan.FromSeconds(4);

    private readonly List<LiveStatsSample> _samples = new();

    public LiveStatsWindow(TimeSpan? span = null)
    {
        Span = span ?? DefaultSpan;
        if (Span <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(span), "the window must be longer than nothing");
    }

    /// <summary>How far back the oldest sample used for the rates is kept.</summary>
    public TimeSpan Span { get; }

    /// <summary>Forgets every sample; the next <see cref="Add"/> starts a new window.</summary>
    public void Reset() => _samples.Clear();

    /// <summary>
    /// Adds a reading and returns the rates from the oldest sample still inside the window to
    /// this one, or null when there is nothing to rate yet: the first sample, a sample no newer
    /// than the last, or the first sample after the counters restarted.
    /// </summary>
    public LiveStatsRates? Add(LiveStatsSample sample)
    {
        if (_samples.Count > 0)
        {
            var last = _samples[^1];
            if (sample.TimestampMs <= last.TimestampMs)
                return null;
            if (LiveStats.IsRestart(last, sample))
                _samples.Clear();
        }

        _samples.Add(sample);

        // Drop the oldest only while the one after it still reaches back a full span, so the
        // window never gets shorter than Span once it has grown to it.
        long spanMs = (long)Span.TotalMilliseconds;
        while (_samples.Count > 2 && sample.TimestampMs - _samples[1].TimestampMs >= spanMs)
            _samples.RemoveAt(0);

        return _samples.Count >= 2 ? LiveStats.Rates(_samples[0], sample) : null;
    }
}
