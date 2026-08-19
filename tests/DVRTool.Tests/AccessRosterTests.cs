using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// Cross-panel roster aggregation. Modeled on the real Site A layout, where one panel
/// holds the whole fob roster and the others hold strict subsets — access is per-door, not
/// uniform, so "missing from panel B" is normal and must not be reported as drift by itself.
/// </summary>
public class AccessRosterTests
{
    private static AccessCard Card(string no, params int[] doors) =>
        new() { CardNo = no, Doors = doors, Valid = true };

    private static AccessPanelResult Panel(string host, params AccessCard[] cards) =>
        new() { PanelHost = host, Cards = cards };

    [Fact]
    public void UnifiesOneFobAcrossPanels()
    {
        var roster = AccessRoster.Build([
            Panel("10.0.0.1", Card("2375", 1), Card("1075", 1)),
            Panel("10.0.0.2", Card("2375", 1, 2, 3, 4)),
        ]);

        var entry = roster.FindCard("2375");
        Assert.NotNull(entry);
        Assert.Equal(2, entry!.Presence.Count);
        Assert.Equal(["10.0.0.1", "10.0.0.2"], entry.Panels);
        // Doors are per-panel, so they are reported per-panel rather than merged.
        Assert.Equal([1], entry.Presence[0].Doors);
        Assert.Equal([1, 2, 3, 4], entry.Presence[1].Doors);
        Assert.Equal(5, entry.TotalDoors);
    }

    [Fact]
    public void CardNumbersJoinNumericallySoLeadingZerosUnify()
    {
        // The device refuses to hold both "1" and "01" — it compares integer values. A
        // textual join would invent a phantom "missing from the other panel".
        var roster = AccessRoster.Build([
            Panel("a", Card("0123", 1)),
            Panel("b", Card("123", 2)),
        ]);

        Assert.Single(roster.Entries);
        Assert.Equal(2, roster.Entries[0].Presence.Count);
        Assert.NotNull(roster.FindCard("00123"));
        Assert.NotNull(roster.FindCard("123"));
    }

    [Fact]
    public void ReportsTheCardNumberAsTheDeviceSpellsIt()
    {
        var roster = AccessRoster.Build([Panel("a", Card("007", 1))]);
        Assert.Equal("007", roster.Entries[0].CardNo);
    }

    [Fact]
    public void SortsNumericFobsNumerically()
    {
        var roster = AccessRoster.Build([
            Panel("a", Card("1075"), Card("99"), Card("2375"), Card("310")),
        ]);
        Assert.Equal(["99", "310", "1075", "2375"], roster.Entries.Select(e => e.CardNo));
    }

    [Fact]
    public void NonNumericFobsSortAfterNumericOnes()
    {
        var roster = AccessRoster.Build([Panel("a", Card("ABC123"), Card("42"))]);
        Assert.Equal(["42", "ABC123"], roster.Entries.Select(e => e.CardNo));
    }

    [Fact]
    public void SurfacesAFailedPanelInsteadOfDroppingIt()
    {
        // The critical safety property: a panel that could not be read must never look like
        // a panel on which the fob is absent.
        var roster = AccessRoster.Build([
            Panel("10.0.0.1", Card("2375", 1)),
            AccessPanelResult.Failed("10.0.0.2", "connect failed (7)"),
        ]);

        Assert.True(roster.IsPartial);
        Assert.Equal(["10.0.0.2"], roster.FailedPanels.Select(p => p.PanelHost));
        Assert.Single(roster.FindCard("2375")!.Presence);
    }

    [Fact]
    public void AllPanelsReadableMeansNotPartial()
    {
        var roster = AccessRoster.Build([Panel("a", Card("1", 1)), Panel("b")]);
        Assert.False(roster.IsPartial);
        Assert.Empty(roster.FailedPanels);
    }

    [Fact]
    public void MissingFromFindsRealDrift()
    {
        var roster = AccessRoster.Build([
            Panel("full", Card("1"), Card("2"), Card("3")),
            Panel("subset", Card("2")),
        ]);

        Assert.Equal(["1", "3"],
            roster.MissingFrom("subset", "full").Select(e => e.CardNo));
        Assert.Empty(roster.MissingFrom("full", "subset"));
    }

    [Fact]
    public void OnlyOnFindsFobsUniqueToOnePanel()
    {
        var roster = AccessRoster.Build([
            Panel("full", Card("1"), Card("2")),
            Panel("subset", Card("2")),
        ]);

        Assert.Equal(["1"], roster.OnlyOn("full").Select(e => e.CardNo));
        Assert.Empty(roster.OnlyOn("subset"));
    }

    [Fact]
    public void RevokedEverywhereIsDistinctFromAbsent()
    {
        var roster = AccessRoster.Build([
            Panel("a", new AccessCard { CardNo = "9", Valid = false }),
            Panel("b", new AccessCard { CardNo = "9", Valid = false }),
        ]);

        var entry = roster.FindCard("9");
        Assert.NotNull(entry);
        Assert.True(entry!.FullyRevoked);
        Assert.Equal(0, entry.TotalDoors);
        // Absent is a null entry; revoked is an entry that opens nothing. Not the same thing.
        Assert.Null(roster.FindCard("10"));
    }

    [Fact]
    public void PartlyRevokedIsNotFullyRevoked()
    {
        var roster = AccessRoster.Build([
            Panel("a", new AccessCard { CardNo = "9", Valid = false }),
            Panel("b", Card("9", 1)),
        ]);

        Assert.False(roster.FindCard("9")!.FullyRevoked);
    }

    [Fact]
    public void NameIsPickedUpFromWhicheverPanelKnowsIt()
    {
        var roster = AccessRoster.Build([
            Panel("a", Card("5", 1)),
            Panel("b", new AccessCard { CardNo = "5", Name = "First.Last", Doors = [1] }),
        ]);

        Assert.Equal("First.Last", roster.FindCard("5")!.Name);
        Assert.True(roster.AnyPanelStoresNames);
        Assert.Equal(["5"], roster.FindByName("first.l").Select(e => e.CardNo));
    }

    [Fact]
    public void NameSearchIsEmptyWhenNoPanelStoresNames()
    {
        // This is the live Site A situation. The caller has to distinguish it from
        // "this person has no access", which is why AnyPanelStoresNames exists.
        var roster = AccessRoster.Build([Panel("a", Card("1", 1)), Panel("b", Card("2", 1))]);

        Assert.False(roster.AnyPanelStoresNames);
        Assert.Empty(roster.FindByName("anyone"));
    }

    [Fact]
    public void EmptyFleetIsHandled()
    {
        var roster = AccessRoster.Build([]);
        Assert.Empty(roster.Entries);
        Assert.False(roster.IsPartial);
        Assert.Null(roster.FindCard("1"));
    }

    [Theory]
    [InlineData("0123", "123")]
    [InlineData("123", "123")]
    [InlineData("0", "0")]
    [InlineData("0000", "0")]
    [InlineData("  42 ", "42")]
    [InlineData("abc", "ABC")]
    public void NormalizesCardNumbers(string input, string expected) =>
        Assert.Equal(expected, AccessRoster.NormalizeCardNo(input));
}
