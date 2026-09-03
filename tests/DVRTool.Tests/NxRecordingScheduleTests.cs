using System.Text.Json;
using DVRTool.Core;
using DVRTool.Vendors.NxWitness;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The recording mode of an Nx / DW Spectrum camera from its schedule cells — the three the
/// DW client offers (Always, Motion, Motion + Lo-Res), the object-detection variants Nx 5
/// added, and the legacy spellings.
/// </summary>
public class NxRecordingScheduleTests
{
    private static RecordingSchedule ScheduleOf(string schedule)
    {
        using var doc = JsonDocument.Parse($$"""
            {
              "id": "{aaaaaaaa-0000-0000-0000-000000000001}",
              "name": "Yard",
              "deviceType": "Camera",
              "status": "Recording",
              "schedule": {{schedule}},
              "mediaStreams": [{"codec": 27, "encoderIndex": 0, "resolution": "1920x1080"}]
            }
            """);
        var camera = NxCamera.Parse(doc.RootElement)!;
        return NxWitnessClient.ToCameraStream(camera, 1).Schedule!;
    }

    private static string Cell(int day, int start, int end, string type, string metadata = "none") =>
        $$"""{"dayOfWeek": {{day}}, "startTime": {{start}}, "endTime": {{end}}, "recordingType": "{{type}}", "metadataTypes": "{{metadata}}", "streamQuality": "high", "fps": 15, "bitrateKbps": 0}""";

    [Fact]
    public void MotionPlusLowRes_AllWeek_IsTheDwSitesUsualMode_AndEventOnlyForTheMainStream()
    {
        var cells = string.Join(",", Enumerable.Range(1, 7)
            .Select(d => Cell(d, 0, 86_400, "metadataAndLowQuality", "motion")));
        var schedule = ScheduleOf($$"""{"isEnabled": true, "tasks": [{{cells}}]}""");

        Assert.Equal("Motion & low-res always", schedule.Summary);
        Assert.True(schedule.IsEventOnly);
        Assert.False(schedule.IsMixed);
        Assert.Equal(7, schedule.RecordingSpans.Count);
        Assert.All(schedule.RecordingSpans, s => Assert.Equal(
            RecordingTrigger.Motion | RecordingTrigger.LowResContinuous, s.Triggers));
        Assert.Equal(["Mon–Sun  00:00–24:00 Motion & low-res always"], schedule.DescribeWeek());
        Assert.Equal("Motion & low-res always", schedule.DescribeNow(new DateTime(2026, 9, 2, 9, 0, 0)));
    }

    [Fact]
    public void BusinessHours_DayOneIsMonday_SevenIsSunday_HoursPerMode()
    {
        var weekdays = Enumerable.Range(1, 5).SelectMany(d => new[]
        {
            Cell(d, 0, 28_800, "metadataAndLowQuality", "motion"),
            Cell(d, 28_800, 64_800, "always"),
            Cell(d, 64_800, 86_400, "metadataAndLowQuality", "motion"),
        });
        var weekend = new[] { 6, 7 }.Select(d => Cell(d, 0, 86_400, "metadataOnly", "motion|objects"));
        var schedule = ScheduleOf(
            $$"""{"isEnabled": true, "tasks": [{{string.Join(",", weekdays.Concat(weekend))}}]}""");

        Assert.Equal("Motion & low-res always* + Continuous* + Motion | Objects*", schedule.Summary);
        Assert.Equal("Motion & low-res always 70 h/wk, Continuous 50 h/wk, Motion | Objects 48 h/wk",
            schedule.HoursText);
        Assert.False(schedule.HasDeadTime);
        Assert.True(schedule.IsMixed);
        Assert.False(schedule.IsEventOnly);
        Assert.Equal(
        [
            "Mon–Fri  00:00–08:00 Motion & low-res always; 08:00–18:00 Continuous; 18:00–24:00 Motion & low-res always",
            "Sat–Sun  00:00–24:00 Motion | Objects",
        ], schedule.DescribeWeek());

        var saturday = new DateTime(2026, 9, 5, 10, 0, 0);
        Assert.Equal("Motion | Objects (until Sun 24:00)", schedule.DescribeNow(saturday));
        Assert.Equal("Continuous (until 18:00)", schedule.DescribeNow(new DateTime(2026, 9, 2, 12, 0, 0)));
    }

    [Fact]
    public void DisabledSchedule_IsOff_WhateverTheCellsSay()
    {
        var schedule = ScheduleOf($$"""{"isEnabled": false, "tasks": [{{Cell(1, 0, 86_400, "always")}}]}""");

        Assert.Equal(RecordingState.Off, schedule.State);
        Assert.Equal("Off", schedule.Summary);
        Assert.False(schedule.RecordsAnything);
    }

    [Fact]
    public void NeverCells_AreWhiteSpace_AndAnAllNeverWeekRecordsNothing()
    {
        var schedule = ScheduleOf($$"""
            {"isEnabled": true, "tasks": [
              {{Cell(1, 0, 86_400, "never")}},
              {{Cell(2, 0, 43_200, "never")}},
              {{Cell(2, 43_200, 86_400, "always")}}
            ]}
            """);
        Assert.Equal("Continuous*", schedule.Summary);
        Assert.Equal("Continuous 12 h/wk; nothing records for 156 h/wk", schedule.HoursText);
        Assert.Single(schedule.RecordingSpans);
        Assert.Equal(DayOfWeek.Tuesday, schedule.RecordingSpans[0].Day);

        var nothing = ScheduleOf($$"""{"isEnabled": true, "tasks": [{{Cell(1, 0, 86_400, "never")}}]}""");
        Assert.Equal("Off (nothing scheduled)", nothing.Summary);
        Assert.False(nothing.RecordsAnything);
    }

    [Theory]
    [InlineData("always", "none", "Continuous", RecordingTrigger.Continuous)]
    [InlineData("metadataOnly", "motion", "Motion", RecordingTrigger.Motion)]
    [InlineData("metadataOnly", "objects", "Objects", RecordingTrigger.Analytics)]
    [InlineData("metadataOnly", "motion|objects", "Motion | Objects", RecordingTrigger.Motion | RecordingTrigger.Analytics)]
    [InlineData("metadataOnly", "none", "Motion", RecordingTrigger.Motion)]
    [InlineData("RT_MotionOnly", "", "Motion", RecordingTrigger.Motion)]
    [InlineData("RT_MotionAndLowQuality", "", "Motion & low-res always", RecordingTrigger.Motion | RecordingTrigger.LowResContinuous)]
    [InlineData("metadataAndLowQuality", "objects", "Objects & low-res always", RecordingTrigger.Analytics | RecordingTrigger.LowResContinuous)]
    public void CellTypes_ReadAsTheDwClientNamesThem(string type, string metadata, string mode,
        RecordingTrigger triggers)
    {
        var task = new NxScheduleTask(1, 0, 86_400,
            NxScheduleTask.NormalizeRecordingType(type), "high", 15, 0,
            NxScheduleTask.NormalizeMetadataTypes(metadata));

        var described = NxWitnessClient.DescribeTask(task);

        Assert.Equal(mode, described.Mode);
        Assert.Equal(triggers, described.Triggers);
    }

    [Theory]
    [InlineData(1, DayOfWeek.Monday)]
    [InlineData(6, DayOfWeek.Saturday)]
    [InlineData(7, DayOfWeek.Sunday)]
    public void WeekdayNumbers_FollowQt(int nx, DayOfWeek day) =>
        Assert.Equal(day, NxWitnessClient.NxDay(nx));
}
