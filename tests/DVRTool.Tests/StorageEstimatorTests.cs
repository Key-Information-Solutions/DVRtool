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
}
