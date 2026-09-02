using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The footer stats arithmetic. The things worth pinning are the counter semantics —
/// libvlc's decoded-video counter advances twice per frame, its byte total is a wrapping
/// 32-bit value, every counter restarts when the player gets a new media, and the whole
/// block is a 250 ms snapshot that a one-second delta cannot be trusted over — and the
/// wording, which the GUI shows verbatim.
/// </summary>
public class LiveStatsTests
{
    private static LiveStatsSample At(long ms, long bytes, long decoded, long displayed = 0, long lost = 0) =>
        new(ms, bytes, decoded, displayed, lost);

    [Fact]
    public void RatesOverOneSecond()
    {
        // Twenty frames is forty counts: libvlc bumps decoded_video per packet in and per
        // picture out (measured 40/s on Site C's 20 fps main stream, 24/s on a 12 fps one).
        var r = LiveStats.Rates(At(1000, 100_000, 80), At(2000, 600_000, 120));
        Assert.NotNull(r);
        Assert.Equal(20.0, r.Value.FramesPerSecond, 3);
        Assert.Equal(4_000_000.0, r.Value.BitsPerSecond, 3);
        Assert.Equal(0, r.Value.FramesLost);
    }

    [Fact]
    public void RatesScaleToTheInterval()
    {
        // Half a second of the same stream: same fps, same bitrate.
        var r = LiveStats.Rates(At(0, 0, 0), At(500, 250_000, 20));
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

    [Fact]
    public void RestartIsTheBackwardStep()
    {
        Assert.False(LiveStats.IsRestart(At(0, 100, 10), At(1000, 200, 20)));
        Assert.True(LiveStats.IsRestart(At(0, 100, 10), At(1000, 200, 5)));
        Assert.True(LiveStats.IsRestart(At(0, 100, 10, 0, 4), At(1000, 200, 20, 0, 1)));
        Assert.True(LiveStats.IsRestart(At(0, 50_000_000, 10), At(1000, 400_000, 20)));
        Assert.False(LiveStats.IsRestart(At(0, 0xFFFF_F000L, 10), At(1000, 1000, 20)));
    }

    // ----- the sliding window -----

    /// <summary>
    /// libvlc refreshes its statistics snapshot at most every 250 ms, so a reading taken at
    /// <paramref name="ms"/> shows the counters as they were up to a few hundred ms earlier.
    /// A steady 20 fps stream (40 counts/s) at 4 Mbps, seen through a snapshot that old.
    /// </summary>
    private static LiveStatsSample Snapshot(long ms, long ageMs)
    {
        long streamMs = ms - ageMs;
        return At(ms, streamMs * 500, streamMs * 40 / 1000);
    }

    [Fact]
    public void OneSecondDeltasSwingWithTheSnapshotAge()
    {
        // Exactly what made a 20 fps camera read 32–48 fps: the age of consecutive
        // snapshots differs by up to 300 ms, and a one-second delta has no way to know.
        var slow = LiveStats.Rates(Snapshot(1000, 0), Snapshot(2000, 300));
        var fast = LiveStats.Rates(Snapshot(2000, 300), Snapshot(3000, 0));
        Assert.Equal(14.0, slow!.Value.FramesPerSecond, 3);
        Assert.Equal(26.0, fast!.Value.FramesPerSecond, 3);
    }

    [Fact]
    public void WindowHoldsTheRateWithinTenPercent()
    {
        var window = new LiveStatsWindow(TimeSpan.FromSeconds(4));
        long[] ages = [0, 300, 0, 300, 300, 0, 250, 50, 300, 0, 300];
        var rates = new List<double>();
        for (int i = 0; i < ages.Length; i++)
        {
            var r = window.Add(Snapshot(1000 * (i + 1), ages[i]));
            if (i >= 4) // window full from the fifth sample on
                rates.Add(r!.Value.FramesPerSecond);
        }
        Assert.All(rates, fps => Assert.InRange(fps, 18.0, 22.0));
    }

    [Fact]
    public void WindowNeedsTwoSamples()
    {
        var window = new LiveStatsWindow();
        Assert.Null(window.Add(At(1000, 0, 0)));
        Assert.NotNull(window.Add(At(2000, 500_000, 40)));
    }

    [Fact]
    public void WindowRatesFromTheOldestSampleInsideTheSpan()
    {
        // 20 fps for five seconds, then 10 fps. Four seconds after the change the window
        // holds only the slow part.
        var window = new LiveStatsWindow(TimeSpan.FromSeconds(4));
        long counts = 0;
        LiveStatsRates? last = null;
        for (int t = 0; t <= 9; t++)
        {
            last = window.Add(At(t * 1000, 0, counts));
            counts += t < 5 ? 40 : 20;
        }
        Assert.Equal(10.0, last!.Value.FramesPerSecond, 3);
        // Halfway through the change the window straddles it.
        var mid = new LiveStatsWindow(TimeSpan.FromSeconds(4));
        counts = 0;
        for (int t = 0; t <= 7; t++)
        {
            last = mid.Add(At(t * 1000, 0, counts));
            counts += t < 5 ? 40 : 20;
        }
        // Samples at 3..7: 4 s; counts over them 40 + 40 + 20 + 20 = 120 → 15 fps.
        Assert.Equal(15.0, last!.Value.FramesPerSecond, 3);
    }

    [Fact]
    public void WindowEmptiesOnRestart()
    {
        var window = new LiveStatsWindow();
        window.Add(At(0, 1_000_000, 400));
        window.Add(At(1000, 1_500_000, 440));
        // New media: counters are small again. Nothing to rate this tick …
        Assert.Null(window.Add(At(2000, 10_000, 4)));
        // … and the next tick rates against the restart, not the old stream.
        var r = window.Add(At(3000, 510_000, 44));
        Assert.Equal(20.0, r!.Value.FramesPerSecond, 3);
        Assert.Equal(4_000_000.0, r.Value.BitsPerSecond, 3);
    }

    [Fact]
    public void WindowIgnoresASampleThatIsNotNewer()
    {
        var window = new LiveStatsWindow();
        window.Add(At(1000, 0, 0));
        Assert.Null(window.Add(At(1000, 500_000, 40)));
        Assert.Null(window.Add(At(900, 500_000, 40)));
        Assert.Equal(20.0, window.Add(At(2000, 500_000, 40))!.Value.FramesPerSecond, 3);
    }

    [Fact]
    public void WindowLostFramesSpanTheWindow()
    {
        var window = new LiveStatsWindow(TimeSpan.FromSeconds(4));
        for (int t = 0; t <= 4; t++)
            window.Add(At(t * 1000, 0, t * 40, 0, t == 4 ? 3 : t >= 2 ? 1 : 0));
        var r = window.Add(At(5000, 0, 200, 0, 3));
        Assert.Equal(3, r!.Value.FramesLost);
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
        Assert.Equal("H.264  ·  1920×1080  ·  20 fps  ·  4.2 Mbps", line);
    }

    [Fact]
    public void DescribeNamesDropsOnlyWhenThereAreAny()
    {
        var line = LiveStats.Describe("H.265", 2560, 1440, new LiveStatsRates(11.6, 2_000_000, 3), 640_000);
        Assert.Equal("H.265  ·  2560×1440  ·  12 fps  ·  2.0 Mbps  ·  3 dropped  ·  640 KB dropped by viewer", line);
    }

    [Fact]
    public void DescribeBeforeAnythingIsKnown()
    {
        Assert.Equal("measuring …", LiveStats.Describe("", 0, 0, null));
        Assert.Equal("H.264  ·  measuring …", LiveStats.Describe("H.264", 0, 0, null));
    }
}
