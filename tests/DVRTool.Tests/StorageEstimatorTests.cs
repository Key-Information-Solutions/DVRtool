using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

public class StorageEstimatorTests
{
    [Fact]
    public void EstimateRetentionDays_BasicMath()
    {
        // 1,000,000 MB = 8e12 bits; at 1000 kbps (1e6 bit/s) that is 8e6 s ≈ 92.59 days.
        double days = StorageEstimator.EstimateRetentionDays(1_000_000, 1_000)!.Value;
        Assert.Equal(92.59, days, 2);
    }

    [Fact]
    public void EstimateRetentionDays_MatchesTheLiveRecorderItWasValidatedAgainst()
    {
        // Site C, 2026-09-01: 4 × 7,630,885 MB and ~125 Mbps of configured caps held
        // ~24 days of footage; the worst-case estimate must land just under that.
        double days = StorageEstimator.EstimateRetentionDays(4 * 7_630_885L, 125_000)!.Value;
        Assert.InRange(days, 20.0, 24.0);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1000, 0)]
    [InlineData(-5, -5)]
    public void EstimateRetentionDays_UnknownSides_ReturnNull(long capacityMB, long kbps)
    {
        Assert.Null(StorageEstimator.EstimateRetentionDays(capacityMB, kbps));
    }

    [Fact]
    public void RequiredTotalKbps_InvertsTheEstimate()
    {
        long budget = StorageEstimator.RequiredTotalKbps(1_000_000, 92.5926);
        Assert.InRange(budget, 995, 1005);

        // The budget it hands back must actually fit the target.
        double achieved = StorageEstimator.EstimateRetentionDays(1_000_000, budget)!.Value;
        Assert.True(achieved >= 92.5, $"budget {budget} kbps only achieves {achieved} days");
    }

    [Fact]
    public void PlanUniform_SplitsEvenly_AndSnapsToStep()
    {
        var cams = Enumerable.Range(1, 10)
            .Select(ch => new PlanCamera(ch, $"cam{ch}", 5120, 32, 16384))
            .ToList();

        // 10 TB, 30 days → ~30,864 kbps total → 3,086 per camera → snapped to 3,072.
        var plan = StorageEstimator.PlanUniform(10_000_000, 30, cams);

        Assert.Equal(3072, plan.UniformKbps);
        Assert.Equal(0, plan.UniformKbps % StorageEstimator.KbpsStep);
        Assert.All(plan.Cameras, c => Assert.Equal(3072, c.PlannedKbps));
        Assert.All(plan.Cameras, c => Assert.False(c.Clamped));
        Assert.True(plan.MeetsTarget, $"snapping down must never miss the target " +
            $"(estimated {plan.EstimatedDays})");
        Assert.True(plan.EstimatedDays >= 30);
    }

    [Fact]
    public void PlanUniform_ClampsToEachCamerasRange_AndReestimatesHonestly()
    {
        var cams = new List<PlanCamera>
        {
            new(1, "wide", 8192, 32, 16384),
            new(2, "legacy", 2048, 1024, 2048), // can't go below 1024 or above 2048
        };

        // Ambitious target: 2 TB for 365 days → ~507 kbps total, ~253 per camera,
        // snapped to 224 — camera 2's 1024 minimum then forces the total to 1248,
        // more than double the budget, so the plan must say the target is missed.
        var plan = StorageEstimator.PlanUniform(2_000_000, 365, cams);

        Assert.Equal(224, plan.UniformKbps);
        Assert.Equal(224, plan.Cameras[0].PlannedKbps);
        Assert.Equal(1024, plan.Cameras[1].PlannedKbps);
        Assert.True(plan.Cameras[1].Clamped);
        Assert.False(plan.Cameras[0].Clamped);
        Assert.False(plan.MeetsTarget);
        Assert.True(plan.EstimatedDays < 365);
        Assert.Equal(224 + 1024, plan.PlannedTotalKbps);
    }

    [Fact]
    public void PlanUniform_ReportsWhichCamerasChange()
    {
        var cams = new List<PlanCamera>
        {
            new(1, "a", 4096, 32, 16384),
            new(2, "b", null, 32, 16384), // unknown current rate always counts as a change
        };
        var plan = StorageEstimator.PlanUniform(10_000_000, 30, cams);
        int uniform = plan.UniformKbps;

        Assert.Equal(uniform != 4096, plan.Cameras[0].Changes);
        Assert.True(plan.Cameras[1].Changes);
    }

    [Fact]
    public void PlanUniform_NoCameras_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            StorageEstimator.PlanUniform(1_000_000, 30, []));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1_000_000, 0)]
    public void RequiredTotalKbps_RejectsEmptyInputs(long capacityMB, double days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StorageEstimator.RequiredTotalKbps(capacityMB, days));
    }

    [Fact]
    public void PlanUniform_TinyBudget_NeverGoesBelowTheStepFloor()
    {
        var cams = Enumerable.Range(1, 100)
            .Select(ch => new PlanCamera(ch, "", 512, 32, 16384))
            .ToList();
        // 100 cameras against a laughable 100 GB: per-camera floor is one step (32).
        var plan = StorageEstimator.PlanUniform(100_000, 365, cams);

        Assert.All(plan.Cameras, c => Assert.True(c.PlannedKbps >= 32));
        Assert.False(plan.MeetsTarget);
    }

    [Fact]
    public void PlanUniform_FixedSecondaryStreams_AreSpentBeforeTheSplit_AndCountedInTheTotal()
    {
        // Four Nx cameras each archiving a 500 kbps secondary stream. 10 TB for 30 days is a
        // budget of ~30,864 kbps; 2,000 of it is spoken for, so the main streams share
        // ~28,864 → 7,216 each → snapped to 7,200, and the disks see 4 × 7,200 + 2,000.
        var cams = Enumerable.Range(1, 4)
            .Select(ch => new PlanCamera(ch, $"cam{ch}", 8000, 192, 65536, FixedKbps: 500))
            .ToList();
        var plan = StorageEstimator.PlanUniform(10_000_000, 30, cams);

        Assert.Equal(7200, plan.UniformKbps);
        Assert.All(plan.Cameras, c => Assert.Equal(500, c.FixedKbps));
        Assert.Equal(4 * 7200 + 2000, plan.PlannedTotalKbps);
        Assert.True(plan.MeetsTarget, $"estimated {plan.EstimatedDays}");

        // The same cameras without the fixed part would have been given more.
        var without = StorageEstimator.PlanUniform(10_000_000, 30,
            cams.Select(c => c with { FixedKbps = 0 }).ToList());
        Assert.True(without.UniformKbps > plan.UniformKbps);
        Assert.Equal(0, without.Cameras[0].FixedKbps);
    }

    [Fact]
    public void PlanUniform_FixedPartAloneOverBudget_FloorsTheMainsAndReportsTheMiss()
    {
        var cams = Enumerable.Range(1, 10)
            .Select(ch => new PlanCamera(ch, "", 512, 32, 16384, FixedKbps: 5000))
            .ToList();
        // 100 GB for a year: the secondary streams alone blow the budget.
        var plan = StorageEstimator.PlanUniform(100_000, 365, cams);

        Assert.All(plan.Cameras, c => Assert.Equal(32, c.PlannedKbps));
        Assert.Equal(10 * 32 + 50_000, plan.PlannedTotalKbps);
        Assert.False(plan.MeetsTarget);
    }

    // ----- pinned cameras -----

    [Fact]
    public void PlanUniform_PinnedCameras_AreSpentFirst_AndTheRestSplitWhatIsLeft()
    {
        // 10 TB for 30 days is a ~30,864 kbps budget. One camera is pinned at 12,000, so the
        // other nine share ~18,864 → 2,096 each → snapped to 2,080. Without the pin they
        // would each have had 3,072.
        var cams = Enumerable.Range(1, 10)
            .Select(ch => new PlanCamera(ch, $"cam{ch}", 5120, 32, 16384,
                PinnedKbps: ch == 1 ? 12_000 : null))
            .ToList();

        var plan = StorageEstimator.PlanUniform(10_000_000, 30, cams);

        Assert.Equal(2080, plan.UniformKbps);
        Assert.Equal(12_000, plan.Cameras[0].PlannedKbps);
        Assert.True(plan.Cameras[0].Pinned);
        Assert.All(plan.Cameras.Skip(1), c => Assert.Equal(2080, c.PlannedKbps));
        Assert.All(plan.Cameras.Skip(1), c => Assert.False(c.Pinned));
        Assert.Equal(1, plan.PinnedCount);
        Assert.Equal(9, plan.FreeCount);
        Assert.False(plan.AllPinned);
        Assert.Equal(12_000, plan.PinnedTotalKbps);
        Assert.Equal(12_000 + 9 * 2080, plan.PlannedTotalKbps);
        Assert.True(plan.MeetsTarget, $"estimated {plan.EstimatedDays}");
        Assert.Null(plan.MissReason);
    }

    [Fact]
    public void PlanUniform_PinnedAtItsCurrentRate_IsNotAWrite_ButPinningAnotherRateIs()
    {
        var cams = new List<PlanCamera>
        {
            new(1, "held", 4096, 32, 16384, PinnedKbps: 4096),  // "keep what it reads"
            new(2, "moved", 4096, 32, 16384, PinnedKbps: 8192),  // an instruction
            new(3, "free", 4096, 32, 16384),
        };

        var plan = StorageEstimator.PlanUniform(10_000_000, 30, cams);

        Assert.False(plan.Cameras[0].Changes);
        Assert.True(plan.Cameras[1].Changes);
        Assert.True(plan.Cameras[1].Pinned);
    }

    [Fact]
    public void PlanUniform_PinAboveWhatTheCameraAccepts_IsClampedAndFlagged()
    {
        var cams = new List<PlanCamera>
        {
            new(1, "legacy", 2048, 512, 2048, PinnedKbps: 9000),
            new(2, "free", 4096, 32, 16384),
        };

        var plan = StorageEstimator.PlanUniform(10_000_000, 30, cams);

        Assert.Equal(2048, plan.Cameras[0].PlannedKbps);
        Assert.True(plan.Cameras[0].Clamped);
        Assert.True(plan.Cameras[0].Pinned);
        // Only what the camera will really record is spent from the budget.
        Assert.Equal(2048, plan.PinnedTotalKbps);
    }

    [Fact]
    public void PlanUniform_EveryCameraPinned_HasNoUniformRate_AndSaysSoWhenItMisses()
    {
        var cams = Enumerable.Range(1, 4)
            .Select(ch => new PlanCamera(ch, $"cam{ch}", 8192, 32, 16384, PinnedKbps: 8192))
            .ToList();

        // 1 TB for 60 days is a ~1,543 kbps budget; the pins ask for 32,768.
        var plan = StorageEstimator.PlanUniform(1_000_000, 60, cams);

        Assert.Equal(0, plan.UniformKbps);      // nothing was left to the planner
        Assert.True(plan.AllPinned);
        Assert.Equal(0, plan.FreeCount);
        Assert.All(plan.Cameras, c => Assert.Equal(8192, c.PlannedKbps));
        Assert.False(plan.MeetsTarget);
        Assert.Contains("every camera is pinned", plan.MissReason);
        Assert.Contains("Unpin a camera", plan.MissReason);
    }

    [Fact]
    public void PlanUniform_PinsAloneOverBudget_BlamesThePins_NotCameraMinimums()
    {
        // Two cameras pinned at 8 Mbps against a budget of ~1,543 kbps: the eight free
        // cameras end up on the floor and it still does not fit. An operator reading
        // "camera minimums keep the total up" here would go price disks for a decision they
        // made themselves, which is why the pins are named first.
        var cams = Enumerable.Range(1, 10)
            .Select(ch => new PlanCamera(ch, $"cam{ch}", 4096, 32, 16384,
                PinnedKbps: ch <= 2 ? 8192 : null))
            .ToList();

        var plan = StorageEstimator.PlanUniform(1_000_000, 60, cams);

        Assert.All(plan.Cameras.Skip(2), c => Assert.Equal(32, c.PlannedKbps));
        Assert.False(plan.MeetsTarget);
        Assert.Contains("2 pinned camera(s) alone want 16,384 kbps", plan.MissReason);
        Assert.Contains("floored at 32 kbps", plan.MissReason);
        Assert.DoesNotContain("camera minimums", plan.MissReason);
    }

    [Fact]
    public void PlanUniform_MissWithNoPins_StillBlamesCameraMinimums()
    {
        var cams = new List<PlanCamera>
        {
            new(1, "wide", 8192, 32, 16384),
            new(2, "legacy", 2048, 1024, 2048),
        };
        var plan = StorageEstimator.PlanUniform(2_000_000, 365, cams);

        Assert.Equal(0, plan.PinnedCount);
        Assert.Contains("camera minimums", plan.MissReason);
        Assert.Contains("More disk, fewer cameras", plan.MissReason);
    }

    [Fact]
    public void PlanUniform_PinsAndSecondaryStreams_BothComeOffTheBudgetBeforeTheSplit()
    {
        // An Nx-shaped system: every camera archives a 500 kbps secondary stream, and one
        // camera is pinned at 6,000. 10 TB/30 days ≈ 30,864 → less 2,000 fixed, less 6,000
        // pinned → 22,864 over three cameras → 7,621 → snapped to 7,616.
        var cams = Enumerable.Range(1, 4)
            .Select(ch => new PlanCamera(ch, $"cam{ch}", 8000, 192, 65536, FixedKbps: 500,
                PinnedKbps: ch == 1 ? 6000 : null))
            .ToList();

        var plan = StorageEstimator.PlanUniform(10_000_000, 30, cams);

        Assert.Equal(7616, plan.UniformKbps);
        Assert.Equal(2000, plan.FixedTotalKbps);
        Assert.Equal(6000, plan.PinnedTotalKbps);
        Assert.Equal(6000 + 3 * 7616 + 2000, plan.PlannedTotalKbps);
        // A pinned camera keeps its own fixed part in the total, like every other camera.
        Assert.Equal(500, plan.Cameras[0].FixedKbps);
        Assert.True(plan.MeetsTarget, $"estimated {plan.EstimatedDays}");
    }

    [Fact]
    public void PlanUniform_BudgetIsReported_SoAMissCanBeExplainedInKbps()
    {
        var cams = new List<PlanCamera> { new(1, "a", 4096, 32, 16384) };
        var plan = StorageEstimator.PlanUniform(10_000_000, 30, cams);

        Assert.Equal(StorageEstimator.RequiredTotalKbps(10_000_000, 30), plan.BudgetKbps);
        Assert.Equal(0, plan.PinnedTotalKbps);
        Assert.Equal(0, plan.FixedTotalKbps);
    }

    [Fact]
    public void CameraStream_RecordedBitrate_AddsTheArchivedSecondaryStream()
    {
        var hik = new CameraStream(1, 101, true, "H.265", 2688, 1520, 20, "VBR", 4096, null, null);
        Assert.Equal(4096, hik.RecordedBitrateKbps); // no second stream: same as the cap

        var nx = hik with { QualityControlType = "BEST", SecondaryRecordedKbps = 512 };
        Assert.Equal(4096, nx.MaxBitrateKbps);      // the cap the planner may write
        Assert.Equal(4608, nx.RecordedBitrateKbps); // what the disks actually see

        var unknownMain = nx with { VbrUpperCapKbps = null };
        Assert.Null(unknownMain.MaxBitrateKbps);
        Assert.Equal(512, unknownMain.RecordedBitrateKbps);
        Assert.Null((hik with { VbrUpperCapKbps = null }).RecordedBitrateKbps);
    }

    [Fact]
    public void StorageInfo_VolumesThatDoNotRecord_AreListedButNotCounted()
    {
        var info = new StorageInfo(
        [
            new HddInfo(1, @"D:\", "local", "ok", "main", 60_000_000, 0, "", ""),
            new HddInfo(2, @"E:\", "local", "ok", "backup", 8_000_000, 0, "", "", RecordsFootage: false),
            new HddInfo(3, "bay3", "SATA", "notexist", "", 0, 0, "", "WD"),
        ], null, null);

        Assert.Equal(2, info.InstalledCount);
        Assert.Equal(1, info.GhostBayCount);
        Assert.Equal(60_000_000, info.TotalCapacityMB);
        Assert.Equal(@"E:\", Assert.Single(info.NonRecordingHdds).Name);
    }

    [Fact]
    public void CameraStream_ArchiveCap_IsCarriedAndNullByDefault()
    {
        var plain = new CameraStream(1, 1, true, "H.264", 1920, 1080, 15, "HIGH", 2763, null, 3);
        Assert.Null(plain.ArchiveCapDays);
        Assert.Equal(31, (plain with { ArchiveCapDays = 31 }).ArchiveCapDays);
    }
}
