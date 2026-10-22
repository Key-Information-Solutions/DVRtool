namespace DVRTool.Core;

/// <summary>
/// Opt-in capability: restart the whole recorder. A sibling of
/// <see cref="IDeviceConfigWriter"/>, not a member of it, because it is a different kind of
/// act: no document is read, modified or read back, and nothing it does can be compared
/// afterwards — the only evidence is the recorder going away and coming back
/// (<see cref="RebootWatch"/>).
/// </summary>
/// <remarks>
/// <para>
/// Hikvision only (<c>PUT /ISAPI/System/reboot</c>, empty body). On an NVR with built-in PoE
/// this is also the only power cycle of the cameras the API offers: the PoE document
/// (<c>/ISAPI/System/Network/POE</c>) describes the ports and has no switch to cut one, and the
/// relayed camera reboot needs the NVR to reach the camera — which is exactly what an offline
/// (<c>netUnreachable</c>) PoE camera fails.
/// </para>
/// <para>
/// Every camera on the recorder stops recording until it is back (two to three minutes on a
/// K-series NVR), so both front ends ask first and default to No.
/// </para>
/// </remarks>
public interface IDeviceRebooter
{
    /// <summary>
    /// Asks the recorder to restart. Returns once it has acknowledged; throws when it refused.
    /// A connection that drops before the answer arrives is a failure here, although the
    /// recorder may already be restarting — <see cref="RebootWatch"/> is what tells.
    /// </summary>
    Task RebootAsync(CancellationToken ct = default);
}

/// <summary>How a watched reboot ended.</summary>
public enum RebootWatchOutcome
{
    /// <summary>The recorder stopped answering, then answered again.</summary>
    CameBack,

    /// <summary>It kept answering for the whole down window: the restart probably never ran.</summary>
    NeverWentDown,

    /// <summary>It went down and has not answered since, inside the up window.</summary>
    DidNotReturn,
}

/// <summary>The result of <see cref="RebootWatch.WaitAsync"/>.</summary>
public sealed record RebootWatchResult(RebootWatchOutcome Outcome, TimeSpan Elapsed)
{
    public string Text => Outcome switch
    {
        RebootWatchOutcome.CameBack =>
            $"back after {RebootWatch.Format(Elapsed)}",
        RebootWatchOutcome.NeverWentDown =>
            $"still answering after {RebootWatch.Format(Elapsed)} — the restart may not have " +
            "happened",
        _ =>
            $"not answering {RebootWatch.Format(Elapsed)} after the restart — check the site",
    };
}

/// <summary>
/// Watches a restart from the outside: first for the recorder to stop answering, then for it
/// to answer again. Both phases are needed — a recorder acknowledges the reboot and keeps
/// serving for several seconds before it goes, so "it answered" straight after the request
/// proves nothing.
/// </summary>
public static class RebootWatch
{
    public static readonly TimeSpan DefaultDownWindow = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultUpWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultDownPoll = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DefaultUpPoll = TimeSpan.FromSeconds(10);

    /// <param name="answers">One probe: true when the recorder answered. Must not throw for
    /// an unreachable device — that is a false.</param>
    /// <param name="clock">The elapsed time since the request was acknowledged.</param>
    /// <param name="delay">Waits between probes (injected so tests run instantly).</param>
    /// <param name="progress">Told "down" when the recorder stops answering.</param>
    public static async Task<RebootWatchResult> WaitAsync(
        Func<CancellationToken, Task<bool>> answers,
        Func<TimeSpan> clock,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct = default,
        IProgress<string>? progress = null,
        TimeSpan? downWindow = null, TimeSpan? upWindow = null,
        TimeSpan? downPoll = null, TimeSpan? upPoll = null)
    {
        var downBy = downWindow ?? DefaultDownWindow;
        var upBy = upWindow ?? DefaultUpWindow;

        while (await answers(ct))
        {
            if (clock() >= downBy)
                return new RebootWatchResult(RebootWatchOutcome.NeverWentDown, clock());
            await delay(downPoll ?? DefaultDownPoll, ct);
        }

        progress?.Report("down");
        var wentDown = clock();
        while (true)
        {
            if (clock() - wentDown >= upBy)
                return new RebootWatchResult(RebootWatchOutcome.DidNotReturn, clock());
            await delay(upPoll ?? DefaultUpPoll, ct);
            if (await answers(ct))
                return new RebootWatchResult(RebootWatchOutcome.CameBack, clock());
        }
    }

    internal static string Format(TimeSpan t) => t.TotalMinutes >= 1
        ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s"
        : $"{(int)t.TotalSeconds} s";
}
