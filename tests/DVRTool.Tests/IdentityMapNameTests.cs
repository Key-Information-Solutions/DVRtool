using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// Name-based lookup and the add/remove helpers onboard/offboard rely on. Synthetic data only.
/// </summary>
public class IdentityMapNameTests
{
    private static CardholderIdentity Id(string fob, string name, string source = "test") =>
        new() { Fob = fob, Name = name, Source = source };

    [Theory]
    [InlineData("Alpha Uno")]
    [InlineData("alpha uno")]
    [InlineData("Alpha.Uno")]
    [InlineData("  alpha_uno ")]
    [InlineData("Alpha   Uno")]
    public void FindByNameNormalizesSeparatorsAndCase(string query)
    {
        var map = IdentityMap.Build([Id("9001", "Alpha Uno")], "test");
        var hits = map.FindByName(query);
        Assert.Single(hits);
        Assert.Equal("9001", hits[0].Fob);
    }

    [Fact]
    public void FindByNameReturnsEveryFobForADuplicatedName()
    {
        var map = IdentityMap.Build([Id("1", "Dup Name"), Id("2", "Dup Name")], "test");
        Assert.Equal(["1", "2"], map.FindByName("dup.name").Select(h => h.Fob).OrderBy(f => f));
    }

    [Fact]
    public void FindByNameIsEmptyWhenAbsentOrBlank()
    {
        var map = IdentityMap.Build([Id("9001", "Alpha Uno")], "test");
        Assert.Empty(map.FindByName("Nobody"));
        Assert.Empty(map.FindByName(""));
        Assert.Empty(map.FindByName("   "));
    }

    [Fact]
    public void WithoutRemovesTheFobEvenAcrossLeadingZeroVariants()
    {
        var map = IdentityMap.Build([Id("0123", "Ada"), Id("200", "Bee")], "test");
        var trimmed = map.Without("123");   // same card as "0123"
        Assert.Null(trimmed.Lookup("123"));
        Assert.Equal(1, trimmed.Count);
        Assert.Equal("Bee", trimmed.Lookup("200")!.Name);
    }

    [Fact]
    public void WithAddsANewBindingAndRecordsProvenance()
    {
        var map = IdentityMap.Build([Id("100", "Ada", "ivms-db")], "ivms-db");
        var updated = map.With(Id("9001", "New Hire", "onboard"));

        Assert.Equal(2, updated.Count);
        Assert.Equal("New Hire", updated.Lookup("9001")!.Name);
        Assert.Equal("Ada", updated.Lookup("100")!.Name);
        Assert.Contains("onboard", updated.Source);
    }

    [Fact]
    public void WithSupersedesAnExistingFob()
    {
        var map = IdentityMap.Build([Id("9001", "Old Holder")], "test");
        var updated = map.With(Id("9001", "New Holder", "onboard"));
        Assert.Equal(1, updated.Count);
        Assert.Equal("New Holder", updated.Lookup("9001")!.Name);
    }
}
