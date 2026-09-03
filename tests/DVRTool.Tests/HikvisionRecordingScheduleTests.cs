using System.Net;
using DVRTool.Core;
using DVRTool.Vendors.Hikvision;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The recording mode of every camera from the RaCM track list. Shapes are as four live
/// recorders answered on 2026-09-02 (the lab recorder, Site C, Site F, Site E): whole days
/// written "Monday 00:00 → Tuesday 00:00", the on/off switch in the vendor extension, third
/// streams with an empty schedule block.
/// </summary>
public class HikvisionRecordingScheduleTests
{
    private static readonly NvrConnection Conn = new()
    {
        Host = "192.0.2.10",
        Username = "admin",
        Password = "p@ss:word",
    };

    private const string Ns = "http://www.isapi.org/ver20/XMLSchema";

    private static string Streams => $"""
        <StreamingChannelList version="2.0" xmlns="{Ns}">
        <StreamingChannel><id>101</id><enabled>true</enabled>
          <Video><enabled>true</enabled><videoCodecType>H.265</videoCodecType>
          <videoQualityControlType>VBR</videoQualityControlType><vbrUpperCap>8192</vbrUpperCap>
          <maxFrameRate>3000</maxFrameRate></Video></StreamingChannel>
        <StreamingChannel><id>102</id><enabled>true</enabled>
          <Video><enabled>true</enabled><vbrUpperCap>512</vbrUpperCap></Video></StreamingChannel>
        <StreamingChannel><id>201</id><enabled>true</enabled>
          <Video><enabled>true</enabled><videoQualityControlType>VBR</videoQualityControlType>
          <vbrUpperCap>3072</vbrUpperCap><maxFrameRate>3000</maxFrameRate></Video></StreamingChannel>
        <StreamingChannel><id>301</id><enabled>true</enabled>
          <Video><enabled>true</enabled><videoQualityControlType>VBR</videoQualityControlType>
          <vbrUpperCap>3072</vbrUpperCap><maxFrameRate>3000</maxFrameRate></Video></StreamingChannel>
        <StreamingChannel><id>401</id><enabled>true</enabled>
          <Video><enabled>true</enabled><videoQualityControlType>VBR</videoQualityControlType>
          <vbrUpperCap>3072</vbrUpperCap><maxFrameRate>3000</maxFrameRate></Video></StreamingChannel>
        </StreamingChannelList>
        """;

    private static string Action(int id, string startDay, string start, string endDay, string end,
        string mode, bool record = true) => $"""
        <ScheduleAction><id>{id}</id>
          <ScheduleActionStartTime><DayOfWeek>{startDay}</DayOfWeek><TimeOfDay>{start}</TimeOfDay></ScheduleActionStartTime>
          <ScheduleActionEndTime><DayOfWeek>{endDay}</DayOfWeek><TimeOfDay>{end}</TimeOfDay></ScheduleActionEndTime>
          <ScheduleDSTEnable>true</ScheduleDSTEnable><Description>nothing</Description>
          <Actions><Record>{(record ? "true" : "false")}</Record><Log>false</Log><SaveImg>false</SaveImg>
            <ActionRecordingMode>{mode}</ActionRecordingMode></Actions>
        </ScheduleAction>
        """;

