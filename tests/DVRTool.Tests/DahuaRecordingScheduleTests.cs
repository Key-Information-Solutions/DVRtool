using System.Net;
using System.Text;
using DVRTool.Core;
using DVRTool.Vendors.Dahua;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The recording mode of every channel from the Record table, shaped as Site B
/// answered on 2026-09-02: eight weekday rows (7 is the holiday schedule), six sections per
/// day, a whole day written 00:00:00-23:59:59, mask 39 on every camera.
/// </summary>
public class DahuaRecordingScheduleTests
{
    private static readonly NvrConnection Conn = new()
    {
        Host = "10.0.0.60",
        Username = "admin",
        Password = "secret",
    };

    private const string EncodeConfig = """
        table.Encode[0].MainFormat[0].Video.BitRate=1280
        table.Encode[0].MainFormat[0].Video.BitRateControl=CBR
        table.Encode[0].MainFormat[0].Video.Compression=H.265
        table.Encode[0].MainFormat[0].Video.FPS=15
        table.Encode[0].MainFormat[0].Video.resolution=2688x1520
        table.Encode[0].MainFormat[0].VideoEnable=true
        table.Encode[1].MainFormat[0].Video.BitRate=2048
        table.Encode[1].MainFormat[0].Video.BitRateControl=VBR
        table.Encode[1].MainFormat[0].VideoEnable=true
        table.Encode[2].MainFormat[0].Video.BitRate=4096
        table.Encode[2].MainFormat[0].Video.BitRateControl=CBR
        table.Encode[2].MainFormat[0].VideoEnable=true
        table.Encode[3].MainFormat[0].Video.BitRate=1280
        table.Encode[3].MainFormat[0].Video.BitRateControl=CBR
        table.Encode[3].MainFormat[0].VideoEnable=true
        """;

    private const string RecordMode = """
        table.RecordMode[0].Mode=0
        table.RecordMode[0].ModeExtra1=2
        table.RecordMode[0].ModeExtra2=2
        table.RecordMode[1].Mode=0
        table.RecordMode[2].Mode=1
        table.RecordMode[3].Mode=2
        """;

    /// <summary>
    /// Channel 0 as Site B has it (mask 39 all week); channel 1 a business-hours schedule
    /// (continuous 08–18 on weekdays, motion otherwise); channels 2 and 3 the same as 0 but
    /// switched to manual and to stop by RecordMode.
    /// </summary>
    private static string RecordTable()
    {
        var sb = new StringBuilder();
        for (int ch = 0; ch < 4; ch++)
        {
            sb.Append($"table.Record[{ch}].Enable=false\n");
            sb.Append($"table.Record[{ch}].HolidayEnable=true\n");
            sb.Append($"table.Record[{ch}].PreRecord=4\n");
            for (int day = 0; day <= 7; day++)
                for (int section = 0; section < 6; section++)
                    sb.Append($"table.Record[{ch}].TimeSection[{day}][{section}]={Value(ch, day, section)}\n");
        }
        return sb.ToString();
    }

    private static string Value(int ch, int day, int section)
    {
        const string empty = "0 00:00:00-24:00:00";
        if (day == 7)
            return section == 0 ? "1 00:00:00-23:59:59" : empty; // the holiday row is not read
        return ch switch
        {
            1 when day is 0 or 6 => section == 0 ? "2 00:00:00-23:59:59" : empty,
            1 => section switch
            {
                0 => "2 00:00:00-08:00:00",
                1 => "1 08:00:00-18:00:00",
                2 => "2 18:00:00-24:00:00",
                _ => empty,
            },
            _ => section == 0 ? "39 00:00:00-23:59:59" : empty,
        };
    }

    private static MockHttpHandler Server(bool recordAvailable = true) => new((req, _) =>
    {
        string pq = req.RequestUri!.PathAndQuery;
        if (pq.Contains("name=RecordMode", StringComparison.Ordinal))
            return MockHttpHandler.Text(RecordMode);
        if (pq.Contains("name=Record", StringComparison.Ordinal))
            return recordAvailable
                ? MockHttpHandler.Text(RecordTable())
                : MockHttpHandler.Text("Error\n", HttpStatusCode.BadRequest);
        if (pq.Contains("name=Encode", StringComparison.Ordinal))
            return MockHttpHandler.Text(EncodeConfig);
        throw new Xunit.Sdk.XunitException($"unexpected request {pq}");
    });

    [Fact]
    public async Task SiteBMask_AllWeek_ReadsAsContinuousPlusEventTags_HolidayRowIgnored()
    {
        using var client = new DahuaClient(Conn, Server());

        var ch1 = (await client.GetMainStreamsAsync())[0];
        var schedule = ch1.Schedule!;

        Assert.True(ch1.Enabled);
        Assert.Equal("Continuous | Motion | Alarm | MD&Alarm", ch1.RecordingText);
        Assert.Equal(7, schedule.RecordingSpans.Count);
        Assert.All(schedule.RecordingSpans, s =>
        {
            Assert.Equal(TimeSpan.Zero, s.Start);
            Assert.Equal(RecordingSchedule.EndOfDay, s.End); // 23:59:59 is Dahua's end of day
            Assert.True(s.Triggers.HasFlag(RecordingTrigger.Continuous));
        });
        Assert.False(schedule.IsEventOnly);
        Assert.False(schedule.IsMixed);
        Assert.Equal(["Mon–Sun  00:00–24:00 Continuous | Motion | Alarm | MD&Alarm"],
            schedule.DescribeWeek());
    }

