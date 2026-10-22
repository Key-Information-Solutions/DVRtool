using System.Net;
using DVRTool.Core;
using DVRTool.Vendors.Hikvision;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The restart request and the watch that is its only evidence. The cases worth having: a
/// refusal under HTTP 200, and a recorder that keeps answering straight after acknowledging —
/// which must not be read as "it came back".
/// </summary>
public class RebootTests
{
    private static readonly NvrConnection Conn = new()
    {
        Host = "192.0.2.10",
        Username = "admin",
        Password = "p@ss",
    };

    [Fact]
    public async Task Reboot_is_an_empty_PUT_to_System_reboot()
    {
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Xml("""
            <ResponseStatus><statusCode>1</statusCode><statusString>OK</statusString></ResponseStatus>
            """));
        using var client = new HikvisionClient(Conn, handler);

        await client.RebootAsync();

        var (req, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal("/ISAPI/System/reboot", req.RequestUri!.AbsolutePath);
        Assert.Equal("", body);
    }

    [Fact]
    public async Task A_refusal_under_HTTP_200_is_a_failure_in_the_devices_words()
    {
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Xml("""
            <ResponseStatus><statusCode>4</statusCode><statusString>Invalid Operation</statusString>
            <subStatusCode>lowPrivilege</subStatusCode></ResponseStatus>
            """));
        using var client = new HikvisionClient(Conn, handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() => client.RebootAsync());
        Assert.Contains("lowPrivilege", ex.Message);
    }

    [Fact]
    public async Task An_HTTP_error_is_a_failure()
    {
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Xml("<ResponseStatus/>", HttpStatusCode.Forbidden));
        using var client = new HikvisionClient(Conn, handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() => client.RebootAsync());
        Assert.Equal(403, ex.StatusCode);
    }

    /// <summary>A fake clock that only moves when the watch waits.</summary>
    private sealed class FakeTime
    {
        public TimeSpan Now { get; private set; }
        public TimeSpan Elapsed() => Now;
        public Task Delay(TimeSpan by, CancellationToken _)
        {
            Now += by;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Answering_right_after_the_request_is_not_coming_back()
    {
        var time = new FakeTime();
        // Answers for 10 s, silent until 70 s, then answers again.
        Task<bool> Answers(CancellationToken _) => Task.FromResult(
            time.Now < TimeSpan.FromSeconds(10) || time.Now >= TimeSpan.FromSeconds(70));
        var reported = new List<string>();

        var result = await RebootWatch.WaitAsync(Answers, time.Elapsed, time.Delay,
            progress: new SyncProgress(reported.Add));

        Assert.Equal(RebootWatchOutcome.CameBack, result.Outcome);
        Assert.True(result.Elapsed >= TimeSpan.FromSeconds(70));
        Assert.Equal(["down"], reported);
        Assert.StartsWith("back after 1 min", result.Text);
    }

    [Fact]
    public async Task A_recorder_that_never_stops_answering_never_went_down()
    {
        var time = new FakeTime();

        var result = await RebootWatch.WaitAsync(_ => Task.FromResult(true), time.Elapsed,
            time.Delay);

        Assert.Equal(RebootWatchOutcome.NeverWentDown, result.Outcome);
        Assert.True(result.Elapsed >= RebootWatch.DefaultDownWindow);
    }

    [Fact]
    public async Task A_recorder_that_goes_down_and_stays_down_did_not_return()
    {
        var time = new FakeTime();

        var result = await RebootWatch.WaitAsync(_ => Task.FromResult(false), time.Elapsed,
            time.Delay);

        Assert.Equal(RebootWatchOutcome.DidNotReturn, result.Outcome);
        Assert.True(result.Elapsed >= RebootWatch.DefaultUpWindow);
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
