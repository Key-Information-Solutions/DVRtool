namespace DVRTool.Core;

/// <summary>One reading of a live player's counters, as the viewer's statistics report them.</summary>
/// <param name="TimestampMs">When the reading was taken, on a monotonic millisecond clock.</param>
/// <param name="ReadBytes">Bytes read from the input so far. libvlc reports this as a 32-bit
/// value that wraps; <see cref="LiveStats.Rates"/> undoes the wrap.</param>
/// <param name="DecodedFrames">Video frames the decoder has produced.</param>
/// <param name="DisplayedFrames">Pictures the video output has put on screen.</param>
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
/// vendor SDK. The rates are computed from consecutive samples rather than taken from
/// libvlc's own <c>InputBitrate</c>, whose units differ between versions and which is a
/// smoothed figure the viewer cannot inspect.
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
    /// The rates between two samples of the same stream, or null when they cannot be
    /// compared: no time passed, or the counters restarted because the player was given a
    /// new media.
    /// </summary>
    public static LiveStatsRates? Rates(LiveStatsSample previous, LiveStatsSample current)
    {
        long elapsedMs = current.TimestampMs - previous.TimestampMs;
        if (elapsedMs <= 0)
            return null;

        long frames = current.DecodedFrames - previous.DecodedFrames;
        long lost = current.LostFrames - previous.LostFrames;
        long bytes = current.ReadBytes - previous.ReadBytes;

        // Frame counters only ever go down when the stream was restarted. The byte counter
        // also goes down when it wraps, by very nearly the whole modulus — anything smaller
        // is a restart too.
        if (frames < 0 || lost < 0)
            return null;
        if (bytes < 0)
        {
            if (bytes > -(ByteCounterModulus / 2))
                return null;
            bytes += ByteCounterModulus;
        }

        double seconds = elapsedMs / 1000.0;
        return new LiveStatsRates(frames / seconds, bytes * 8 / seconds, lost);
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
    /// only when there are any — a healthy stream has nothing to say about them.
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
    public static string Describe(string codec, int width, int height, LiveStatsRates? rates,
        long viewerDroppedBytes = 0)
    {
        var parts = new List<string>(6);
        if (codec.Length > 0)
            parts.Add(codec);
        if (width > 0 && height > 0)
            parts.Add($"{width}×{height}");
        if (rates is { } r)
        {
            parts.Add($"{r.FramesPerSecond:0.0} fps");
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
