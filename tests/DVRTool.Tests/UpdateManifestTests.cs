using DVRTool.Core.Updates;
using Xunit;

namespace DVRTool.Tests;

public class UpdateManifestTests
{
    private const string Good = """
        {
          "version": "1.2.3",
          "file": "DVRTool-1.2.3.msi",
          "size": 157286400,
          "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
          "notesUrl": "https://github.com/Key-Information-Solutions/DVRtool/releases/tag/v1.2.3",
          "publishedAt": "2026-09-11T14:00:00+00:00",
          "keyId": "a68b6dde"
        }
        """;

    [Fact]
    public void Parses_a_well_formed_manifest()
    {
        var m = UpdateManifest.Parse(Good);
        Assert.Equal(new Version(1, 2, 3), m.Version);
        Assert.Equal("DVRTool-1.2.3.msi", m.FileName);
        Assert.Equal(157286400, m.Size);
        Assert.Equal("a68b6dde", m.KeyId);
        Assert.True(m.Accepts(new Version(1, 0, 0)));
    }

    [Theory]
    [InlineData("\"version\": \"1.2.3\"", "\"version\": \"1.2.3-beta\"")]
    [InlineData("\"version\": \"1.2.3\"", "\"version\": \"1.2.3.4\"")]
    [InlineData("\"file\": \"DVRTool-1.2.3.msi\"", "\"file\": \"..\\\\evil.msi\"")]
    [InlineData("\"file\": \"DVRTool-1.2.3.msi\"", "\"file\": \"setup.exe\"")]
    [InlineData("\"size\": 157286400", "\"size\": 0")]
    [InlineData("\"sha256\": \"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08\"", "\"sha256\": \"9f86\"")]
    public void Rejects_malformed_fields(string from, string to)
    {
        string bad = Good.Replace(from, to);
        Assert.NotEqual(Good, bad);
        Assert.Throws<FormatException>(() => UpdateManifest.Parse(bad));
    }

    [Fact]
    public void Rejects_non_json()
    {
        Assert.Throws<FormatException>(() => UpdateManifest.Parse("<html>rate limited</html>"));
    }

    [Fact]
    public void Round_trips_through_the_bytes_the_tool_signs()
    {
        var m = UpdateManifest.Parse(Good);
        var again = UpdateManifest.Parse(m.ToJsonBytes());
        Assert.Equal(m, again);
    }

    [Fact]
    public void Minimum_version_gates_old_installs()
    {
        var m = UpdateManifest.Parse(Good) with { MinimumVersion = "1.1.0" };
        Assert.False(m.Accepts(new Version(1, 0, 2)));
        Assert.True(m.Accepts(new Version(1, 1, 0)));
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    public void Product_version_parses_tags_and_numbers(string text, int a, int b, int c)
    {
        Assert.True(ProductVersion.TryParse(text, out var v));
        Assert.Equal(new Version(a, b, c), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3+abc")]
    [InlineData("latest")]
    public void Product_version_refuses_what_the_msi_would(string text) =>
        Assert.False(ProductVersion.TryParse(text, out _));
}
