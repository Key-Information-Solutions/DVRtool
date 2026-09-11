using System.Net;
using System.Text;
using DVRTool.Core;
using DVRTool.Vendors.Dahua;
using DVRTool.Vendors.Hikvision;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The pure half of playback: pixels to time and back, zoom that keeps the anchor still, footage
/// merged into runs the gap-skip can land on, and a playhead that follows the decoder's clock.
/// </summary>
public class PlaybackTimelineTests
{
    private static readonly DateTime Day = new(2026, 9, 3, 0, 0, 0, DateTimeKind.Unspecified);

    private static RecordingSegment Seg(int startMin, int endMin,
        RecordingType type = RecordingType.Continuous) => new()
    {
        Channel = 1,
        Start = Day.AddMinutes(startMin),
        End = Day.AddMinutes(endMin),
        Type = type,
    };

    // ----- ClipRange (the typed export range) -----

    [Theory]
    [InlineData("14:32:10", 14, 32, 10)]
    [InlineData("14:32", 14, 32, 0)]
    [InlineData("9:05", 9, 5, 0)]
    [InlineData(" 00:00:00 ", 0, 0, 0)]
    [InlineData("23:59:59", 23, 59, 59)]
    public void ClipRange_parses_clock_times_on_the_day(string text, int h, int m, int s)
    {
        Assert.Equal(Day.AddHours(h).AddMinutes(m).AddSeconds(s), ClipRange.ParseTime(Day, text));
    }