    /// <summary>Seven whole days the way the recorders write them: each ends at 00:00 of the next day.</summary>
    private static string WholeWeek(string mode)
    {
        string[] days = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "Monday"];
        return string.Concat(Enumerable.Range(0, 7)
            .Select(i => Action(i + 1, days[i], "00:00:00", days[i + 1], "00:00:00", mode)));
    }

    private static string Track(int id, string actions, bool enableSchedule = true) => $$"""
        <Track>
        <id>{{id}}</id><Channel>{{id}}</Channel><Enable>false</Enable>
        <Description>trackType=standard,contentType=video</Description>
        <DefaultRecordingMode>CMR</DefaultRecordingMode>
        <TrackSchedule><ScheduleBlockList><ScheduleBlock>
          <ScheduleBlockGUID>{00000000-0000-0000-0000-000000000000}</ScheduleBlockGUID>
          <ScheduleBlockType>www.hikvision.com/racm/schedule/ver10</ScheduleBlockType>
          {{actions}}
        </ScheduleBlock></ScheduleBlockList></TrackSchedule>
        <CustomExtensionList><CustomExtension>
          <CustomExtensionName>www.hikvision.com/RaCM/trackExt/ver10</CustomExtensionName>
          <enableSchedule>{{(enableSchedule ? "true" : "false")}}</enableSchedule>
          <HolidaySchedule><ScheduleBlock>
            <ScheduleBlockGUID>{00000000-0000-0000-0000-000000000000}</ScheduleBlockGUID>
            <ScheduleBlockType>www.hikvision.com/racm/schedule/ver10</ScheduleBlockType>
            {{Action(9, "Monday", "00:00:00", "Tuesday", "00:00:00", "ALARM")}}
          </ScheduleBlock></HolidaySchedule>
        </CustomExtension></CustomExtensionList>
        <delayTime>0</delayTime>
        </Track>
        """;

    private static string Tracks => $"""
        <TrackList version="2.0" xmlns="{Ns}">
        {Track(101, WholeWeek("CMR"))}
        {Track(102, WholeWeek("MOTION"))}
        {Track(103, "", enableSchedule: false)}
        {Track(201,
            Action(1, "Monday", "00:00:00", "Monday", "08:00:00", "EDR") +
            Action(2, "Monday", "08:00:00", "Tuesday", "00:00:00", "CMR") +
            Action(3, "Tuesday", "00:00:00", "Wednesday", "00:00:00", "MOTION") +
            Action(4, "Wednesday", "00:00:00", "Thursday", "00:00:00", "MOTION") +
            Action(5, "Thursday", "00:00:00", "Friday", "00:00:00", "MOTION") +
            Action(6, "Friday", "00:00:00", "Saturday", "00:00:00", "MOTION") +
            Action(7, "Saturday", "00:00:00", "Sunday", "00:00:00", "MOTION") +
            Action(8, "Sunday", "00:00:00", "Monday", "00:00:00", "MOTION") +
            Action(9, "Sunday", "12:00:00", "Sunday", "13:00:00", "ALARM", record: false))}
        {Track(301, WholeWeek("CMR"), enableSchedule: false)}
        {Track(401, "")}
        </TrackList>
        """;

    private static MockHttpHandler Server(bool tracksAvailable = true) => new((req, _) =>
        req.RequestUri!.AbsolutePath switch
        {
            "/ISAPI/Streaming/channels" => MockHttpHandler.Xml(Streams),
            "/ISAPI/ContentMgmt/record/tracks" => tracksAvailable
                ? MockHttpHandler.Xml(Tracks)
                : MockHttpHandler.Text("Not Found", HttpStatusCode.NotFound),
            _ => throw new Xunit.Sdk.XunitException($"unexpected request {req.RequestUri.AbsolutePath}"),
        });

    [Fact]
    public async Task MainStreams_ReadTheTrackList_OneGetForEveryCamera()
    {
        var handler = Server();
        using var client = new HikvisionClient(Conn, handler);

        var streams = await client.GetMainStreamsAsync();

        Assert.Equal(4, streams.Count);
        Assert.Equal("/ISAPI/Streaming/channels", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal("/ISAPI/ContentMgmt/record/tracks", handler.Requests[1].Request.RequestUri!.AbsolutePath);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ContinuousAllWeek_ReadsAsContinuous_DaysWrittenAsNextMidnight()
    {
        using var client = new HikvisionClient(Conn, Server());

        var ch1 = (await client.GetMainStreamsAsync())[0];
        var schedule = ch1.Schedule!;

        Assert.True(ch1.Enabled);
        Assert.Equal("Continuous", ch1.RecordingText);
        Assert.Equal(RecordingState.Scheduled, schedule.State);
        Assert.Equal(7, schedule.RecordingSpans.Count);
        Assert.All(schedule.RecordingSpans, s =>
        {
            Assert.Equal(TimeSpan.Zero, s.Start);
            Assert.Equal(RecordingSchedule.EndOfDay, s.End);
            Assert.Equal(RecordingTrigger.Continuous, s.Triggers);
        });
        // Sunday's action ends on "Monday 00:00:00" and must not vanish or wrap the week.
        Assert.Contains(schedule.RecordingSpans, s => s.Day == DayOfWeek.Sunday);
        Assert.False(schedule.IsEventOnly);
        Assert.False(schedule.IsMixed);
        Assert.Equal(["Mon–Sun  00:00–24:00 Continuous"], schedule.DescribeWeek());
        // The whole week is one run: no "until".
        Assert.Equal("Continuous", schedule.DescribeNow(new DateTime(2026, 9, 2, 10, 0, 0)));
        // The holiday block's ALARM action and the sub-stream track's schedule are not this camera's.
        Assert.DoesNotContain(schedule.RecordingSpans, s => s.Mode != "Continuous");
    }

    [Fact]
    public async Task MixedWeek_SumsHoursPerMode_AndNamesWhatIsInEffectNow()
    {
        using var client = new HikvisionClient(Conn, Server());

        var ch2 = (await client.GetMainStreamsAsync())[1];
        var schedule = ch2.Schedule!;

        // Monday: 8 h "Motion | Alarm" (EDR), 16 h Continuous; six more days of Motion.
        Assert.Equal("Motion 144h, Continuous 16h, Motion | Alarm 8h", ch2.RecordingText);
        Assert.True(schedule.IsMixed);
        Assert.False(schedule.IsEventOnly);
        Assert.Equal(
        [
            "Mon      00:00–08:00 Motion | Alarm; 08:00–24:00 Continuous",
            "Tue–Sun  00:00–24:00 Motion",
        ], schedule.DescribeWeek());

        var monday = new DateTime(2026, 8, 31); // a Monday
        Assert.Equal("Motion | Alarm (until 08:00)", schedule.DescribeNow(monday.AddHours(7)));
        Assert.Equal("Continuous (until 24:00)", schedule.DescribeNow(monday.AddHours(9)));
        // Tuesday's Motion runs through to Sunday midnight before Monday's EDR takes over.
        Assert.Equal("Motion (until Sun 24:00)", schedule.DescribeNow(monday.AddDays(2).AddHours(12)));
        // An action with <Record>false</Record> is not a recording span.
        Assert.DoesNotContain(schedule.RecordingSpans, s => s.Mode == "Alarm");
    }

    [Fact]
    public async Task ScheduleSwitchedOff_OrEmpty_MeansTheCameraRecordsNothing()
    {
        using var client = new HikvisionClient(Conn, Server());

        var streams = await client.GetMainStreamsAsync();
        var ch3 = streams[2];
        var ch4 = streams[3];

        // enableSchedule=false: off, whatever the week holds — and out of the retention total.
        Assert.Equal(RecordingState.Off, ch3.Schedule!.State);
        Assert.Equal("Off", ch3.RecordingText);
        Assert.False(ch3.Enabled);
        Assert.Equal(["Recording is switched off."], ch3.Schedule.DescribeWeek());
        Assert.Equal("off", ch3.Schedule.DescribeNow(DateTime.Now));

        // An enabled schedule with no action at all records nothing either.
        Assert.Equal("Off (nothing scheduled)", ch4.RecordingText);
        Assert.False(ch4.Schedule!.RecordsAnything);
        Assert.False(ch4.Enabled);
        Assert.Equal(["Nothing is scheduled."], ch4.Schedule.DescribeWeek());
    }

    [Fact]
    public async Task TrackListUnavailable_LeavesTheModeUnknown_AndTheStreamsAsTheyWere()
    {
        using var client = new HikvisionClient(Conn, Server(tracksAvailable: false));

        var streams = await client.GetMainStreamsAsync();

        Assert.Equal(4, streams.Count);
        Assert.All(streams, s => Assert.Null(s.Schedule));
        Assert.All(streams, s => Assert.Equal("?", s.RecordingText));
        Assert.All(streams, s => Assert.True(s.Enabled));
    }

    [Theory]
    [InlineData("CMR", "Continuous", RecordingTrigger.Continuous)]
    [InlineData("MOTION", "Motion", RecordingTrigger.Motion)]
    [InlineData("motion", "Motion", RecordingTrigger.Motion)]
    [InlineData("ALARM", "Alarm", RecordingTrigger.Alarm)]
    [InlineData("EDR", "Motion | Alarm", RecordingTrigger.Motion | RecordingTrigger.Alarm)]
    [InlineData("ALARMORMOTION", "Motion | Alarm", RecordingTrigger.Motion | RecordingTrigger.Alarm)]
    [InlineData("ALARMANDMOTION", "Motion & Alarm", RecordingTrigger.Motion | RecordingTrigger.Alarm)]
    [InlineData("AllEvent", "Event", RecordingTrigger.Motion | RecordingTrigger.Alarm | RecordingTrigger.Analytics)]
    [InlineData("FieldDetection", "Intrusion", RecordingTrigger.Analytics)]
    [InlineData("LineDetection", "Line crossing", RecordingTrigger.Analytics)]
    [InlineData("POS", "POS", RecordingTrigger.Pos)]
    [InlineData("pir", "PIR", RecordingTrigger.Alarm)]
    [InlineData("SomethingNew", "SomethingNew", RecordingTrigger.Other)]
    public void RecordingModes_MapTheWebUisVocabulary_UnknownWordsSurviveVerbatim(
        string raw, string mode, RecordingTrigger triggers)
    {
        var mapped = HikvisionClient.MapRecordingMode(raw);
        Assert.Equal(mode, mapped.Mode);
        Assert.Equal(triggers, mapped.Triggers);
    }
}
