using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// Clock drift, measured the way the discovery pass measured it: a difference of wall-clock
/// digits, never of instants. Every case here is a reading that actually happened on
/// 2026-09-09 (<c>docs/device-config-discovery.md</c>) or a boundary of the tolerance those
/// readings are judged against.
/// </summary>
public class ClockDriftTests
{
    // The operator's own clock at the moment of the discovery pass: 07:24:30 in EDT (−04:00).
    private static readonly DateTimeOffset Reference =
        new(2026, 9, 9, 7, 24, 30, TimeSpan.FromHours(-4));

    private static ClockDrift Measure(DeviceClock clock, double latencySeconds = 0,
        int? expectedOffsetMinutes = null) =>
        ClockDrift.Measure(clock, Reference,
            Reference.AddSeconds(latencySeconds), expectedOffsetMinutes);

    private static DeviceClock At(string wall, TimeSpan? declared = null,
        string? zone = null, bool? dst = null) =>
        new(DateTime.Parse(wall), declared, zone, dst);

    [Fact]
    public void SiteB_IsAnHourBehind()
    {
        // The Dahua 128-channel recorder: 06:24:26 against a 07:24:30 workstation. Every
        // recording it writes is stamped an hour early, and nothing else in DVRTool says so.
        var drift = Measure(At("2026-09-09 06:24:26"));

        Assert.True(drift.IsSignificant);
        Assert.True(drift.Drift < TimeSpan.Zero);
        Assert.Equal(-3604, (int)drift.Drift.TotalSeconds);
        Assert.Equal("1 h behind", drift.Text);
        Assert.Equal(new DateTime(2026, 9, 9, 7, 24, 30), drift.ReferenceWallClock);

        // Dahua states no offset at all, so there is no claim to disagree with.
        Assert.False(drift.DeclaredOffsetDiffers);
    }

    [Fact]
    public void Hikvision_ClockRight_OffsetWrong()
    {
        // The pairing the two fields exist for: 07:24:29−05:00 read from a recorder standing
        // in −04:00. The digits are correct EDT — so drift is nil — while
        // DateTimeOffset.Parse of the same string would put the instant an hour out.
        var drift = Measure(At("2026-09-09 07:24:29", TimeSpan.FromHours(-5),
            "CST+5:00:00DST01:00:00,M3.2.0/02:00:00,M11.1.0/02:00:00"));

        Assert.False(drift.IsSignificant);
        Assert.True(drift.DeclaredOffsetDiffers);
        Assert.Equal("in step", drift.Text);
    }

    [Fact]
    public void AnOffsetThatAgreesIsNotReported()
    {
        var drift = Measure(At("2026-09-09 07:24:30", TimeSpan.FromHours(-4)));

        Assert.False(drift.DeclaredOffsetDiffers);
        Assert.False(drift.IsSignificant);
    }

    [Fact]
    public void LatencyIsTheErrorBar_AndTheReferenceIsItsMidpoint()
    {
        // A relay read through DW Cloud can take a second, and a one-second error bar on a
        // one-minute tolerance has to be visible rather than assumed away.
        var drift = Measure(At("2026-09-09 07:24:32"), latencySeconds: 4);

        Assert.Equal(TimeSpan.FromSeconds(4), drift.ReadLatency);
        Assert.Equal(new DateTime(2026, 9, 9, 7, 24, 32), drift.ReferenceWallClock);
        Assert.Equal(TimeSpan.Zero, drift.Drift);
    }

    [Fact]
    public void EndsGivenBackwardsStillMeasure()
    {
        var drift = ClockDrift.Measure(At("2026-09-09 07:24:30"),
            Reference.AddSeconds(2), Reference);

        Assert.Equal(TimeSpan.FromSeconds(2), drift.ReadLatency);
        Assert.False(drift.IsSignificant);
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, false)] // exactly the tolerance is still trusted
    [InlineData(61, true)]
    [InlineData(-61, true)]
    public void ToleranceIsOneMinute(int driftSeconds, bool significant)
    {
        var drift = Measure(new DeviceClock(
            new DateTime(2026, 9, 9, 7, 24, 30).AddSeconds(driftSeconds), null, null, null));

        Assert.Equal(significant, drift.IsSignificant);
    }

    [Fact]
    public void AnExpectedOffsetMovesTheReference_NotTheReading()
    {
        // A recorder deliberately an hour east of the workstation: 08:24:30 is *in step* for
        // it, and the drift measurement says so without any zone database being consulted.
        var drift = Measure(At("2026-09-09 08:24:30"), expectedOffsetMinutes: 60);

        Assert.False(drift.IsSignificant);
        Assert.Equal(60, drift.ExpectedOffsetMinutes);
        Assert.Equal(new DateTime(2026, 9, 9, 8, 24, 30), drift.ReferenceWallClock);

        // And the same recorder without the expectation reads as an hour out, which is the
        // documented cost of measuring digits rather than instants.
        Assert.True(Measure(At("2026-09-09 08:24:30")).IsSignificant);
    }

    [Theory]
    [InlineData(0, "in step")]
    [InlineData(1, "in step")]
    [InlineData(-30, "30 s behind")]
    [InlineData(3480, "58 min ahead")]
    [InlineData(-3600, "1 h behind")]
    [InlineData(3720, "1 h 2 min ahead")]
    public void DriftReadsTheWayAnOperatorSaysIt(int seconds, string text) =>
        Assert.Equal(text, ClockDrift.Describe(TimeSpan.FromSeconds(seconds)));
}
