using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// Onboard/offboard planning: proving the right per-panel <see cref="AccessCard"/> set (onboard)
/// and revoke set (offboard) fall out of a mocked policy + identity map. No panel contact.
/// </summary>
public class AccessProvisionerTests
{
    private static AccessPolicy Policy() => AccessPolicyFixtures.Policy();

    private static CardholderIdentity Id(string fob, string name) =>
        new() { Fob = fob, Name = name, Source = "test" };

    // ---------- onboard ----------

    [Fact]
    public void OnboardWritesOneActiveCardPerPanelWithTheDoorUnion()
    {
        var plan = AccessProvisioner.PlanOnboard(Policy(), "Test.User", "9001", ["Everyone", "Admins"]);

        Assert.Equal("9001", plan.Fob);
        Assert.Equal(["10.0.0.1", "10.0.0.2"], plan.Panels);

        var north = plan.Writes.Single(w => w.PanelIp == "10.0.0.1").Card;
        Assert.Equal("9001", north.CardNo);
        Assert.True(north.Valid);
        Assert.Equal(AccessCardType.Normal, north.Type);
        Assert.Equal([1, 2], north.Doors);          // UNION of Everyone(1) + Admins(2)
        Assert.Equal("10.0.0.1", north.PanelHost);

        var south = plan.Writes.Single(w => w.PanelIp == "10.0.0.2").Card;
        Assert.Equal([1], south.Doors);
    }

    [Fact]
    public void OnboardGivesEveryCardTheSameBoundedValidityWindow()
    {
        var plan = AccessProvisioner.PlanOnboard(Policy(), "Test.User", "9001", ["Everyone"]);

        // Bounded, long, and never unbounded — the property offboarding relies on.
        Assert.True(plan.ValidUntil > plan.ValidFrom);
        Assert.True(plan.ValidUntil > plan.ValidFrom.AddYears(9));
        Assert.True(plan.ValidUntil < plan.ValidFrom.AddYears(11));

        var card = plan.Writes.Single().Card;
        Assert.Equal(plan.ValidFrom, card.ValidFrom);
        Assert.Equal(plan.ValidUntil, card.ValidUntil);
    }

    [Fact]
    public void OnboardHonorsAnExplicitValidUntil()
    {
        var until = new DateTime(2027, 1, 1, 0, 0, 0);
        var plan = AccessProvisioner.PlanOnboard(Policy(), "Test.User", "9001", ["Everyone"],
            validUntil: until);
        Assert.Equal(until, plan.ValidUntil);
        Assert.Equal(until, plan.Writes.Single().Card.ValidUntil);
    }

    [Fact]
    public void OnboardWarnsWhenAGroupIsNotContinuous()
    {
        var plan = AccessProvisioner.PlanOnboard(Policy(), "Guard.One", "9002", ["NightGuard"]);

        // The write still happens (always-on, plan 1), but the approximation is never silent.
        Assert.Single(plan.Writes);
        Assert.Equal([2], plan.Writes.Single().Card.Doors);
        var warning = Assert.Single(plan.ScheduleWarnings);
        Assert.Contains("NightGuard", warning);
        Assert.Contains("not 24/7", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnboardIntoContinuousGroupsHasNoScheduleWarning()
    {
        var plan = AccessProvisioner.PlanOnboard(Policy(), "Test.User", "9001", ["Everyone", "Admins"]);
        Assert.Empty(plan.ScheduleWarnings);
    }

    [Fact]
    public void OnboardTrimsTheFobAndName()
    {
        var plan = AccessProvisioner.PlanOnboard(Policy(), "  Test.User  ", "  9001 ", ["Everyone"]);
        Assert.Equal("9001", plan.Fob);
        Assert.Equal("Test.User", plan.Name);
        Assert.Equal("9001", plan.Writes.Single().Card.CardNo);
    }

    [Fact]
    public void OnboardRejectsUnknownGroupsAndMissingInputs()
    {
        Assert.Throws<NvrException>(() =>
            AccessProvisioner.PlanOnboard(Policy(), "Test.User", "9001", ["Nope"]));
        Assert.Throws<ArgumentException>(() =>
            AccessProvisioner.PlanOnboard(Policy(), "Test.User", "", ["Everyone"]));
        Assert.Throws<ArgumentException>(() =>
            AccessProvisioner.PlanOnboard(Policy(), "Test.User", "9001", []));
        Assert.Throws<ArgumentException>(() =>
            AccessProvisioner.PlanOnboard(Policy(), "", "9001", ["Everyone"]));
    }

    [Fact]
    public void OnboardRejectsAnInvertedValidityWindow()
    {
        Assert.Throws<ArgumentException>(() =>
            AccessProvisioner.PlanOnboard(Policy(), "Test.User", "9001", ["Everyone"],
                validFrom: new DateTime(2027, 1, 1), validUntil: new DateTime(2026, 1, 1)));
    }

    // ---------- offboard ----------

    [Fact]
    public void OffboardResolvesTheNameToAFobAndRevokesEverywhere()
    {
        var map = IdentityMap.Build([Id("9001", "Alpha Uno"), Id("200", "Beta Dos")], "test");
        var panels = new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3" };

        var plan = AccessProvisioner.PlanOffboard("Alpha Uno", map, panels);
        Assert.Equal("9001", plan.Fob);
        Assert.Equal(panels, plan.PanelRevokes);   // ALL panels, regardless of group
    }

    [Fact]
    public void OffboardMatchesTheDotConventionAgainstAnIvmsSpaceName()
    {
        // The CLI convention is First.Last; iVMS stores "First Last". They must resolve alike.
        var map = IdentityMap.Build([Id("9001", "Alpha Uno")], "test");
        var plan = AccessProvisioner.PlanOffboard("Alpha.Uno", map, ["10.0.0.1"]);
        Assert.Equal("9001", plan.Fob);
    }

    [Fact]
    public void OffboardFailsClearlyWithNoMap()
    {
        var ex = Assert.Throws<NvrException>(() =>
            AccessProvisioner.PlanOffboard("Alpha Uno", null, ["10.0.0.1"]));
        Assert.Contains("no cardholder-name map", ex.Message);
    }

    [Fact]
    public void OffboardFailsWhenTheNameIsUnknown()
    {
        var map = IdentityMap.Build([Id("9001", "Alpha Uno")], "test");
        var ex = Assert.Throws<NvrException>(() =>
            AccessProvisioner.PlanOffboard("Nobody Here", map, ["10.0.0.1"]));
        Assert.Contains("no fob is mapped", ex.Message);
    }

    [Fact]
    public void OffboardRefusesToGuessWhenANameMapsToSeveralFobs()
    {
        var map = IdentityMap.Build([Id("1", "Dup Name"), Id("2", "Dup Name")], "test");
        var ex = Assert.Throws<NvrException>(() =>
            AccessProvisioner.PlanOffboard("Dup Name", map, ["10.0.0.1"]));
        Assert.Contains("maps to 2 fobs", ex.Message);
    }
}
