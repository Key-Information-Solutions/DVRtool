using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

public class RecordingScheduleTests
{
    private static RecordingSpan Span(DayOfWeek day, int startHour, int endHour, string mode,
        RecordingTrigger triggers) =>
        new(day, TimeSpan.FromHours(startHour), TimeSpan.FromHours(endHour), mode, triggers);

    private static readonly DayOfWeek[] Week =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    private static RecordingSchedule AllWeek(string mode, RecordingTrigger triggers) =>
        new(RecordingState.Scheduled, Week.Select(d => Span(d, 0, 24, mode, triggers)).ToList());

    // ----- summary -----

    [Fact]
    public void Summary_OneModeAllWeek_IsJustTheMode()
    {
        Assert.Equal("Motion", AllWeek("Motion", RecordingTrigger.Motion).Summary);
        Assert.Equal("Continuous", AllWeek("Continuous", RecordingTrigger.Continuous).Summary);
    }

    [Fact]
    public void Summary_OneModePartOfTheWeek_IsStarred_AndTheHoursAreOneCallAway()
    {
        var schedule = new RecordingSchedule(RecordingState.Scheduled,
            Week.Take(5).Select(d => Span(d, 8, 18, "Continuous", RecordingTrigger.Continuous)).ToList());

        // The whole point of the star: this recorder is set to continuous, like the rest of
        // the fleet, but it is not recording 24/7 — and nothing else in the cell says so.
        Assert.Equal("Continuous*", schedule.Summary);
        Assert.Equal("Continuous 50 h/wk; nothing records for 118 h/wk", schedule.HoursText);
        Assert.True(schedule.HasDeadTime);
        Assert.Equal(TimeSpan.FromHours(50), schedule.CoveredTime);
        Assert.False(schedule.IsMixed);
        Assert.False(schedule.IsEventOnly);
        Assert.Equal("not recording", schedule.DescribeNow(new DateTime(2026, 9, 5, 12, 0, 0))); // Saturday
    }

    [Fact]
    public void Summary_MixedWeek_JoinsModesWithPlus_EachStarred_MostTimeFirst()
    {
        var spans = new List<RecordingSpan>();
        foreach (var d in Week.Take(5))
        {
            spans.Add(Span(d, 0, 8, "Motion", RecordingTrigger.Motion));
            spans.Add(new RecordingSpan(d, TimeSpan.FromHours(8), TimeSpan.FromHours(17.5), "Continuous",
                RecordingTrigger.Continuous));
            spans.Add(new RecordingSpan(d, TimeSpan.FromHours(17.5), TimeSpan.FromHours(24), "Motion",
                RecordingTrigger.Motion));
        }
        spans.Add(Span(DayOfWeek.Saturday, 0, 24, "Motion | Alarm", RecordingTrigger.Motion | RecordingTrigger.Alarm));
        spans.Add(Span(DayOfWeek.Sunday, 0, 24, "Motion | Alarm", RecordingTrigger.Motion | RecordingTrigger.Alarm));
        var schedule = new RecordingSchedule(RecordingState.Scheduled, spans);

        Assert.Equal("Motion* + Motion | Alarm* + Continuous*", schedule.Summary);
        // The week is fully covered even though no single mode covers it — that is exactly the
        // difference the stars make readable, and why dead air is a separate question.
        Assert.Equal(RecordingSchedule.FullWeek, schedule.CoveredTime);
        Assert.False(schedule.HasDeadTime);
        Assert.Equal("Motion 72.5 h/wk, Motion | Alarm 48 h/wk, Continuous 47.5 h/wk",
            schedule.HoursText);
        Assert.True(schedule.IsMixed);
        Assert.Equal(3, schedule.TimePerMode.Count);
        Assert.Equal("Motion", schedule.TimePerMode[0].Mode);
    }

    [Fact]
    public void Summary_ModeSplitAcrossTheWeekButCoveringAllOfIt_IsNotStarred()
    {
        // Two entries for the same mode that together are the whole week: it IS 24/7
        // continuous, however the recorder chose to write it down.
        var schedule = new RecordingSchedule(RecordingState.Scheduled,
            Week.SelectMany(d => new[]
            {
                Span(d, 0, 9, "Continuous", RecordingTrigger.Continuous),
                Span(d, 9, 24, "Continuous", RecordingTrigger.Continuous),
            }).ToList());

        Assert.Equal("Continuous", schedule.Summary);
        Assert.False(schedule.HasDeadTime);
    }

    [Fact]
    public void CoveredTime_CountsOverlappingSpansOnce()
    {
        // A recorder that answers with two entries over the same hours must not read as more
        // than a week of coverage — summing would have said 240 hours and dropped the star.
        var schedule = new RecordingSchedule(RecordingState.Scheduled,
            Week.SelectMany(d => new[]
            {
                Span(d, 0, 10, "Motion", RecordingTrigger.Motion),
                Span(d, 5, 20, "Motion", RecordingTrigger.Motion),
            }).ToList());

        Assert.Equal(TimeSpan.FromHours(140), schedule.CoveredTime);
        Assert.Equal("Motion*", schedule.Summary);
        Assert.True(schedule.HasDeadTime);
        Assert.Equal(TimeSpan.FromHours(28), schedule.DeadTime);
    }

