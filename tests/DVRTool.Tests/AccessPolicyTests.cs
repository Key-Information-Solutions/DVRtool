using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The policy loader and the door-union resolver — the heart of provisioning. All data is the
/// synthetic <see cref="AccessPolicyFixtures"/>; the real policy is customer PII and is never
/// loaded here.
/// </summary>
public class AccessPolicyTests
{
    private static AccessPolicy Sample() => AccessPolicyFixtures.Policy();

    [Fact]
    public void LoadsAllGroupsAndDoors()
    {
        var policy = Sample();
        Assert.Equal(3, policy.Groups.Count);

        var everyone = policy.FindGroup("Everyone");
        Assert.NotNull(everyone);
        Assert.Single(everyone!.Doors);
        Assert.Equal("10.0.0.1", everyone.Doors[0].PanelIp);
        Assert.Equal(1, everyone.Doors[0].DoorNo);
        Assert.Equal(2, everyone.MemberCount);
    }

    [Fact]
    public void GroupLookupIsCaseInsensitiveAndTrimmed()
    {
        var policy = Sample();
        Assert.NotNull(policy.FindGroup("  admins "));
        Assert.NotNull(policy.FindGroup("NIGHTGUARD"));
        Assert.Null(policy.FindGroup("nope"));
    }

    [Fact]
    public void PanelCatalogIsDistinctAndIpOrdered()
    {
        var panels = Sample().Panels;
        Assert.Equal(["10.0.0.1", "10.0.0.2"], panels.Select(p => p.PanelIp));
        Assert.Equal("north", panels[0].PanelName);
        Assert.Equal("south", panels[1].PanelName);
    }

    [Fact]
    public void ResolveGrantsForOneGroupOnOnePanel()
    {
        var grants = Sample().ResolveGrants(["Everyone"]);
        Assert.Single(grants);
        Assert.Equal("10.0.0.1", grants[0].PanelIp);
        Assert.Equal([1], grants[0].Doors);
    }

    [Fact]
    public void ResolveGrantsForAGroupThatSpansTwoPanels()
    {
        var grants = Sample().ResolveGrants(["Admins"]);
        Assert.Equal(["10.0.0.1", "10.0.0.2"], grants.Select(g => g.PanelIp));
        Assert.Equal([2], grants[0].Doors);
        Assert.Equal([1], grants[1].Doors);
    }

    [Fact]
    public void ResolveGrantsUnionsDoorsPerPanelAcrossGroups()
    {
        // Alpha's real case: Everyone (door 1) + Admins (door 2 on the same panel, plus a
        // second panel). The panel-1 doors must be the UNION, not a replacement.
        var grants = Sample().ResolveGrants(["Everyone", "Admins"]);
        Assert.Equal(["10.0.0.1", "10.0.0.2"], grants.Select(g => g.PanelIp));
        Assert.Equal([1, 2], grants[0].Doors);
        Assert.Equal([1], grants[1].Doors);
    }

    [Fact]
    public void ResolveGrantsIsCaseInsensitiveAndIgnoresDuplicateGroupNames()
    {
        var grants = Sample().ResolveGrants(["everyone", "EVERYONE"]);
        Assert.Single(grants);
        Assert.Equal([1], grants[0].Doors);
    }

    [Fact]
    public void UnknownGroupIsAClearError()
    {
        var ex = Assert.Throws<NvrException>(() => Sample().ResolveGrants(["Everyone", "Wizards"]));
        Assert.Contains("Wizards", ex.Message);
        Assert.Contains("unknown access group", ex.Message);
    }

    [Fact]
    public void ResolveGrantsRejectsAnEmptyGroupSet()
    {
        Assert.Throws<ArgumentException>(() => Sample().ResolveGrants([]));
        Assert.Throws<ArgumentException>(() => Sample().ResolveGrants(["  "]));
    }

    [Theory]
    [InlineData("(default) = 00:00:00;24:00:00;FFFF;FFFF", true)]
    [InlineData("Admin = 00:00:00;24:00:00;ffff;ffff", true)]   // masks are case-insensitive
    [InlineData("Admin = 00:02:00;24:00:00;FFFF;FFFF", false)]  // the live non-24/7 quirk
    [InlineData("Restricted = 22:00:00;06:00:00;FFFF;FFFF", false)]
    [InlineData("00:00:00;24:00:00;FFFF;FFFF", true)]           // no label
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("garbage", false)]
    public void Is24x7RecognizesTheAlwaysOnTemplateOnly(string? schedule, bool expected) =>
        Assert.Equal(expected, AccessSchedule.Is24x7(schedule));

    [Fact]
    public void GroupIs24x7FlagReflectsTheSchedule()
    {
        var policy = Sample();
        Assert.True(policy.FindGroup("Everyone")!.Is24x7);
        Assert.True(policy.FindGroup("Admins")!.Is24x7);
        Assert.False(policy.FindGroup("NightGuard")!.Is24x7);
    }

    [Fact]
    public void ParseRejectsEmptyAndMalformedPolicies()
    {
        Assert.Throws<NvrException>(() => AccessPolicy.Parse("[]"));
        Assert.Throws<NvrException>(() => AccessPolicy.Parse("{ not json"));
        // A door with no panelIp cannot be provisioned — the writer drives off the IP.
        Assert.Throws<NvrException>(() => AccessPolicy.Parse(
            """[ { "group": "X", "doors": [ { "doorNo": 1 } ], "members": [] } ]"""));
    }

    [Fact]
    public void LoadThrowsOnMissingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dvrtool-nopolicy-{Guid.NewGuid():N}.json");
        Assert.Throws<NvrException>(() => AccessPolicy.Load(path));
    }

    [Fact]
    public void LoadReadsFromDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dvrtool-policy-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, AccessPolicyFixtures.Json);
            var policy = AccessPolicy.Load(path);
            Assert.Equal(3, policy.Groups.Count);
            Assert.Equal([1, 2], policy.ResolveGrants(["Everyone", "Admins"])[0].Doors);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
