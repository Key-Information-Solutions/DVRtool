using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The fob → name map and roster enrichment. All data here is synthetic — the real
/// mapping is customer PII and never appears in the repo.
/// </summary>
public class IdentityMapTests
{
    private static CardholderIdentity Id(string fob, string name, string? org = null,
        string source = "test") =>
        new() { Fob = fob, Name = name, Organization = org, Source = source };

    [Fact]
    public void BuildDedupesByNormalizedFobLaterWins()
    {
        var map = IdentityMap.Build(
        [
            Id("0123", "Ada Older"),
            Id("123", "Ada Newer"),
        ], "test");

        Assert.Equal(1, map.Count);
        Assert.Equal("Ada Newer", map.Lookup("123")!.Name);
        // Leading-zero variant resolves to the same person.
        Assert.Equal("Ada Newer", map.Lookup("00123")!.Name);
    }

    [Fact]
    public void BuildDropsBlankFobsAndNames()
    {
        var map = IdentityMap.Build(
        [
            Id("", "No Fob"),
            Id("5", "   "),
            Id("7", "Real Person"),
        ], "test");

        Assert.Equal(1, map.Count);
        Assert.Equal("Real Person", map.Lookup("7")!.Name);
    }

    [Fact]
    public void LookupNormalizesBothSides()
    {
        var map = IdentityMap.Build([Id("42", "Grace H")], "test");
        Assert.Equal("Grace H", map.Lookup("  042 ")!.Name);
        Assert.Null(map.Lookup("99"));
    }

    [Fact]
    public void MergeOtherWinsOnConflict()
    {
        var a = IdentityMap.Build([Id("1", "Old One"), Id("2", "Keep Two")], "csv",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var b = IdentityMap.Build([Id("1", "New One", source: "expiry")], "expiry",
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        var merged = a.Merge(b);

        Assert.Equal(2, merged.Count);
        Assert.Equal("New One", merged.Lookup("1")!.Name);
        Assert.Equal("Keep Two", merged.Lookup("2")!.Name);
        // Combined provenance and the newer capture time survive.
        Assert.Equal("csv+expiry", merged.Source);
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), merged.CapturedAtUtc);
    }

    [Fact]
    public void MergeKeepsSingleSourceLabelWhenEqual()
    {
        var a = IdentityMap.Build([Id("1", "One")], "csv");
        var b = IdentityMap.Build([Id("2", "Two")], "csv");
        Assert.Equal("csv", a.Merge(b).Source);
    }

    [Fact]
    public void EnrichWithFillsOnlyBlankNames()
    {
        var roster = AccessRoster.Build(
        [
            new AccessPanelResult
            {
                PanelHost = "p",
                Cards =
                [
                    new AccessCard { CardNo = "1", Doors = [1] },
                    new AccessCard { CardNo = "2", Name = "Panel Knows", Doors = [1] },
                ],
            },
        ]);

        var map = IdentityMap.Build(
        [
            Id("1", "Imported Name", "Engineering"),
            Id("2", "Should Not Win", "Ignored"),
        ], "test");

        var enriched = roster.EnrichWith(map);

        var one = enriched.FindCard("1")!;
        Assert.Equal("Imported Name", one.Name);
        Assert.Equal("Engineering", one.Organization);
        // A panel-supplied name is authoritative and must not be overwritten.
        var two = enriched.FindCard("2")!;
        Assert.Equal("Panel Knows", two.Name);
        Assert.Null(two.Organization);
    }

    [Fact]
    public void EnrichWithLeavesUnmatchedEntriesUntouched()
    {
        var roster = AccessRoster.Build(
        [
            new AccessPanelResult { PanelHost = "p", Cards = [new AccessCard { CardNo = "9" }] },
        ]);
        var enriched = roster.EnrichWith(IdentityMap.Build([Id("1", "Somebody")], "test"));

        Assert.Null(enriched.FindCard("9")!.Name);
        Assert.False(enriched.HasNames);
    }

    [Fact]
    public void HasNamesReflectsEnrichment()
    {
        var roster = AccessRoster.Build(
        [
            new AccessPanelResult { PanelHost = "p", Cards = [new AccessCard { CardNo = "1" }] },
        ]);
        Assert.False(roster.HasNames);

        var enriched = roster.EnrichWith(IdentityMap.Build([Id("1", "Named")], "test"));
        Assert.True(enriched.HasNames);
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"dvrtool-identity-{Guid.NewGuid():N}.json");
        try
        {
            var map = IdentityMap.Build(
            [
                Id("100", "Round Trip", "Ops"),
            ], "test", new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc));

            IdentityMapStore.Save(map, path);
            var loaded = IdentityMapStore.Load(path);

            Assert.NotNull(loaded);
            Assert.Equal(1, loaded!.Count);
            Assert.Equal("Round Trip", loaded.Lookup("100")!.Name);
            Assert.Equal("Ops", loaded.Lookup("100")!.Organization);
            Assert.Equal("test", loaded.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadReturnsNullWhenFileMissing() =>
        Assert.Null(IdentityMapStore.Load(
            Path.Combine(Path.GetTempPath(), $"dvrtool-missing-{Guid.NewGuid():N}.json")));
}
