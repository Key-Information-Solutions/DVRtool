using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The read-only drift computation: expected per-panel card sets (policy + identity map) vs a
/// live roster. All synthetic; no panel is contacted.
/// </summary>
public class AccessReconcilerTests
{
    private static AccessPolicy Policy() => AccessPolicyFixtures.Policy();

    private static IdentityMap MappedAlphaAndBeta() => IdentityMap.Build(
        [
            new CardholderIdentity { Fob = "100", Name = "Alpha Uno", Source = "test" },
            new CardholderIdentity { Fob = "200", Name = "Beta Dos", Source = "test" },
            // Gamma Tres (NightGuard) is intentionally absent -> unmapped.
        ], "test");

    private static AccessCard Card(string no, bool valid, params int[] doors) =>
        new() { CardNo = no, Valid = valid, Doors = doors };

    private static AccessPanelResult Panel(string host, params AccessCard[] cards) =>
        new() { PanelHost = host, Cards = cards };

    private static readonly string[] BothPanels = ["10.0.0.1", "10.0.0.2"];

    [Fact]
    public void BuildExpectationUnionsGroupsPerPersonAndFlagsUnmapped()
    {
        var (byPanel, unmapped) = AccessReconciler.BuildExpectation(Policy(), MappedAlphaAndBeta());

        // Alpha (Everyone + Admins) -> north doors 1,2 and south door 1; Beta -> north door 1.
        var north = byPanel["10.0.0.1"];
        Assert.Equal([1, 2], north.Single(c => c.Fob == "100").Doors.OrderBy(d => d));
        Assert.Equal([1], north.Single(c => c.Fob == "200").Doors);

        var south = byPanel["10.0.0.2"];
        Assert.Equal([1], south.Single(c => c.Fob == "100").Doors);
        Assert.DoesNotContain(south, c => c.Fob == "200");

        // Gamma held a NightGuard door but has no fob in the map -> not checked, surfaced.
        Assert.Contains("Gamma Tres", unmapped);
    }

    [Fact]
    public void BuildExpectationWithNoMapLeavesEveryoneUnmapped()
    {
        var (byPanel, unmapped) = AccessReconciler.BuildExpectation(Policy(), map: null);
        Assert.Empty(byPanel);
        Assert.Equal(3, unmapped.Count);
    }

    [Fact]
    public void CompareReportsInSyncPanelsAndExtraFobs()
    {
        var roster = AccessRoster.Build(
        [
            Panel("10.0.0.1", Card("100", true, 1, 2), Card("200", true, 1), Card("999", true, 1)),
            Panel("10.0.0.2", Card("100", true, 1)),
        ]);

        var report = AccessReconciler.Compare(Policy(), MappedAlphaAndBeta(), roster, BothPanels);

        var north = report.Panels.Single(p => p.PanelIp == "10.0.0.1");
        Assert.Empty(north.Missing);
        Assert.Empty(north.DoorMismatches);
        Assert.Equal(["999"], north.ExtraFobs);   // a fob no policy group accounts for
        Assert.False(north.InSync);

        var south = report.Panels.Single(p => p.PanelIp == "10.0.0.2");
        Assert.True(south.InSync);

        Assert.Contains("Gamma Tres", report.UnmappedMembers);
        Assert.False(report.InSync);
    }

    [Fact]
    public void CompareDetectsDoorMismatches()
    {
        var roster = AccessRoster.Build(
        [
            Panel("10.0.0.1", Card("100", true, 1), Card("200", true, 1)),  // 100 should open 1 AND 2
            Panel("10.0.0.2", Card("100", true, 1)),
        ]);

        var north = AccessReconciler.Compare(Policy(), MappedAlphaAndBeta(), roster, BothPanels)
            .Panels.Single(p => p.PanelIp == "10.0.0.1");

        var mismatch = Assert.Single(north.DoorMismatches);
        Assert.Equal("100", mismatch.Fob);
        Assert.Equal([1, 2], mismatch.Expected);
        Assert.Equal([1], mismatch.Actual);
        Assert.Empty(north.Missing);
    }

    [Fact]
    public void CompareTreatsAMissingOrRevokedExpectedCardAsMissing()
    {
        var roster = AccessRoster.Build(
        [
            // 200 absent entirely; 100 present but REVOKED — both count as no access.
            Panel("10.0.0.1", Card("100", false, 1, 2)),
            Panel("10.0.0.2", Card("100", true, 1)),
        ]);

        var north = AccessReconciler.Compare(Policy(), MappedAlphaAndBeta(), roster, BothPanels)
            .Panels.Single(p => p.PanelIp == "10.0.0.1");

        Assert.Equal(["100", "200"], north.Missing.Select(m => m.Fob).OrderBy(f => f));
        // A revoked fob is not "extra" — it is simply absent access.
        Assert.Empty(north.ExtraFobs);
    }

    [Fact]
    public void ComparePreservesAnUnreadablePanelAsUnjudged()
    {
        var roster = AccessRoster.Build(
        [
            Panel("10.0.0.1", Card("100", true, 1, 2), Card("200", true, 1)),
            AccessPanelResult.Failed("10.0.0.2", "connect failed (7)"),
        ]);

        var report = AccessReconciler.Compare(Policy(), MappedAlphaAndBeta(), roster, BothPanels);

        var south = report.Panels.Single(p => p.PanelIp == "10.0.0.2");
        Assert.False(south.Read);
        Assert.False(south.InSync);
        // North matched exactly, but the fleet is not "in sync" while a panel is unjudged.
        Assert.True(report.Panels.Single(p => p.PanelIp == "10.0.0.1").InSync);
        Assert.False(report.InSync);
    }

    [Fact]
    public void CompareSurfacesNonContinuousScheduleWarnings()
    {
        var roster = AccessRoster.Build([Panel("10.0.0.1"), Panel("10.0.0.2")]);
        var report = AccessReconciler.Compare(Policy(), MappedAlphaAndBeta(), roster, BothPanels);
        Assert.Contains(report.ScheduleWarnings, w => w.Contains("NightGuard"));
    }
}