    [Fact]
    public void Summary_MoreThanThreeModes_TruncatesWithAnEllipsis()
    {
        var schedule = new RecordingSchedule(RecordingState.Scheduled,
        [
            Span(DayOfWeek.Monday, 0, 24, "A", RecordingTrigger.Motion),
            Span(DayOfWeek.Tuesday, 0, 24, "B", RecordingTrigger.Alarm),
            Span(DayOfWeek.Wednesday, 0, 24, "C", RecordingTrigger.Analytics),
            Span(DayOfWeek.Thursday, 0, 12, "D", RecordingTrigger.Pos),
        ]);

        Assert.Equal("A* + B* + C* + …", schedule.Summary);
    }

    [Fact]
    public void Summary_StatesInFrontOfTheSchedule()
    {
        Assert.Equal("Off", RecordingSchedule.Off.Summary);
        Assert.Equal("Continuous (manual)", RecordingSchedule.Manual.Summary);
        Assert.Equal("Off (nothing scheduled)", new RecordingSchedule(RecordingState.Scheduled, []).Summary);

        // Manual recording is the whole week by definition, and "off" has no week to have a
        // gap in — neither may ever earn a star or a dead-air warning.
        Assert.Equal(RecordingSchedule.FullWeek, RecordingSchedule.Manual.CoveredTime);
        Assert.False(RecordingSchedule.Manual.HasDeadTime);
        Assert.False(RecordingSchedule.Off.HasDeadTime);
        Assert.False(new RecordingSchedule(RecordingState.Scheduled, []).HasDeadTime);

        // Off wins over whatever the cells say.
        var off = new RecordingSchedule(RecordingState.Off, AllWeek("Continuous", RecordingTrigger.Continuous).Spans);
        Assert.Equal("Off", off.Summary);
        Assert.Empty(off.RecordingSpans);
        Assert.False(off.RecordsAnything);
        Assert.True(RecordingSchedule.Manual.RecordsAnything);
    }

    // ----- classification -----

    [Fact]
    public void IsEventOnly_TrueWithoutAnyContinuousSpan_LowResContinuousStillCounts()
    {
        Assert.True(AllWeek("Motion", RecordingTrigger.Motion).IsEventOnly);
        Assert.True(AllWeek("Motion & low-res always",
            RecordingTrigger.Motion | RecordingTrigger.LowResContinuous).IsEventOnly);
        Assert.False(AllWeek("Continuous | Motion", RecordingTrigger.Continuous | RecordingTrigger.Motion).IsEventOnly);
        Assert.False(RecordingSchedule.Off.IsEventOnly);
        Assert.False(RecordingSchedule.Manual.IsEventOnly);
        Assert.False(new RecordingSchedule(RecordingState.Scheduled, []).IsEventOnly);
    }

    [Fact]
    public void ZeroLengthAndTriggerlessSpans_AreNotRecordingSpans()
    {
        var schedule = new RecordingSchedule(RecordingState.Scheduled,
        [
            Span(DayOfWeek.Monday, 8, 8, "Motion", RecordingTrigger.Motion),
            Span(DayOfWeek.Monday, 8, 12, "nothing", RecordingTrigger.None),
            Span(DayOfWeek.Monday, 12, 18, "Motion", RecordingTrigger.Motion),
        ]);

        var only = Assert.Single(schedule.RecordingSpans);
        Assert.Equal(TimeSpan.FromHours(12), only.Start);
        Assert.Equal("Motion*", schedule.Summary);
    }

    // ----- now -----

    [Fact]
    public void DescribeNow_FollowsARunOfSameModeSpans_AcrossMidnight()
    {
        var spans = new List<RecordingSpan>();
        foreach (var d in Week)
        {
            spans.Add(Span(d, 0, 6, "Motion", RecordingTrigger.Motion));
            spans.Add(Span(d, 6, 22, "Continuous", RecordingTrigger.Continuous));
            spans.Add(Span(d, 22, 24, "Motion", RecordingTrigger.Motion));
        }
        var schedule = new RecordingSchedule(RecordingState.Scheduled, spans);

        var wednesday = new DateTime(2026, 9, 2);
        Assert.Equal("Continuous (until 22:00)", schedule.DescribeNow(wednesday.AddHours(12)));
        // 22:00 tonight runs into 06:00 tomorrow as one run of Motion.
        Assert.Equal("Motion (until Thu 06:00)", schedule.DescribeNow(wednesday.AddHours(23)));
        Assert.Equal("Motion (until 06:00)", schedule.DescribeNow(wednesday.AddHours(2)));
        Assert.Equal(TimeSpan.FromHours(6), schedule.SpanAt(wednesday.AddHours(12))!.Start);
    }

    [Fact]
    public void DescribeNow_WholeWeekOneMode_NamesNoEnd()
    {
        Assert.Equal("Motion", AllWeek("Motion", RecordingTrigger.Motion).DescribeNow(DateTime.Now));
        Assert.Equal("off", RecordingSchedule.Off.DescribeNow(DateTime.Now));
        Assert.Equal("Continuous (manual)", RecordingSchedule.Manual.DescribeNow(DateTime.Now));
    }