    [Fact]
    public async Task BusinessHours_SundayIsDayZero_SumsHoursPerMode()
    {
        using var client = new DahuaClient(Conn, Server());

        var ch2 = (await client.GetMainStreamsAsync())[1];
        var schedule = ch2.Schedule!;

        // Motion: 2 × 24 h at the weekend + 5 × 14 h on weekdays; Continuous: 5 × 10 h.
        Assert.Equal("Motion 118h, Continuous 50h", ch2.RecordingText);
        Assert.True(schedule.IsMixed);
        Assert.False(schedule.IsEventOnly);
        Assert.Equal(
        [
            "Mon–Fri  00:00–08:00 Motion; 08:00–18:00 Continuous; 18:00–24:00 Motion",
            "Sat–Sun  00:00–24:00 Motion",
        ], schedule.DescribeWeek());

        var wednesday = new DateTime(2026, 9, 2);
        Assert.Equal("Continuous (until 18:00)", schedule.DescribeNow(wednesday.AddHours(10)));
        Assert.Equal("Motion (until 08:00)", schedule.DescribeNow(wednesday.AddHours(3)));
        // Saturday's motion runs through Sunday to Monday 08:00.
        Assert.Equal("Motion (until Mon 08:00)", schedule.DescribeNow(wednesday.AddDays(3).AddHours(10)));
    }

    [Fact]
    public async Task RecordMode_ManualForcesContinuous_StopSwitchesOff()
    {
        using var client = new DahuaClient(Conn, Server());

        var streams = await client.GetMainStreamsAsync();
        var manual = streams[2];
        var stopped = streams[3];

        Assert.Equal(RecordingState.ManualContinuous, manual.Schedule!.State);
        Assert.Equal("Continuous (manual)", manual.RecordingText);
        Assert.True(manual.Schedule.RecordsAnything);
        Assert.True(manual.Enabled);
        Assert.Equal("Continuous (manual)", manual.Schedule.DescribeNow(DateTime.Now));

        Assert.Equal(RecordingState.Off, stopped.Schedule!.State);
        Assert.Equal("Off", stopped.RecordingText);
        Assert.False(stopped.Enabled);
    }

    [Fact]
    public async Task RecordTableUnavailable_LeavesTheModeUnknown()
    {
        using var client = new DahuaClient(Conn, Server(recordAvailable: false));

        var streams = await client.GetMainStreamsAsync();

        Assert.Equal(4, streams.Count);
        Assert.All(streams, s => Assert.Null(s.Schedule));
        Assert.All(streams, s => Assert.Equal("?", s.RecordingText));
        Assert.True(streams[0].Enabled);
        Assert.False(streams[3].Enabled); // RecordMode 2 still switches the channel off
    }

    [Theory]
    [InlineData(1, "Continuous", RecordingTrigger.Continuous)]
    [InlineData(2, "Motion", RecordingTrigger.Motion)]
    [InlineData(6, "Motion | Alarm", RecordingTrigger.Motion | RecordingTrigger.Alarm)] // the spec's own example
    [InlineData(16, "Intel", RecordingTrigger.Analytics)]
    [InlineData(64, "POS", RecordingTrigger.Pos)]
    [InlineData(39, "Continuous | Motion | Alarm | MD&Alarm",
        RecordingTrigger.Continuous | RecordingTrigger.Motion | RecordingTrigger.Alarm)]
    [InlineData(128, "bit 7", RecordingTrigger.Other)]
    public void RecordMask_BitsAreTheRecordTypes(int mask, string mode, RecordingTrigger triggers)
    {
        var described = DahuaClient.DescribeRecordMask(mask);
        Assert.Equal(mode, described.Mode);
        Assert.Equal(triggers, described.Triggers);
    }

    [Fact]
    public void TimeSection_ParsesMaskAndRange_LastSecondIsEndOfDay()
    {
        Assert.True(DahuaClient.TryParseTimeSection("39 00:00:00-23:59:59", out int mask, out var start, out var end));
        Assert.Equal(39, mask);
        Assert.Equal(TimeSpan.Zero, start);
        Assert.Equal(RecordingSchedule.EndOfDay, end);

        Assert.True(DahuaClient.TryParseTimeSection("1 08:00:00-18:00:00", out mask, out start, out end));
        Assert.Equal(1, mask);
        Assert.Equal(TimeSpan.FromHours(8), start);
        Assert.Equal(TimeSpan.FromHours(18), end);

        Assert.True(DahuaClient.TryParseTimeSection("0 00:00:00-24:00:00", out mask, out _, out end));
        Assert.Equal(0, mask);
        Assert.Equal(RecordingSchedule.EndOfDay, end);

        Assert.False(DahuaClient.TryParseTimeSection("garbage", out _, out _, out _));
        Assert.False(DahuaClient.TryParseTimeSection("1 25:00:00-26:00:00", out _, out _, out _));
    }
}
