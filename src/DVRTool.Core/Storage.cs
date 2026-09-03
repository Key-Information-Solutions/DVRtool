namespace DVRTool.Core;

/// <summary>
/// One disk bay entry as the recorder reports it. Hikvision keeps a row for every bay it
/// has ever seen a disk in: a row whose <see cref="Status"/> is <c>notexist</c> is the
/// ghost of a removed drive (its last-known model and serial are still shown), not an
/// installed one — so capacity math must skip it, but it is worth displaying because it
/// marks a bay that is wired and known-good.
/// </summary>
/// <param name="Id">Bay/slot number as the recorder numbers it (1-based, holes allowed).</param>
/// <param name="CapacityMB">Decimal megabytes (10^6 bytes), as ISAPI reports it. 0 for ghosts.</param>
/// <param name="FreeSpaceMB">
/// Decimal megabytes. A healthy recorder in overwrite mode reports 0 here permanently —
/// free space says nothing about retention, which is why estimates use capacity instead.
/// </param>
/// <param name="RecordsFootage">
/// False for a volume that is present but not part of the recording pool — an Nx storage
/// that is not "used for writing", or a backup storage that only mirrors footage — so it is
/// listed but never counted toward retention. Recorder disk bays are always true.
/// </param>
public sealed record HddInfo(
    int Id,
    string Name,
    string HddType,
    string Status,
    string Property,
    long CapacityMB,
    long FreeSpaceMB,
    string SerialNumber,
    string Model,
    bool RecordsFootage = true)
{
    public bool IsInstalled =>
        !string.Equals(Status, "notexist", StringComparison.OrdinalIgnoreCase);

    public bool IsHealthy => string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The recorder's disk inventory plus what its firmware says it could hold.</summary>
/// <param name="WorkMode">Recording allocation mode (e.g. "quota", "group"), when reported.</param>
/// <param name="MaxSupportedHdds">
/// The firmware's supported HDD count from the storage capabilities endpoint. This is the
/// firmware family's ceiling, not the chassis's physical bay count — a DS-9632NI-M8 (8 bays)
/// reports 16 — so present it as "supports up to", never as "free slots".
/// </param>
public sealed record StorageInfo(
    IReadOnlyList<HddInfo> Hdds,
    string? WorkMode,
    int? MaxSupportedHdds)
{
    public int InstalledCount => Hdds.Count(h => h.IsInstalled);

    /// <summary>Bays holding the ghost of a removed disk — wired, currently empty.</summary>
    public int GhostBayCount => Hdds.Count(h => !h.IsInstalled);

    /// <summary>Installed volumes that hold no footage (backup, or not used for writing) — shown, never counted.</summary>
    public IReadOnlyList<HddInfo> NonRecordingHdds =>
        Hdds.Where(h => h.IsInstalled && !h.RecordsFootage).ToList();

    /// <summary>The recording pool: installed volumes that footage actually lands on.</summary>
    public long TotalCapacityMB =>
        Hdds.Where(h => h.IsInstalled && h.RecordsFootage).Sum(h => h.CapacityMB);

    public long TotalFreeSpaceMB =>
        Hdds.Where(h => h.IsInstalled && h.RecordsFootage).Sum(h => h.FreeSpaceMB);

    /// <summary>Installed disks in a state other than "ok" (error, formatting, idle…).</summary>
    public IReadOnlyList<HddInfo> UnhealthyHdds =>
        Hdds.Where(h => h.IsInstalled && !h.IsHealthy).ToList();
}

/// <summary>
/// The recording (main) stream settings of one camera, as configured on the recorder.
/// </summary>
/// <param name="FrameRateFps">Decoded from ISAPI's fps×100 encoding (2000 → 20.0).</param>
/// <param name="FrameRateIsFull">
/// The channel is configured for "Full Frame Rate" (ISAPI sends <c>maxFrameRate</c> 0) and
/// <paramref name="FrameRateFps"/> is the camera's native maximum resolved from its
/// capabilities, not a rate the operator picked.
/// </param>
/// <param name="VbrUpperCapKbps">The VBR ceiling — the max-bitrate figure retention math uses.</param>
/// <param name="ConstantBitrateKbps">Set when the channel is CBR (element absent on pure-VBR firmware).</param>
/// <param name="SecondaryRecordedKbps">
/// The bitrate of a second stream the recorder archives <em>alongside</em> the main one, when
/// it does. Nx Witness / DW Spectrum records both the primary and the secondary (low-quality)
/// stream of every camera unless told not to, so its disks fill at the sum; Hikvision and
/// Dahua record the main stream only and leave this null. It is not controlled by the
/// main-stream bitrate write, which is why it is carried separately from the cap.
/// </param>
/// <param name="ArchiveCapDays">
/// A per-camera age limit the recorder enforces regardless of disk space (Nx's "Max archive
/// days"), when one is set. Days held on such a camera can never exceed it, however generous
/// the capacity estimate — so the report says so next to the number.
/// </param>
/// <param name="Schedule">
/// When the recorder records this camera — the weekly schedule and its modes (continuous,
/// motion, alarm, Nx's "motion + low-res always", …). Null when the recorder did not answer
/// its schedule endpoint, which the report shows as "?" rather than as "records nothing".
/// </param>
public sealed record CameraStream(
    int Channel,
    int TrackId,
    bool Enabled,
    string CodecType,
    int Width,
    int Height,
    double? FrameRateFps,
    string QualityControlType,
    int? VbrUpperCapKbps,
    int? ConstantBitrateKbps,
    int? FixedQuality,
    bool FrameRateIsFull = false,
    int? SecondaryRecordedKbps = null,
    int? ArchiveCapDays = null,
    RecordingSchedule? Schedule = null)
{
    public bool IsVbr => string.Equals(QualityControlType, "VBR", StringComparison.OrdinalIgnoreCase);

    /// <summary>"20.0", or "30.0 (full)" when the camera runs at its native maximum.</summary>
    public string FrameRateText => FrameRateFps is double fps
        ? FrameRateIsFull ? $"{fps:F1} (full)" : fps.ToString("F1")
        : "";

    /// <summary>
    /// The worst-case recording bitrate this channel is allowed to produce — the number
    /// retention estimates and SLA math must use. VBR channels are bounded by their upper
    /// cap; CBR channels sit at their constant rate.
    /// </summary>
    public int? MaxBitrateKbps => IsVbr
        ? VbrUpperCapKbps
        : ConstantBitrateKbps ?? VbrUpperCapKbps;

    /// <summary>
    /// Everything this camera writes to disk per second, worst case: the main-stream cap plus
    /// any second stream the recorder archives with it. This — not <see cref="MaxBitrateKbps"/>
    /// alone — is what retention totals must sum; on a Hikvision or Dahua recorder the two are
    /// the same number.
    /// </summary>
    public int? RecordedBitrateKbps => (MaxBitrateKbps, SecondaryRecordedKbps) switch
    {
        (null, null) => null,
        (var main, var secondary) => (main ?? 0) + (secondary ?? 0),
    };

    public string Resolution => Width > 0 || Height > 0 ? $"{Width}x{Height}" : "";

    /// <summary>The "Recording" column: the schedule's summary, or "?" when the recorder did not say.</summary>
    public string RecordingText => Schedule?.Summary ?? "?";
}

/// <summary>Writable bitrate bounds for one channel, from its capabilities document.</summary>
public sealed record BitrateRange(int MinKbps, int MaxKbps);

/// <summary>
/// Opt-in capability: storage inventory, retention reads and bitrate control. Implemented
/// separately from <see cref="INvrClient"/> because not every vendor module exposes it
/// (Hikvision ISAPI, Dahua CGI and the Nx Witness / DW Spectrum REST API all do; the vendor
/// modules opt in by implementing it).
/// </summary>
public interface IStorageClient
{
    /// <summary>Disk inventory. Throws when the device does not answer the storage endpoint.</summary>
    Task<StorageInfo> GetStorageInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// The recording (main) stream configuration of every camera the recorder knows,
    /// ordered by channel. Sub/third streams are not returned: recordings are the main
    /// stream, and retention math over sub-stream bitrates would flatter every estimate.
    /// </summary>
    Task<IReadOnlyList<CameraStream>> GetMainStreamsAsync(CancellationToken ct = default);

    /// <summary>
    /// The start of the earliest recording still on disk for one channel, in the device's
    /// local wall-clock time — null when the channel has no recordings at all.
    /// </summary>
    Task<DateTime?> FindOldestRecordingAsync(int channel, CancellationToken ct = default);

    /// <summary>Writable bitrate bounds for one channel, or null when the device won't say.</summary>
    Task<BitrateRange?> GetBitrateRangeAsync(int channel, CancellationToken ct = default);

    /// <summary>
    /// Sets one channel's max recording bitrate (the VBR upper cap, and the constant rate
    /// where the channel is CBR), then reads it back and returns what the device now
    /// reports — which may differ from <paramref name="kbps"/> on cameras that clamp to
    /// their own steps. Throws when the device rejects the write outright.
    /// </summary>
    Task<int> SetMaxBitrateAsync(int channel, int kbps, CancellationToken ct = default);
}

/// <summary>One camera as the bitrate planner sees it.</summary>
/// <param name="FixedKbps">
/// Bitrate this camera writes regardless of the plan — a second stream the recorder archives
/// alongside the main one (<see cref="CameraStream.SecondaryRecordedKbps"/>). It is spent from
/// the budget before the main-stream rates are split, and never planned or written.
/// </param>
public sealed record PlanCamera(
    int Channel, string Name, int? CurrentKbps, int MinKbps, int MaxKbps, int FixedKbps = 0);

/// <summary>One camera's planned setting: what it records at now, and what the plan wants.</summary>
/// <param name="FixedKbps">Carried from <see cref="PlanCamera.FixedKbps"/>; part of the total, not of the write.</param>
public sealed record PlannedCamera(
    int Channel, string Name, int? CurrentKbps, int PlannedKbps, bool Clamped, int FixedKbps = 0)
{
    public bool Changes => CurrentKbps != PlannedKbps;
}

/// <summary>
/// A "we need X days" bitrate plan: the uniform per-camera rate that fits the target into
/// the disks, camera by camera after clamping to what each will accept.
/// </summary>
/// <param name="PlannedTotalKbps">
/// What the disks will see: every planned main-stream rate plus every camera's fixed
/// (secondary-stream) bitrate.
/// </param>
public sealed record BitratePlan(
    double TargetDays,
    int UniformKbps,
    IReadOnlyList<PlannedCamera> Cameras,
    long PlannedTotalKbps,
    double? EstimatedDays)
{
    /// <summary>False when per-camera minimums forced the total above the budget.</summary>
    public bool MeetsTarget => EstimatedDays is double d && d >= TargetDays * 0.999;
}

/// <summary>
/// Retention math over raw disk capacity and worst-case (max) bitrates. Pure — no I/O.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is deliberately worst-case: recorders in overwrite mode report zero
/// free space forever, so the only honest estimate is "if every camera produced its
/// configured maximum around the clock, the disks hold N days". VBR with smart codecs
/// usually does better than its cap, so real retention lands at or above the estimate —
/// the right direction for SLA and insurance commitments. Validated against a live
/// recorder: 30.5 TB across 4 disks at ~125 Mbps of configured caps estimated ~22.6 days;
/// the oldest footage actually on it was ~24 days old.
/// </para>
/// <para>
/// Units: ISAPI reports disk capacity in decimal megabytes (10^6 bytes) and bitrates in
/// kilobits (1000 bits) per second.
/// </para>
/// </remarks>
public static class StorageEstimator
{
    /// <summary>Planned bitrates snap down to this step so cameras get tidy values.</summary>
    public const int KbpsStep = 32;

    /// <summary>
    /// Days of footage the capacity holds at the given total bitrate, or null when either
    /// side is unknown or zero.
    /// </summary>
    public static double? EstimateRetentionDays(long capacityMB, long totalBitrateKbps)
    {
        if (capacityMB <= 0 || totalBitrateKbps <= 0)
            return null;
        double totalBits = capacityMB * 8_000_000.0;
        double seconds = totalBits / (totalBitrateKbps * 1000.0);
        return seconds / 86_400.0;
    }

    /// <summary>The total bitrate budget (all cameras summed) that fits targetDays into the disks.</summary>
    public static long RequiredTotalKbps(long capacityMB, double targetDays)
    {
        if (capacityMB <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityMB), "no disk capacity to plan against");
        if (targetDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetDays), "target days must be positive");
        double totalBits = capacityMB * 8_000_000.0;
        return (long)(totalBits / (targetDays * 86_400.0) / 1000.0);
    }

    /// <summary>
    /// "We need X days": take the fixed (secondary-stream) bitrates off the budget first,
    /// split what is left evenly across the cameras, snap down to <see cref="KbpsStep"/>,
    /// clamp each camera to its own writable range, and re-estimate from the clamped result
    /// plus the fixed part — so the reported days are what the plan actually achieves, not
    /// what the arithmetic wished for.
    /// </summary>
    public static BitratePlan PlanUniform(
        long capacityMB, double targetDays, IReadOnlyList<PlanCamera> cameras)
    {
        if (cameras.Count == 0)
            throw new ArgumentException("no cameras to plan for", nameof(cameras));

        long budget = RequiredTotalKbps(capacityMB, targetDays);
        // Secondary streams record whatever the plan says, so they are spent before the
        // split; a budget they alone exceed leaves the minimum step for the main streams and
        // the plan reports the miss.
        long fixedTotal = cameras.Sum(c => (long)Math.Max(0, c.FixedKbps));
        int uniform = (int)Math.Clamp((budget - fixedTotal) / cameras.Count, KbpsStep, int.MaxValue);
        uniform -= uniform % KbpsStep;

        var planned = new List<PlannedCamera>(cameras.Count);
        foreach (var cam in cameras)
        {
            int clamped = Math.Clamp(uniform, cam.MinKbps, cam.MaxKbps);
            planned.Add(new PlannedCamera(
                cam.Channel, cam.Name, cam.CurrentKbps, clamped, Clamped: clamped != uniform,
                FixedKbps: Math.Max(0, cam.FixedKbps)));
        }

        long plannedTotal = planned.Sum(p => (long)p.PlannedKbps + p.FixedKbps);
        return new BitratePlan(targetDays, uniform, planned, plannedTotal,
            EstimateRetentionDays(capacityMB, plannedTotal));
    }
}