    // ----- the week laid out -----

    [Fact]
    public void DescribeWeek_MergesIdenticalConsecutiveDays_MondayFirst()
    {
        var spans = new List<RecordingSpan>();
        foreach (var d in Week)
        {
            if (d is DayOfWeek.Wednesday)
                continue; // nothing on Wednesdays
            if (d is DayOfWeek.Saturday or DayOfWeek.Sunday)
                spans.Add(Span(d, 0, 24, "Motion", RecordingTrigger.Motion));
            else
            {
                spans.Add(Span(d, 9, 17, "Continuous", RecordingTrigger.Continuous));
                spans.Add(Span(d, 0, 9, "Motion", RecordingTrigger.Motion)); // out of order on purpose
            }
        }
        var schedule = new RecordingSchedule(RecordingState.Scheduled, spans);

        Assert.Equal(
        [
            "Mon–Tue  00:00–09:00 Motion; 09:00–17:00 Continuous",
            "Wed      —",
            "Thu–Fri  00:00–09:00 Motion; 09:00–17:00 Continuous",
            "Sat–Sun  00:00–24:00 Motion",
        ], schedule.DescribeWeek());
    }

    // ----- parser helpers -----

    [Theory]
    [InlineData("00:00:00", 0)]
    [InlineData("08:30", 8.5)]
    [InlineData("23:59:59", 23.999722)]
    [InlineData("24:00:00", 24)]
    public void TryParseTimeOfDay_AcceptsClockTimesIncludingMidnightAtTheEnd(string text, double hours)
    {
        Assert.True(RecordingSchedule.TryParseTimeOfDay(text, out var value));
        Assert.Equal(hours, value.TotalHours, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("24:00:01")]
    [InlineData("25:00:00")]
    [InlineData("12:60:00")]
    [InlineData("noon")]
    [InlineData("12")]
    public void TryParseTimeOfDay_RejectsWhatIsNotAClockTime(string text) =>
        Assert.False(RecordingSchedule.TryParseTimeOfDay(text, out _));

    [Fact]
    public void SpansBetween_SplitsAtMidnight_WrapsTheWeekEnd_IgnoresAnEmptyRange()
    {
        var m = RecordingTrigger.Motion;

        // A whole Monday, written as Hikvision does: to 00:00 of Tuesday.
        var monday = RecordingSchedule.SpansBetween(DayOfWeek.Monday, TimeSpan.Zero, DayOfWeek.Tuesday,
            TimeSpan.Zero, "Motion", m).ToList();
        Assert.Equal([new RecordingSpan(DayOfWeek.Monday, TimeSpan.Zero, RecordingSchedule.EndOfDay, "Motion", m)],
            monday);

        // Sunday to "Monday 00:00" is the end of the week, not seven days.
        var sunday = RecordingSchedule.SpansBetween(DayOfWeek.Sunday, TimeSpan.Zero, DayOfWeek.Monday,
            TimeSpan.Zero, "Motion", m).ToList();
        Assert.Equal([new RecordingSpan(DayOfWeek.Sunday, TimeSpan.Zero, RecordingSchedule.EndOfDay, "Motion", m)],
            sunday);

        // Across midnight: two spans.
        var night = RecordingSchedule.SpansBetween(DayOfWeek.Monday, TimeSpan.FromHours(22), DayOfWeek.Tuesday,
            TimeSpan.FromHours(2), "Motion", m).ToList();
        Assert.Equal(
        [
            new RecordingSpan(DayOfWeek.Monday, TimeSpan.FromHours(22), RecordingSchedule.EndOfDay, "Motion", m),
            new RecordingSpan(DayOfWeek.Tuesday, TimeSpan.Zero, TimeSpan.FromHours(2), "Motion", m),
        ], night);

        // Same day, part of it.
        var morning = RecordingSchedule.SpansBetween(DayOfWeek.Monday, TimeSpan.Zero, DayOfWeek.Monday,
            TimeSpan.FromHours(8), "Motion", m).ToList();
        Assert.Equal([new RecordingSpan(DayOfWeek.Monday, TimeSpan.Zero, TimeSpan.FromHours(8), "Motion", m)],
            morning);

        // Start and end coincide: nothing, not a whole week.
        Assert.Empty(RecordingSchedule.SpansBetween(DayOfWeek.Monday, TimeSpan.FromHours(8), DayOfWeek.Monday,
            TimeSpan.FromHours(8), "Motion", m));
    }

    [Fact]
    public void FormatClock_ShowsMidnightAtTheEndAsTwentyFour()
    {
        Assert.Equal("00:00", RecordingSchedule.FormatClock(TimeSpan.Zero));
        Assert.Equal("08:30", RecordingSchedule.FormatClock(TimeSpan.FromHours(8.5)));
        Assert.Equal("24:00", RecordingSchedule.FormatClock(RecordingSchedule.EndOfDay));
    }
}
