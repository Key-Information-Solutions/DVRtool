using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// What a fob revoke would touch, worked out from a roster. The interesting cases are all
/// about what the plan refuses to claim: a panel that did not answer is not a panel where the
/// fob is inactive, and "already revoked" is not "revoked by us".
/// </summary>
public class CardRevokePlanTests
{
    private static AccessCard Card(string no, bool valid, params int[] doors) =>
        new() { CardNo = no, Doors = doors, Valid = valid };

    private static AccessPanelResult Panel(string host, params AccessCard[] cards) =>
        new() { PanelHost = host, Cards = cards };

    [Fact]
    public void PlansAWriteForEveryPanelHoldingTheFobActive()
    {
        var roster = AccessRoster.Build([
            Panel("10.0.0.1", Card("2375", true, 1)),
            Panel("10.0.0.2", Card("2375", true, 1, 2, 3, 4)),
        ]);

        var plan = CardRevokePlan.For(roster, "2375");

        Assert.True(plan.HasWork);
        Assert.False(plan.IsPartial);
        Assert.False(plan.NotFound);
        Assert.Equal(["10.0.0.1", "10.0.0.2"], plan.Revokes.Select(r => r.PanelHost));
        // Doors are what the confirmation names, and they differ per panel.
        Assert.Equal("1", plan.Revokes[0].DoorSummary);
        Assert.Equal("1,2,3,4", plan.Revokes[1].DoorSummary);
    }

    [Fact]
    public void LeavesAlreadyRevokedPanelsOutOfTheWrite()
    {
        var roster = AccessRoster.Build([
            Panel("10.0.0.1", Card("2375", false, 1)),
            Panel("10.0.0.2", Card("2375", true, 2)),
        ]);

        var plan = CardRevokePlan.For(roster, "2375");

        Assert.Equal(["10.0.0.2"], plan.Revokes.Select(r => r.PanelHost));
        Assert.Equal(["10.0.0.1"], plan.AlreadyRevoked);
    }

    [Fact]
    public void AFobRevokedEverywhereIsFoundButHasNoWork()
    {
        var roster = AccessRoster.Build([Panel("10.0.0.1", Card("2375", false, 1))]);

        var plan = CardRevokePlan.For(roster, "2375");

        Assert.False(plan.HasWork);
        // Found, so the operator is told it is already revoked rather than that it does not exist.
        Assert.False(plan.NotFound);
        Assert.Equal(["10.0.0.1"], plan.AlreadyRevoked);
    }

    [Fact]
    public void AnUnreadPanelMakesThePlanPartialEvenWhenTheWriteIsComplete()
    {
        // The whole reason the type exists: the fob can still be opening doors on a panel
        // nobody could read, so a successful write is not "access removed".
        var roster = AccessRoster.Build([
            Panel("10.0.0.1", Card("2375", true, 1)),
            AccessPanelResult.Failed("10.0.0.2", "login timed out"),
        ]);

        var plan = CardRevokePlan.For(roster, "2375");

        Assert.True(plan.HasWork);
        Assert.True(plan.IsPartial);
        Assert.Equal(["10.0.0.2"], plan.Unreadable.Select(p => p.PanelHost));
        Assert.Equal("login timed out", plan.Unreadable[0].Error);
    }

    [Fact]
    public void AFobNobodyHoldsIsNotFoundRatherThanRevokedEverywhere()
    {
        var roster = AccessRoster.Build([Panel("10.0.0.1", Card("1075", true, 1))]);

        var plan = CardRevokePlan.For(roster, "2375");

        Assert.True(plan.NotFound);
        Assert.False(plan.HasWork);
        Assert.Empty(plan.AlreadyRevoked);
    }

    [Fact]
    public void MatchesTheFobTheDeviceMeansAndReportsItsOwnSpelling()
    {
        // The panels compare card numbers as integers and refuse to hold both "123" and
        // "0123" — so a typed "0123" must revoke the card the panel spells "123".
        var roster = AccessRoster.Build([Panel("10.0.0.1", Card("123", true, 1))]);

        var plan = CardRevokePlan.For(roster, "0123");

        Assert.True(plan.HasWork);
        Assert.Equal("123", plan.CardNo);
    }

    [Fact]
    public void CarriesTheEnrichedNameSoTheConfirmationCanSayWho()
    {
        var roster = AccessRoster.Build([Panel("10.0.0.1", Card("2375", true, 1))])
            .EnrichWith(IdentityMap.Build(
                [new CardholderIdentity { Fob = "2375", Name = "A. Holder", Source = "test" }],
                "test"));

        Assert.Equal("A. Holder", CardRevokePlan.For(roster, "2375").Name);
    }

    [Fact]
    public void RefusesABlankFob()
    {
        var roster = AccessRoster.Build([Panel("10.0.0.1", Card("2375", true, 1))]);
        Assert.Throws<ArgumentException>(() => CardRevokePlan.For(roster, "   "));
    }
}
