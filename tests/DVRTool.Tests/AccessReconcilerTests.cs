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
    public void BuildExpectationGivesAUniquePersonEveryFobTheyHold()
    {
        // Beta legitimately carries two cards; both must be expected, and Beta stays mapped.
        var map = IdentityMap.Build(
        [
            new CardholderIdentity { Fob = "100", Name = "Alpha Uno", Source = "test" },
            new CardholderIdentity { Fob = "200", Name = "Beta Dos", Source = "test" },
            new CardholderIdentity { Fob = "201", Name = "Beta Dos", Source = "test" },
        ], "test");

        var (byPanel, unmapped) = AccessReconciler.BuildExpectation(Policy(), map);

        var betaFobs = byPanel["10.0.0.1"].Where(c => c.Name == "Beta Dos").ToList();
        Assert.Equal(["200", "201"], betaFobs.Select(c => c.Fob).OrderBy(f => f));
        Assert.All(betaFobs, c => Assert.Equal([1], c.Doors));
        Assert.DoesNotContain(unmapped, u => u.StartsWith("Beta"));
    }

    [Fact]
    public void CompareAcceptsBothFobsOfATwoCardHolderAsInSync()
    {
        var map = IdentityMap.Build(
        [
            new CardholderIdentity { Fob = "100", Name = "Alpha Uno", Source = "test" },
            new CardholderIdentity { Fob = "200", Name = "Beta Dos", Source = "test" },
            new CardholderIdentity { Fob = "201", Name = "Beta Dos", Source = "test" },
        ], "test");
        var roster = AccessRoster.Build(
        [
            Panel("10.0.0.1", Card("100", true, 1, 2), Card("200", true, 1), Card("201", true, 1)),
            Panel("10.0.0.2", Card("100", true, 1)),
        ]);

        var north = AccessReconciler.Compare(Policy(), map, roster, BothPanels)
            .Panels.Single(p => p.PanelIp == "10.0.0.1");

        Assert.Empty(north.ExtraFobs);   // the second card is expected now, not an unexplained extra
        Assert.Empty(north.Missing);
        Assert.True(north.InSync);
    }

    [Fact]
    public void BuildExpectationSetMatchesHomonymsWithIdenticalDoors()
    {
        // Two DISTINCT people (different GUIDs) share a name and the SAME group: their two fobs are
        // interchangeable, so both are expected (each with the shared door union), neither unmapped.
        const string json = """
            [
              { "group": "Everyone", "groupGuid": "GG", "scheduleGuid": "SG",
                "schedule": "(default) = 00:00:00;24:00:00;FFFF;FFFF", "memberCount": 2,
                "doors": [ { "panelName": "north", "panelIp": "10.0.0.1", "doorNo": 1, "doorName": "d" } ],
                "members": [
                  { "name": "Sam Twin", "employeeNo": "1", "personnelGuid": "G-1" },
                  { "name": "Sam Twin", "employeeNo": "2", "personnelGuid": "G-2" }
                ] }
            ]
            """;
        var map = IdentityMap.Build(
        [
            new CardholderIdentity { Fob = "10", Name = "Sam Twin", Source = "test" },
            new CardholderIdentity { Fob = "11", Name = "Sam Twin", Source = "test" },
        ], "test");

        var (byPanel, unmapped) = AccessReconciler.BuildExpectation(AccessPolicy.Parse(json), map);

        Assert.Equal(["10", "11"], byPanel["10.0.0.1"].Select(c => c.Fob).OrderBy(f => f));
        Assert.All(byPanel["10.0.0.1"], c => Assert.Equal([1], c.Doors));
        Assert.Empty(unmapped);
    }

    [Fact]
    public void BuildExpectationLeavesAGenuinelyUnresolvableSharedNameUnmapped()
    {
        // Same name, DIFFERENT door needs (one north, one south): the fobs are not interchangeable,
        // so neither can be attributed and both stay unmapped.
        const string json = """
            [
              { "group": "North", "groupGuid": "GN", "scheduleGuid": "SG",
                "schedule": "(default) = 00:00:00;24:00:00;FFFF;FFFF", "memberCount": 1,
                "doors": [ { "panelName": "north", "panelIp": "10.0.0.1", "doorNo": 1, "doorName": "d" } ],
                "members": [ { "name": "Sam Twin", "employeeNo": "1", "personnelGuid": "G-1" } ] },
              { "group": "South", "groupGuid": "GS", "scheduleGuid": "SG",
                "schedule": "(default) = 00:00:00;24:00:00;FFFF;FFFF", "memberCount": 1,
                "doors": [ { "panelName": "south", "panelIp": "10.0.0.2", "doorNo": 1, "doorName": "d" } ],
                "members": [ { "name": "Sam Twin", "employeeNo": "2", "personnelGuid": "G-2" } ] }
            ]
            """;
        var map = IdentityMap.Build(
        [
            new CardholderIdentity { Fob = "10", Name = "Sam Twin", Source = "test" },
            new CardholderIdentity { Fob = "11", Name = "Sam Twin", Source = "test" },
        ], "test");

        var (byPanel, unmapped) = AccessReconciler.BuildExpectation(AccessPolicy.Parse(json), map);

        Assert.Empty(byPanel);   // nothing attributed to either person
        Assert.Equal(2, unmapped.Count);
        Assert.All(unmapped, u => Assert.Contains("shared name", u));
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
