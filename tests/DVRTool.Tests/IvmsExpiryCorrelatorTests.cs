using DVRTool.Core;
using DVRTool.Vendors.HikvisionIvms;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// Correlating iVMS names to panel fobs by unique expiry — the cipher-free partial fallback.
/// Synthetic timestamps and names throughout.
/// </summary>
public class IvmsExpiryCorrelatorTests
{
    private static readonly DateTime T1 = new(2028, 1, 1, 8, 0, 0);
    private static readonly DateTime T2 = new(2029, 6, 15, 17, 30, 0);
    private static readonly DateTime T3 = new(2030, 12, 31, 23, 59, 59);

    private static IvmsPerson Person(string name, DateTime? expire) =>
        new() { PersonnelGuid = Guid.NewGuid().ToString(), Name = name, ExpireTime = expire };

    private static AccessRoster RosterWith(params (string Fob, DateTime? Until)[] cards)
    {
        var accessCards = cards.Select(c => new AccessCard
        {
            CardNo = c.Fob,
            Doors = [1],
            Valid = true,
            ValidUntil = c.Until,
        }).ToArray();
        return AccessRoster.Build([new AccessPanelResult { PanelHost = "p", Cards = accessCards }]);
    }

    [Fact]
    public void UniqueExpiryBindsNameToFob()
    {
        var persons = new[] { Person("Alice A", T1), Person("Bob B", T2) };
        var roster = RosterWith(("100", T1), ("200", T2));

        var result = IvmsExpiryCorrelator.Correlate(persons, roster);

        Assert.Equal(2, result.Matched);
        Assert.Equal(0, result.AmbiguousExpiries);
        Assert.Equal("Alice A", result.Map.Lookup("100")!.Name);
        Assert.Equal("Bob B", result.Map.Lookup("200")!.Name);
        Assert.Equal("ivms-expiry", result.Map.Lookup("100")!.Source);
    }

    [Fact]
    public void DuplicateExpiryAmongPersonsIsAmbiguous()
    {
        // Two people share T1, so the single card at T1 cannot be attributed to either.
        var persons = new[] { Person("Cara C", T1), Person("Dan D", T1), Person("Eve E", T2) };
        var roster = RosterWith(("100", T1), ("200", T2));

        var result = IvmsExpiryCorrelator.Correlate(persons, roster);

        Assert.Equal(1, result.Matched);                 // only Eve/200 binds
        Assert.Equal("Eve E", result.Map.Lookup("200")!.Name);
        Assert.Null(result.Map.Lookup("100"));
        Assert.Equal(2, result.AmbiguousExpiries);       // Cara + Dan
        Assert.Equal(2, result.UnmatchedPersons);
    }

    [Fact]
    public void DuplicateExpiryAmongCardsIsAmbiguous()
    {
        // Two cards share T1, so the single person at T1 is not uniquely a fob.
        var persons = new[] { Person("Faye F", T1), Person("Gus G", T2) };
        var roster = RosterWith(("100", T1), ("101", T1), ("200", T2));

        var result = IvmsExpiryCorrelator.Correlate(persons, roster);

        Assert.Equal(1, result.Matched);                 // only Gus/200
        Assert.Equal("Gus G", result.Map.Lookup("200")!.Name);
        Assert.Null(result.Map.Lookup("100"));
        Assert.Null(result.Map.Lookup("101"));
        Assert.Equal(1, result.AmbiguousExpiries);       // Faye
    }

    [Fact]
    public void PersonWithoutExpiryIsUnmatchedNotAmbiguous()
    {
        var persons = new[] { Person("Hana H", null), Person("Ivan I", T3) };
        var roster = RosterWith(("300", T3));

        var result = IvmsExpiryCorrelator.Correlate(persons, roster);

        Assert.Equal(1, result.Matched);
        Assert.Equal("Ivan I", result.Map.Lookup("300")!.Name);
        Assert.Equal(1, result.UnmatchedPersons);
        Assert.Equal(0, result.AmbiguousExpiries);
        Assert.Contains(result.Notes, n => n.Contains("no expiry"));
    }

    [Fact]
    public void PersonExpiryWithNoMatchingCardIsUnmatched()
    {
        var persons = new[] { Person("Jo J", T1) };
        var roster = RosterWith(("999", T2));   // different expiry, no overlap

        var result = IvmsExpiryCorrelator.Correlate(persons, roster);

        Assert.Equal(0, result.Matched);
        Assert.Equal(0, result.AmbiguousExpiries);
        Assert.Equal(1, result.UnmatchedPersons);
        Assert.Equal(0, result.Map.Count);
    }

    [Fact]
    public void SubSecondDifferencesStillMatchToTheSecond()
    {
        var persons = new[] { Person("Kim K", T1.AddMilliseconds(400)) };
        var roster = RosterWith(("400", T1));

        var result = IvmsExpiryCorrelator.Correlate(persons, roster);
        Assert.Equal(1, result.Matched);
        Assert.Equal("Kim K", result.Map.Lookup("400")!.Name);
    }
}