    [Fact]
    public void ClipRange_reads_24_00_as_the_end_of_the_day()
    {
        Assert.Equal(Day.AddDays(1), ClipRange.ParseTime(Day, "24:00"));
        Assert.Equal(Day.AddDays(1), ClipRange.ParseTime(Day, "24:00:00"));
        Assert.Null(ClipRange.ParseTime(Day, "24:00:01"));
        Assert.Equal("24:00:00", ClipRange.Format(Day, Day.AddDays(1)));
        Assert.Equal("14:32:10", ClipRange.Format(Day, Day.AddHours(14).AddMinutes(32).AddSeconds(10)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("noon")]
    [InlineData("14")]
    [InlineData("14:5")]
    [InlineData("14:60")]
    [InlineData("25:00")]
    [InlineData("14:32:5")]
    [InlineData("14:32:60")]
    [InlineData("14:32:10:00")]
    [InlineData("-1:00")]
    [InlineData("2026-09-03 14:32")]
    public void ClipRange_rejects_what_is_not_a_time(string text)
    {
        Assert.Null(ClipRange.ParseTime(Day, text));
    }

    [Fact]
    public void ClipRange_pairs_a_start_and_an_end()
    {
        var range = ClipRange.Parse(Day, "14:32:10", "14:35:40", out var error);
        Assert.Null(error);
        Assert.Equal((Day.AddHours(14).AddMinutes(32).AddSeconds(10),
            Day.AddHours(14).AddMinutes(35).AddSeconds(40)), range);
    }

    [Fact]
    public void ClipRange_is_incomplete_without_both_ends_and_silent_about_it()
    {
        Assert.Null(ClipRange.Parse(Day, "14:32", "", out var error));
        Assert.Null(error);
        Assert.Null(ClipRange.Parse(Day, "", "14:35", out error));
        Assert.Null(error);
        Assert.Null(ClipRange.Parse(Day, "", "", out error));
        Assert.Null(error);
    }

    [Fact]
    public void ClipRange_reports_a_reversed_pair_rather_than_swapping_it()
    {
        Assert.Null(ClipRange.Parse(Day, "14:35", "14:32", out var error));
        Assert.Contains("after its start", error);
        Assert.Null(ClipRange.Parse(Day, "14:35", "14:35", out error));
        Assert.Contains("after its start", error);
    }

    [Fact]
    public void ClipRange_names_the_box_that_is_wrong()
    {
        Assert.Null(ClipRange.Parse(Day, "abc", "14:35", out var error));
        Assert.Contains("\"abc\"", error);
        Assert.Null(ClipRange.Parse(Day, "14:30", "14:99", out error));
        Assert.Contains("\"14:99\"", error);
    }

    // ----- TimelineWindow -----

    [Fact]
    public void TimeAt_and_PixelOf_round_trip_across_the_width()
    {
        var w = TimelineWindow.Day(Day);
        var noon = w.TimeAt(500, 1000);
        Assert.Equal(Day.AddHours(12), noon);
        Assert.Equal(500, w.PixelOf(noon, 1000), 6);
        Assert.Equal(Day, w.TimeAt(-40, 1000));          // clamped left
        Assert.Equal(w.End, w.TimeAt(5000, 1000));       // clamped right
    }

    [Fact]
    public void Zoom_in_keeps_the_time_under_the_cursor_where_it_was()
    {
        var w = TimelineWindow.Day(Day);
        var anchor = w.TimeAt(600, 1000);
        var z = w.Zoom(+1, 0.6);
        Assert.Equal(TimeSpan.FromHours(12), z.Length);
        Assert.Equal(anchor, z.TimeAt(600, 1000));
    }

    [Fact]
    public void Zoom_never_leaves_the_day()
    {
        var w = TimelineWindow.Day(Day);
        var z = w.Zoom(+3, 0.0); // 3 h, anchored at the day's first instant
        Assert.Equal(Day, z.Start);
        Assert.Equal(TimeSpan.FromHours(3), z.Length);

        var late = w.Zoom(+3, 1.0); // anchored at the day's end
        Assert.Equal(Day.AddDays(1), late.End);

        Assert.Equal(w, w.Zoom(-1, 0.5)); // already at the widest level
    }

    [Fact]
    public void Pan_is_clamped_to_the_day()
    {
        var w = new TimelineWindow(Day.AddHours(22), TimeSpan.FromHours(1));
        var p = w.Pan(TimeSpan.FromHours(5));
        Assert.Equal(Day.AddHours(23), p.Start);
        Assert.Equal(Day.AddDays(1), p.End);
        Assert.Equal(Day, w.Pan(TimeSpan.FromHours(-30)).Start);
    }

    [Fact]
    public void Ticks_fall_on_round_clock_steps_and_stay_apart()
    {
        var w = TimelineWindow.Day(Day);
        Assert.Equal(TimeSpan.FromHours(2), w.TickInterval(1000));
        var ticks = w.Ticks(1000).ToList();
        Assert.Equal(12, ticks.Count);
        Assert.Equal(Day, ticks[0]);
        Assert.Equal(Day.AddHours(22), ticks[^1]);
        Assert.Equal("HH:mm", w.TickFormat(1000));

        var tight = new TimelineWindow(Day.AddHours(10).AddMinutes(0.5), TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromSeconds(30), tight.TickInterval(1000));
        Assert.Equal("HH:mm:ss", tight.TickFormat(1000));
        Assert.All(tight.Ticks(1000), t => Assert.Equal(0, t.Ticks % TimeSpan.FromSeconds(30).Ticks));
    }

    // ----- FootageCoverage -----

    [Fact]
    public void Merge_joins_file_seams_and_keeps_real_gaps()
    {
        var runs = FootageCoverage.Merge([Seg(60, 120), Seg(120, 180), Seg(400, 410), Seg(0, 30)]);
        Assert.Equal(3, runs.Count);
        Assert.Equal((Day, Day.AddMinutes(30)), (runs[0].Start, runs[0].End));
        Assert.Equal((Day.AddMinutes(60), Day.AddMinutes(180)), (runs[1].Start, runs[1].End));
        Assert.Equal(RecordingType.Continuous, runs[1].Type);
        Assert.Equal(TimeSpan.FromMinutes(160), FootageCoverage.Total(runs));
    }

    [Fact]
    public void Merge_marks_a_run_of_mixed_triggers_Unknown()
    {
        var runs = FootageCoverage.Merge([Seg(0, 10), Seg(10, 20, RecordingType.Motion)]);
        Assert.Single(runs);
        Assert.Equal(RecordingType.Unknown, runs[0].Type);
    }

    [Fact]
    public void NextFootageAt_snaps_into_the_next_run()
    {
        var runs = FootageCoverage.Merge([Seg(60, 120), Seg(300, 360)]);
        Assert.Equal(Day.AddMinutes(90), FootageCoverage.NextFootageAt(runs, Day.AddMinutes(90)));
        Assert.Equal(Day.AddMinutes(300), FootageCoverage.NextFootageAt(runs, Day.AddMinutes(200)));
        Assert.Equal(Day.AddMinutes(60), FootageCoverage.NextFootageAt(runs, Day));
        Assert.Null(FootageCoverage.NextFootageAt(runs, Day.AddMinutes(360)));
        Assert.False(FootageCoverage.Contains(runs, Day.AddMinutes(200)));
        Assert.Equal(Day.AddMinutes(300), FootageCoverage.SpanAt(runs, Day.AddMinutes(301))!.Start);
    }

    // ----- PlaybackResumePlan -----

    // A continuous day is one run, and a body asked for all of it. The interesting cases are
    // all about a body that stops before that run does.
    private static readonly IReadOnlyList<FootageSpan> TwoRuns =
        FootageCoverage.Merge([Seg(0, 240), Seg(300, 480)]);

    [Fact]
    public void Resume_moves_to_the_next_run_when_the_body_played_its_own_run_out()
    {
        var plan = PlaybackResumePlan.After(TwoRuns, Day, Day.AddMinutes(240),
            Day.AddMinutes(240), Day.AddDays(1));

        Assert.Equal(PlaybackResumeKind.Continue, plan.Kind);
        Assert.Equal(Day.AddMinutes(300), plan.At);
    }

    [Fact]
    public void Resume_carries_on_where_the_picture_reached_when_the_body_stopped_early()
    {
        // The bug this covers: resuming from the *end of the request* instead skipped from
        // minute 30 to minute 300 — four and a half hours of footage silently jumped.
        var plan = PlaybackResumePlan.After(TwoRuns, Day, Day.AddMinutes(240),
            Day.AddMinutes(30), Day.AddDays(1));

        Assert.Equal(PlaybackResumeKind.Continue, plan.Kind);
        Assert.Equal(Day.AddMinutes(30), plan.At);
    }

    [Fact]
    public void Resume_treats_the_last_few_seconds_of_a_run_as_having_played_it_out()
    {
        // Otherwise the run's final seconds are re-requested forever, one body per tick.
        var plan = PlaybackResumePlan.After(TwoRuns, Day, Day.AddMinutes(240),
            Day.AddMinutes(240) - TimeSpan.FromSeconds(2), Day.AddDays(1));

        Assert.Equal(PlaybackResumeKind.Continue, plan.Kind);
        Assert.Equal(Day.AddMinutes(300), plan.At);
    }

    [Fact]
    public void Resume_refuses_to_reopen_a_body_that_delivered_nothing()
    {
        var plan = PlaybackResumePlan.After(TwoRuns, Day.AddMinutes(60), Day.AddMinutes(240),
            Day.AddMinutes(60).AddMilliseconds(200), Day.AddDays(1));

        Assert.Equal(PlaybackResumeKind.GaveNothing, plan.Kind);
    }

    [Fact]
    public void Resume_stops_at_the_end_of_the_days_footage()
    {
        var plan = PlaybackResumePlan.After(TwoRuns, Day.AddMinutes(300), Day.AddMinutes(480),
            Day.AddMinutes(480), Day.AddDays(1));

        Assert.Equal(PlaybackResumeKind.Stop, plan.Kind);
    }

    [Fact]
    public void Resume_stops_rather_than_playing_into_the_next_day()
    {
        var runs = FootageCoverage.Merge([Seg(0, 240), Seg(1500, 1560)]);
        var plan = PlaybackResumePlan.After(runs, Day, Day.AddMinutes(240), Day.AddMinutes(240),
            Day.AddMinutes(1440 - 60));

        Assert.Equal(PlaybackResumeKind.Stop, plan.Kind);
    }

    [Fact]
    public void Resume_ignores_a_clock_that_read_past_the_body()
    {
        // The demuxer reads ahead of the picture, so its clock can sit past the window.
        var plan = PlaybackResumePlan.After(TwoRuns, Day, Day.AddMinutes(240),
            Day.AddMinutes(241), Day.AddDays(1));

        Assert.Equal(PlaybackResumeKind.Continue, plan.Kind);
        Assert.Equal(Day.AddMinutes(300), plan.At);
    }

    [Fact]
    public void Resume_with_no_coverage_loaded_stops_instead_of_guessing()
    {
        var plan = PlaybackResumePlan.After([], Day, Day.AddMinutes(240), Day.AddMinutes(240),
            Day.AddDays(1));

        Assert.Equal(PlaybackResumeKind.Stop, plan.Kind);
    }

    // ----- PlaybackClock -----

    [Fact]
    public void Clock_is_anchor_plus_media_time_and_ignores_the_decoders_startup_values()
    {
        var clock = new PlaybackClock();
        Assert.Null(clock.Position);
        clock.Reanchor(Day.AddHours(10));
        clock.Update(-1);
        clock.Update(0);
        Assert.Equal(Day.AddHours(10), clock.Position);
        clock.Update(90_000);
        Assert.Equal(Day.AddHours(10).AddSeconds(90), clock.Position);
        clock.Reanchor(Day.AddHours(11));
        Assert.Equal(Day.AddHours(11), clock.Position);
        clock.Clear();
        Assert.Null(clock.Position);
    }

    // ----- PrefixedStream -----

    [Fact]
    public async Task Peek_returns_null_for_an_empty_body_and_replays_what_it_read()
    {
        Assert.Null(await PrefixedStream.PeekAsync(new MemoryStream(), 8, default));

        var source = new MemoryStream(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray());
        var peeked = await PrefixedStream.PeekAsync(source, 8, default);
        Assert.NotNull(peeked);
        var all = new MemoryStream();
        await peeked!.CopyToAsync(all);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray(), all.ToArray());
        Assert.False(peeked.CanSeek);
    }

    // ----- ContainerPipe -----

    [Fact]
    public void Pipe_arguments_are_a_stream_copy_from_stdin_to_stdout()
    {
        var args = ContainerPipe.BuildArguments("dhav", "mpegts").ToList();
        Assert.Contains("-c:v", args);
        Assert.Equal("copy", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("dhav", args[args.IndexOf("-f") + 1]);
        Assert.Equal("pipe:0", args[args.IndexOf("-i") + 1]);
        Assert.Equal("pipe:1", args[^1]);
        Assert.DoesNotContain("-c:a", args);
    }

    // ----- vendors -----

    [Fact]
    public void Dahua_recorded_days_bucket_segments_and_give_midnight_to_the_day_before()
    {
        var days = DahuaClient.RecordedDays(
        [
            Seg(0, 24 * 60),                        // 3rd, ending exactly at midnight
            Seg(2 * 24 * 60 + 60, 2 * 24 * 60 + 120), // 5th
            Seg(-30, 15),                           // 2nd → 3rd
        ], 2026, 9);
        Assert.Equal([2, 3, 5], days);
    }

    [Fact]
    public async Task Hikvision_playback_falls_through_to_POST_when_GET_answers_an_empty_body()
    {
        var video = Encoding.ASCII.GetBytes("\0\0\x01\xBA program stream bytes");
        var handler = new MockHttpHandler((req, body) =>
        {
            Assert.Equal("/ISAPI/ContentMgmt/download", req.RequestUri!.AbsolutePath);
            Assert.Contains("/Streaming/tracks/701?starttime=20260903T100000Z", body);
            if (req.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new ByteArrayContent([]) };
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(video) };
            resp.Content.Headers.ContentType = new("video/mp4");
            return resp;
        });
        var conn = new NvrConnection { Host = "h", Username = "u", Password = "p" };
        using var client = new HikvisionClient(conn, handler);

        using var playback = await client.OpenPlaybackAsync(7, Day.AddHours(10), Day.AddHours(11));

        Assert.Equal(PlaybackContainer.MpegPs, playback.Container);
        Assert.Equal(Day.AddHours(10), playback.RequestedStart);
        var all = new MemoryStream();
        await playback.Body.CopyToAsync(all);
        Assert.Equal(video, all.ToArray());
        Assert.Equal(2, handler.Requests.Count);
    }
}
