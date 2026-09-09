namespace DVRTool.Core;

/// <summary>
/// One recorder's clock as the audit sees it: what it reads, how far out it is, or why it
/// could not be read at all.
/// </summary>
/// <remarks>
/// The carried-failure rule from <see cref="DeviceUsersResult"/> applies verbatim: a recorder
/// that could not be read is a row with an <see cref="Error"/>, never a row omitted and never
/// a row reading "fine".
/// </remarks>
public sealed record ClockAuditRow
{
    public required string DeviceName { get; init; }
    public DeviceClock? Clock { get; init; }
    public ClockDrift? Drift { get; init; }

    /// <summary>What keeps this clock honest, when the sweep could tell.</summary>
    public TimeSourceStatus TimeSource { get; init; } = TimeSourceStatus.Unknown;

    /// <summary>Null on success; why the recorder could not be read otherwise.</summary>
    public string? Error { get; init; }

    /// <summary>What the operator should do about this row, or empty.</summary>
    public required string Verdict { get; init; }

    public bool Ok => Error is null;

    /// <summary>This row names a clock an export cannot be trusted against.</summary>
    public bool IsFault => Ok && (Drift?.IsSignificant == true || TimeSource.NtpEnabled is false);

    /// <summary>The zone the recorder says it is in, verbatim, or empty.</summary>
    public string ZoneText => Clock?.VendorZoneLabel ?? "";

    /// <summary>A recorder that answered.</summary>
    public static ClockAuditRow For(string deviceName, DeviceClock clock, ClockDrift drift,
        TimeSourceStatus? timeSource = null) =>
        new()
        {
            DeviceName = deviceName,
            Clock = clock,
            Drift = drift,
            TimeSource = timeSource ?? TimeSourceStatus.Unknown,
            Verdict = ConfigAudit.VerdictFor(clock, drift, timeSource ?? TimeSourceStatus.Unknown),
        };

    /// <summary>A recorder that did not.</summary>
    public static ClockAuditRow Failed(string deviceName, string error) =>
        new()
        {
            DeviceName = deviceName,
            Error = error,
            Verdict = "could not be read — its clock is unknown, not fine",
        };
}

/// <summary>
/// The fleet clock audit: one row per recorder, and a verdict per row. Pure aggregation, no
/// I/O, like <see cref="FleetMatrix"/> and <see cref="AccessRoster"/>, and for the same
/// reason — identical from the GUI, the CLI, or a fixture.
/// </summary>
/// <remarks>
/// This is the part of the device-config feature that repays the build on its own. Footage
/// search, timeline geometry (<see cref="TimelineWindow"/>, <see cref="PlaybackClock"/>) and
/// export naming (<see cref="ExportNaming"/>) all run on NVR-local wall clock, so a recorder
/// an hour out stamps an hour of wrong time onto everything it writes — and the discovery pass
/// found exactly that on the first three recorders it read. Reading the clock is a correctness
/// check on the export product already shipped.
/// </remarks>
public sealed record ConfigAudit
{
    public required IReadOnlyList<ClockAuditRow> Rows { get; init; }

    public IEnumerable<ClockAuditRow> FailedDevices => Rows.Where(r => !r.Ok);

    /// <summary>Rows naming a clock an export cannot be trusted against.</summary>
    public IEnumerable<ClockAuditRow> Faults => Rows.Where(r => r.IsFault);

    /// <summary>True when at least one recorder could not be read, so the audit is partial.</summary>
    public bool IsPartial => Rows.Any(r => !r.Ok);

    /// <summary>One line an operator can act on, partial audits included.</summary>
    public string Summary
    {
        get
        {
            int read = Rows.Count(r => r.Ok);
            int faults = Faults.Count();
            int unread = Rows.Count - read;
            string head =
                read == 0 ? "no recorder answered."
                : faults == 0
                    ? $"{read} recorder(s) read, every clock within " +
                      $"{ClockDrift.Tolerance.TotalMinutes:0} minute."
                    : $"{read} recorder(s) read, {faults} with a clock problem.";
            return unread > 0
                ? head + $" {(read == 0 ? "" : "PARTIAL: ")}{unread} could not be read — " +
                    "those clocks are unknown, not fine."
                : head;
        }
    }

    public static ConfigAudit Build(IEnumerable<ClockAuditRow> rows) =>
        new() { Rows = rows.ToList() };

