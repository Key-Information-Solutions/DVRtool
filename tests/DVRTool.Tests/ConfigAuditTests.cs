using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The fleet clock audit: a verdict per recorder, and the carried-failure rule. Pure
/// aggregation, so every case here is a fixture — which is the point of it living in Core.
/// </summary>
public class ConfigAuditTests
{
    private static readonly DateTimeOffset Reference =
        new(2026, 9, 9, 7, 24, 30, TimeSpan.FromHours(-4));

    private static ClockAuditRow Row(string name, string wall, TimeSpan? declaredOffset = null,
        string? zone = null, bool? dst = null, bool? ntp = null, string ntpDetail = "")
    {
        var clock = new DeviceClock(DateTime.Parse(wall), declaredOffset, zone, dst);
        return ClockAuditRow.For(name, clock,
            ClockDrift.Measure(clock, Reference, Reference.AddSeconds(1)),
            new TimeSourceStatus(ntp, ntpDetail));
    }

    [Fact]
    public void AnHourOutWithDstOff_NamesTheCause()
    {
        // The Dahua recorder as read: NTP enabled and syncing happily, DSTEnable false, a
        // zone that observes DST. The number alone would send someone to check the NTP
        // server, which is working.
        var row = Row("Site B", "2026-09-09 06:24:26", zone: "25 (Easterntime)", dst: false,
            ntp: true, ntpDetail: "time.windows.com, every 60 min");

        Assert.True(row.IsFault);
        Assert.Equal("1 h behind; NTP is syncing; DST is disabled", row.Verdict);
        Assert.Equal("25 (Easterntime)", row.ZoneText);
    }

    [Fact]
    public void TwoHoursOutDoesNotBorrowTheDstExplanation()
    {
        // DST is worth an hour, and only an hour. A recorder two hours out has a different
        // problem and must not be handed this diagnosis.
        var row = Row("Site X", "2026-09-09 05:24:30", dst: false, ntp: true);

        Assert.Equal("2 h behind", row.Verdict);
    }

    [Fact]
    public void ClockRightButOffsetWrong_IsAReportNotAFault()
    {
        var row = Row("the lab recorder", "2026-09-09 07:24:29",
            declaredOffset: TimeSpan.FromHours(-5),
            zone: "CST+5:00:00DST01:00:00,M3.2.0/02:00:00,M11.1.0/02:00:00", ntp: true);

        Assert.Equal("clock right, reports the wrong offset", row.Verdict);
        Assert.False(row.IsFault);
    }

    [Fact]
    public void NtpOff_IsAFaultWhateverTheClockReadsToday()
    {
        var row = Row("Site F", "2026-09-09 07:24:30", ntp: false);

        Assert.Equal("no time source", row.Verdict);
        Assert.True(row.IsFault);
    }

    [Fact]
    public void ADriftingRecorderWithNoTimeSourceSaysBoth()
    {
        var row = Row("Site G", "2026-09-09 07:10:30", ntp: false);

        Assert.Equal("14 min behind; no time source", row.Verdict);
    }

    [Fact]
    public void Nx_GetsTheDriftAndNoNtpOpinion()
    {
        // Nx has no NTP client to have an opinion about — null, never "off" — so a healthy
        // Nx server's verdict is empty rather than a complaint.
        var row = Row("Site D", "2026-09-09 07:24:29", ntp: null,
            ntpDetail: "VMS clock sync on, no clock master (follows the internet)");

        Assert.Equal("", row.Verdict);
        Assert.False(row.IsFault);
    }

    [Fact]
    public void AnUnreadableRecorderIsARowWithAnError_NeverOmittedAndNeverFine()
    {
        var audit = ConfigAudit.Build([
            Row("the lab recorder", "2026-09-09 07:24:30", ntp: true),
            ClockAuditRow.Failed("Site H", "401 Unauthorized"),
        ]);

        Assert.Equal(2, audit.Rows.Count);
        Assert.True(audit.IsPartial);
        var failed = Assert.Single(audit.FailedDevices);
        Assert.Equal("Site H", failed.DeviceName);
        Assert.Null(failed.Clock);
        Assert.Contains("unknown, not fine", failed.Verdict);

        // A failed row is not a fault row: nothing is known about that clock, and counting it
        // as broken would be as wrong as counting it as fine.
        Assert.Empty(audit.Faults);
        Assert.Contains("PARTIAL", audit.Summary);
        Assert.Contains("1 could not be read", audit.Summary);
    }

    [Fact]
    public void AnAuditWhereNothingAnsweredDoesNotClaimEveryClockIsFine()
    {
        var audit = ConfigAudit.Build([ClockAuditRow.Failed("a", "502 Bad Gateway")]);

        Assert.Equal(
            "no recorder answered. 1 could not be read — those clocks are unknown, not fine.",
            audit.Summary);
    }

    [Fact]
    public void AHealthyFleetSaysSoInOneLine()
    {
        var audit = ConfigAudit.Build([
            Row("a", "2026-09-09 07:24:30", ntp: true),
            Row("b", "2026-09-09 07:24:31", ntp: true),
        ]);

        Assert.False(audit.IsPartial);
        Assert.Empty(audit.Faults);
        Assert.Equal("2 recorder(s) read, every clock within 1 minute.", audit.Summary);
    }

    [Fact]
    public void FaultsAreCountedInTheSummary()
    {
        var audit = ConfigAudit.Build([
            Row("a", "2026-09-09 07:24:30", ntp: true),
            Row("b", "2026-09-09 06:24:26", dst: false, ntp: true),
            Row("c", "2026-09-09 07:24:30", ntp: false),
        ]);

        Assert.Equal(2, audit.Faults.Count());
        Assert.Equal("3 recorder(s) read, 2 with a clock problem.", audit.Summary);
    }

    [Fact]
    public void AnExpectedOffsetIsSaidOutLoud()
    {
        // A recorder measured against another zone reads as in step, and the row says which
        // expectation made it so — the alternative is an operator wondering why an hour of
        // difference is being ignored.
        var clock = new DeviceClock(new DateTime(2026, 9, 9, 8, 24, 30), null, null, null);
        var row = ClockAuditRow.For("Site E", clock,
            ClockDrift.Measure(clock, Reference, Reference.AddSeconds(1), 60),
            new TimeSourceStatus(true));

        Assert.Contains("+60 min from this workstation", row.Verdict);
        Assert.False(row.IsFault);
    }
}
