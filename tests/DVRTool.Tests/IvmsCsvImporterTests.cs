using DVRTool.Vendors.HikvisionIvms;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The supported iVMS Person CSV export → name map. Synthetic data only; the real export is
/// customer PII and never lives in the repo.
/// </summary>
public class IvmsCsvImporterTests
{
    private static IReadOnlyList<(string Fob, string Name)> Parse(string csv)
    {
        using var reader = new StringReader(csv);
        var map = IvmsCsvImporter.Parse(reader);
        return map.Identities.Select(i => (i.Fob, i.Name)).ToList();
    }

    [Fact]
    public void ParsesNameAndCardNoHeaders()
    {
        var rows = Parse(
            "Name,Card No\n" +
            "Alice Example,1075\n" +
            "Bob Sample,2375\n");

        Assert.Equal(2, rows.Count);
        Assert.Contains(("1075", "Alice Example"), rows);
        Assert.Contains(("2375", "Bob Sample"), rows);
    }

    [Fact]
    public void JoinsFirstAndLastNameWhenNoFullNameColumn()
    {
        var rows = Parse(
            "First Name,Last Name,Card Number\n" +
            "Carol,Tester,42\n");

        Assert.Equal(("42", "Carol Tester"), Assert.Single(rows));
    }

    [Fact]
    public void HandlesQuotedCommaFields()
    {
        var rows = Parse(
            "Card No,Name\n" +
            "7,\"Doe, Jane\"\n");

        Assert.Equal(("7", "Doe, Jane"), Assert.Single(rows));
    }

    [Fact]
    public void HandlesDoubledQuotesInsideQuotedField()
    {
        var rows = Parse(
            "Card No,Name\n" +
            "8,\"The \"\"Chief\"\"\"\n");

        Assert.Equal(("8", "The \"Chief\""), Assert.Single(rows));
    }

    [Fact]
    public void StripsUtf8Bom()
    {
        var rows = Parse(
            "﻿Name,Card No\n" +
            "Dana Byte,55\n");

        Assert.Equal(("55", "Dana Byte"), Assert.Single(rows));
    }

    [Fact]
    public void UnifiesLeadingZeroFobsViaMapDedupe()
    {
        // Both rows normalize to the same fob, so the map keeps one (later wins).
        var rows = Parse(
            "Name,Card No\n" +
            "Old Label,0123\n" +
            "New Label,123\n");

        Assert.Equal(("123", "New Label"), Assert.Single(rows));
    }

    [Fact]
    public void SkipsRowsMissingCardOrName()
    {
        var rows = Parse(
            "Name,Card No\n" +
            ",999\n" +          // no name
            "No Card,\n" +      // no card
            "Real One,12\n" +
            "\n");              // blank line

        Assert.Equal(("12", "Real One"), Assert.Single(rows));
    }

    [Fact]
    public void CarriesOrganizationWhenColumnPresent()
    {
        using var reader = new StringReader(
            "Name,Card No,Organization\n" +
            "Eve Ops,3,Facilities\n");
        var map = IvmsCsvImporter.Parse(reader);

        var identity = map.Lookup("3");
        Assert.NotNull(identity);
        Assert.Equal("Facilities", identity!.Organization);
        Assert.Equal("ivms-csv", identity.Source);
    }

    [Fact]
    public void HandlesCrlfLineEndings()
    {
        var rows = Parse("Name,Card No\r\nFrank CR,61\r\n");
        Assert.Equal(("61", "Frank CR"), Assert.Single(rows));
    }

    [Fact]
    public void EmptyInputYieldsEmptyMap() =>
        Assert.Empty(Parse(""));
}