    /// <summary>
    /// What to say about one clock. The cases come from the discovery pass, and the order
    /// matters: the drift is the headline, the cause follows it, and a report that is not a
    /// fault is worded as a report.
    /// </summary>
    internal static string VerdictFor(DeviceClock clock, ClockDrift drift,
        TimeSourceStatus timeSource)
    {
        var parts = new List<string>(3);

        if (drift.IsSignificant)
        {
            parts.Add(drift.Text);

            // Where the vendor exposes a cause, the cause: NTP syncing with DST disabled in a
            // zone that observes it is the exact shape of the fault that had a 128-channel
            // recorder an hour out, and saying so is the difference between a verdict and a
            // number.
            if (LooksLikeDisabledDst(clock, drift, timeSource))
                parts.Add("NTP is syncing; DST is disabled");
        }
        else if (drift.DeclaredOffsetDiffers)
        {
            // A report, not a fault: the digits are right, and the digits are what the
            // recorder stamps onto footage.
            parts.Add("clock right, reports the wrong offset");
        }

        if (timeSource.NtpEnabled is false)
            parts.Add("no time source");

        if (parts.Count == 0)
            return drift.ExpectedOffsetMinutes is int shift
                ? $"in step (measured {shift:+#;-#;0} min from this workstation, as configured)"
                : "";

        return string.Join("; ", parts);
    }

    /// <summary>
    /// Whether this recorder's hour looks like a switched-off DST rule rather than a clock
    /// nobody is setting: an exact hour out, DST off, and something syncing the clock.
    /// </summary>
    private static bool LooksLikeDisabledDst(DeviceClock clock, ClockDrift drift,
        TimeSourceStatus timeSource)
    {
        if (clock.DstEnabled is not false || timeSource.NtpEnabled is not true)
            return false;

        // Within the measurement's own tolerance of exactly an hour, either way. A recorder
        // two hours out is a different problem and must not borrow this explanation.
        var offBy = drift.Drift.Duration();
        return (offBy - TimeSpan.FromHours(1)).Duration() <= ClockDrift.Tolerance;
    }
}

/// <summary>
/// Reads one recorder for the fleet clock audit. Lives here, next to the aggregation it feeds,
/// so the GUI's Fleet-clocks panel and <c>dvrtool config audit</c> sweep identically —
/// the same reason <see cref="ConnectivityProbe"/> does its I/O in Core.
/// </summary>
public static class ClockSweep
{
    /// <summary>
    /// The clock and the time source — two cheap reads — and, <b>only for a recorder that
    /// turns out to be out of step</b>, a third: the full configuration, so the verdict can
    /// name the cause. A healthy fleet is still swept in two small requests per device, and
    /// the device that is wrong is the one worth spending a round trip explaining.
    /// </summary>
    /// <param name="expectedOffsetMinutes">
    /// From the saved record: how far this recorder's wall clock is meant to sit from the
    /// workstation's. Null means "the same clock as me", which is right for a single-zone fleet.
    /// </param>
    public static async Task<ClockAuditRow> ReadAsync(string deviceName,
        IDeviceConfigClient config, int? expectedOffsetMinutes = null,
        CancellationToken ct = default)
    {
        var started = DateTimeOffset.Now;
        var clock = await config.GetClockAsync(ct);
        var finished = DateTimeOffset.Now;

        // A recorder that answers its clock and refuses the rest still gets a row: the clock
        // is the part that matters, and an unknown time source is not a missing one.
        var source = TimeSourceStatus.Unknown;
        try
        {
            source = await config.GetTimeSourceAsync(ct);
        }
        catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
        {
        }

        var drift = ClockDrift.Measure(clock, started, finished, expectedOffsetMinutes);
        if (!drift.IsSignificant)
            return ClockAuditRow.For(deviceName, clock, drift, source);

        // Out of step. The cause is in the parts the cheap reads skip — Dahua's DST switch and
        // its zone index, Hikvision's zone string — so they are worth one more read here and
        // nowhere else. The drift measurement is kept: re-measuring would only move the error
        // bar, and the reading that found the fault is the reading to report.
        try
        {
            var doc = await config.GetConfigurationAsync(ct);
            return ClockAuditRow.For(deviceName, doc.Clock with { WallClock = clock.WallClock },
                drift, doc.TimeSource.NtpEnabled is null && source.NtpEnabled is not null
                    ? source
                    : doc.TimeSource);
        }
        catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
        {
            return ClockAuditRow.For(deviceName, clock, drift, source);
        }
    }
}
