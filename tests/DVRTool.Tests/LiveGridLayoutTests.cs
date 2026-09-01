using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The grid arithmetic. The page cap is the one number a live measurement decided (16
/// independent LibVLC players is fine, 21 is a CPU cliff), so it is pinned here rather than
/// left to whoever next edits the XAML.
/// </summary>
public class LiveGridLayoutTests
{
    [Fact]
    public void PageCapIsSixteen()
    {
        Assert.Equal(16, LiveGridLayout.MaxTilesPerPage);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(16, 1)]
    [InlineData(17, 2)]
    [InlineData(21, 2)]
    [InlineData(32, 2)]
    [InlineData(33, 3)]
    public void PageCount(int channels, int pages)
    {
        Assert.Equal(pages, LiveGridLayout.PageCount(channels));
    }

    [Fact]
    public void PagesSliceInOrderAndTheLastIsShort()
    {
        var channels = Enumerable.Range(1, 21).ToList();
        Assert.Equal(Enumerable.Range(1, 16), LiveGridLayout.Page(channels, 0));
        Assert.Equal(Enumerable.Range(17, 5), LiveGridLayout.Page(channels, 1));
    }

    [Fact]
    public void PageIndexIsClampedNotRejected()
    {
        // A page index left over from a 32-camera system, applied to an 8-camera one.
        var channels = Enumerable.Range(1, 8).ToList();
        Assert.Equal(channels, LiveGridLayout.Page(channels, 5));
        Assert.Equal(channels, LiveGridLayout.Page(channels, -1));
        Assert.Empty(LiveGridLayout.Page(new List<int>(), 0));
        Assert.Equal(0, LiveGridLayout.ClampPage(4, 8));
        Assert.Equal(1, LiveGridLayout.ClampPage(4, 21));
        Assert.Equal(0, LiveGridLayout.ClampPage(4, 0));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 3, 2)]
    [InlineData(9, 3, 3)]
    [InlineData(10, 4, 3)]
    [InlineData(16, 4, 4)]
    public void ColumnsAndRowsAreSquareish(int tiles, int columns, int rows)
    {
        Assert.Equal(columns, LiveGridLayout.Columns(tiles));
        Assert.Equal(rows, LiveGridLayout.Rows(tiles));
    }

    [Fact]
    public void DescribeNamesThePageAndTheCameraRange()
    {
        Assert.Equal("", LiveGridLayout.Describe(0, 0));
        Assert.Equal("8 camera(s)", LiveGridLayout.Describe(0, 8));
        Assert.Equal("Page 1 / 2 · cameras 1–16 of 21", LiveGridLayout.Describe(0, 21));
        Assert.Equal("Page 2 / 2 · cameras 17–21 of 21", LiveGridLayout.Describe(1, 21));
        Assert.Equal("Page 2 / 2 · cameras 17–21 of 21", LiveGridLayout.Describe(9, 21));
    }

    [Fact]
    public void StartsAreStaggeredButNotVisiblySlow()
    {
        // Sixteen keyframes at once were a 25–34 Mbps burst; a whole page must still be up
        // in well under two seconds.
        Assert.InRange(LiveGridLayout.StartStagger.TotalMilliseconds, 50, 120);
    }
}
