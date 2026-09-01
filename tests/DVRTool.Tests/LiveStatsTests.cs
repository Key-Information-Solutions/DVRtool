using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The footer stats arithmetic. The two things worth pinning are the counter semantics —
/// libvlc's byte total is a wrapping 32-bit value, and every counter restarts when the
/// player gets a new media — and the wording, which the GUI shows verbatim.
/// </summary>
public class LiveStatsTests
{
    private static LiveStatsSample At(long ms, long bytes, long decoded, long displayed = 0, long lost = 0) =>
        new(ms, bytes, decoded, displayed, lost);

    [Fact]
    public void RatesOverOneSecond()
    {
        var r = LiveStats.Rates(At(1000, 100_000, 40), At(2000, 600_000, 60));
        Assert.NotNull(r);
        Assert.Equal(20.0, r.Value.FramesPerSecond, 3);
        Assert.Equal(4_000_000.0, r.Value.BitsPerSecond, 3);
        Assert.Equal(0, r.Value.FramesLost);
    }

    [Fact]
    public void RatesScaleToTheInterval()
    {
        // Half a second of the same stream: same fps, same bitrate.
        var r = LiveStats.Rates(At(0, 0, 0), At(500, 250_000, 10));
        Assert.NotNull(r);
        Assert.Equal(20.0, r.Value.FramesPerSecond, 3);
        Assert.Equal(4_000_000.0, r.Value.BitsPerSecond, 3);
    }

    [Fact]
    public void LostFramesAreTheIntervalsDelta()
    {
        var r = LiveStats.Rates(At(0, 0, 0, 0, 5), At(1000, 1, 20, 17, 8));
        Assert.Equal(3, r!.Value.FramesLost);
    }

    [Fact]
    public void NoTimePassedIsNull()
    {
        Assert.Null(LiveStats.Rates(At(1000, 0, 0), At(1000, 100, 20)));
        Assert.Null(LiveStats.Rates(At(2000, 0, 0), At(1000, 100, 20)));
    }

    [Fact]
    public void ByteCounterWrapIsUndone()
    {
        // 32-bit truncation: just under the modulus, then 500 000 bytes later a small value.
        long before = 0xFFFF_F000L;
        long after = (before + 500_000) & 0xFFFF_FFFFL;
        var r = LiveStats.Rates(At(0, before, 0), At(1000, after, 20));
        Assert.NotNull(r);
        Assert.Equal(500_000 * 8.0, r.Value.BitsPerSecond, 3);
    }

    [Fact]
    public void RestartedCountersAreNull()
    {
        // A new media: every counter is small again. Frames going backwards is the tell.
        Assert.Null(LiveStats.Rates(At(0, 50_000_000, 20_000), At(1000, 400_000, 20)));
        // Frames not yet back to the old total, but the bytes fell by far less than a wrap.
        Assert.Null(LiveStats.Rates(At(0, 50_000_000, 5), At(1000, 400_000, 20)));
    }

    [Theory]
    [InlineData(0, "0 kbps")]
    [InlineData(512_000, "512 kbps")]
    [InlineData(999_499, "999 kbps")]
    [InlineData(1_000_000, "1.0 Mbps")]
    [InlineData(4_150_000, "4.2 Mbps")]
    [InlineData(-5, "0 kbps")]
    public void Bitrates(double bps, string expected)
    {
        Assert.Equal(expected, LiveStats.FormatBitrate(bps));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(640_000, "640 KB")]
    [InlineData(1_500_000, "1.5 MB")]
    public void ByteCounts(long bytes, string expected)
    {
        Assert.Equal(expected, LiveStats.FormatBytes(bytes));
    }

    private static uint FourCc(string s) =>
        (uint)s[0] | ((uint)s[1] << 8) | ((uint)s[2] << 16) | ((uint)s[3] << 24);

    [Theory]
    [InlineData("h264", "H.264")]
    [InlineData("avc1", "H.264")]
    [InlineData("hevc", "H.265")]
    [InlineData("hvc1", "H.265")]
    [InlineData("MJPG", "MJPEG")]
    [InlineData("mp4v", "MPEG-4")]
    [InlineData("mp4a", "AAC")]
    [InlineData("alaw", "G.711 A-law")]
    [InlineData("zzzz", "ZZZZ")]
    public void CodecNames(string fourcc, string expected)
    {
        Assert.Equal(expected, LiveStats.CodecName(FourCc(fourcc)));
    }

    [Fact]
    public void ZeroCodecIsBlank()
    {
        Assert.Equal("", LiveStats.CodecName(0));
    }

    [Fact]
    public void DescribeHealthyStream()
    {
        var line = LiveStats.Describe("H.264", 1920, 1080, new LiveStatsRates(20.04, 4_150_000, 0));
        Assert.Equal("H.264  ·  1920×1080  ·  20.0 fps  ·  4.2 Mbps", line);
    }

    [Fact]
    public void DescribeNamesDropsOnlyWhenThereAreAny()
    {
        var line = LiveStats.Describe("H.265", 2560, 1440, new LiveStatsRates(12, 2_000_000, 3), 640_000);
        Assert.Equal("H.265  ·  2560×1440  ·  12.0 fps  ·  2.0 Mbps  ·  3 dropped  ·  640 KB dropped by viewer", line);
    }

    [Fact]
    public void DescribeBeforeAnythingIsKnown()
    {
        Assert.Equal("measuring …", LiveStats.Describe("", 0, 0, null));
        Assert.Equal("H.264  ·  measuring …", LiveStats.Describe("H.264", 0, 0, null));
    }
}
