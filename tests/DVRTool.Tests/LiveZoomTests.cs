using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The live pane's digital zoom. Three things here are easy to get wrong in a way that only
/// shows up as a pane that feels broken: the anchor of a wheel zoom, the direction of a drag,
/// and a crop rectangle that hangs off the edge of the picture. All three are pinned.
/// </summary>
public class LiveZoomTests
{
    [Fact]
    public void NoneIsTheWholePictureAndCropsNothing()
    {
        Assert.False(LiveZoom.None.IsZoomed);
        Assert.Equal(1.0, LiveZoom.None.Factor);
        Assert.Equal(0.5, LiveZoom.None.CenterX);
        Assert.Equal(0.5, LiveZoom.None.CenterY);
        Assert.Null(LiveZoom.None.CropGeometry(1920, 1080));
        Assert.Equal("", LiveZoom.None.Describe());
    }

    [Fact]
    public void FactorIsClampedToTheRange()
    {
        Assert.Equal(LiveZoom.MaxFactor, LiveZoom.At(100, 0.5, 0.5).Factor);
        Assert.Equal(LiveZoom.MinFactor, LiveZoom.At(0.1, 0.5, 0.5).Factor);
        Assert.Equal(LiveZoom.MinFactor, LiveZoom.At(double.NaN, 0.5, 0.5).Factor);
    }

    [Fact]
    public void TheVisibleWindowNeverHangsOffThePicture()
    {
        // At 2× the window is half the picture, so its centre cannot pass 0.25 or 0.75.
        var zoom = LiveZoom.At(2, 0.0, 1.0);
        Assert.Equal(0.25, zoom.CenterX, 6);
        Assert.Equal(0.75, zoom.CenterY, 6);

        // And at 1× there is only one place to be.
        var whole = LiveZoom.At(1, 0.1, 0.9);
        Assert.Equal(0.5, whole.CenterX, 6);
        Assert.Equal(0.5, whole.CenterY, 6);
    }

    [Fact]
    public void AWheelNotchKeepsThePointUnderTheCursor()
    {
        // A quarter of the way across the pane, zoomed in three notches from the whole
        // picture: the point at 0.25/0.25 of the picture must still be a quarter of the way
        // across the pane afterwards.
        var zoom = LiveZoom.None.StepAt(3, 0.25, 0.25);
        Assert.True(zoom.IsZoomed);
        double span = zoom.VisibleFraction;
        Assert.Equal(0.25, zoom.CenterX + (0.25 - 0.5) * span, 6);
        Assert.Equal(0.25, zoom.CenterY + (0.25 - 0.5) * span, 6);
    }

    [Fact]
    public void AnchoringSurvivesRepeatedNotchesAtTheSamePoint()
    {
        // Six notches one at a time, cursor never moving: the anchored point stays put, which
        // is the property that makes scrolling into a doorway work.
        var zoom = LiveZoom.None;
        for (int i = 0; i < 6; i++)
            zoom = zoom.StepAt(1, 0.7, 0.35);
        double span = zoom.VisibleFraction;
        Assert.Equal(0.7, zoom.CenterX + (0.7 - 0.5) * span, 6);
        Assert.Equal(0.35, zoom.CenterY + (0.35 - 0.5) * span, 6);
    }

    [Fact]
    public void NotchesCompoundAndStopAtTheLimits()
    {
        Assert.Equal(LiveZoom.StepPerNotch, LiveZoom.None.StepAt(1, 0.5, 0.5).Factor, 6);
        Assert.Equal(LiveZoom.StepPerNotch * LiveZoom.StepPerNotch,
            LiveZoom.None.StepAt(1, 0.5, 0.5).StepAt(1, 0.5, 0.5).Factor, 6);
        Assert.Equal(LiveZoom.MaxFactor, LiveZoom.None.StepAt(40, 0.5, 0.5).Factor);

        // All the way back out is the whole picture again, centred, whatever it was anchored on.
        var out_ = LiveZoom.At(4, 0.8, 0.2).StepAt(-40, 0.9, 0.1);
        Assert.False(out_.IsZoomed);
        Assert.Equal(0.5, out_.CenterX, 6);
        Assert.Equal(0.5, out_.CenterY, 6);
    }

    [Fact]
    public void ADragMovesThePictureWithTheMouse()
    {
        // Dragging right by a quarter of the pane at 2× (half the picture showing) moves the
        // window an eighth of the picture to the left.
        var zoom = LiveZoom.At(2, 0.5, 0.5).PanBy(0.25, 0.0);
        Assert.Equal(0.5 - 0.125, zoom.CenterX, 6);
        Assert.Equal(0.5, zoom.CenterY, 6);

        // Down, likewise: the window goes up.
        var down = LiveZoom.At(2, 0.5, 0.5).PanBy(0.0, 0.25);
        Assert.Equal(0.5 - 0.125, down.CenterY, 6);
    }

    [Fact]
    public void ADragAtOneTimesDoesNothing()
    {
        Assert.Equal(LiveZoom.None, LiveZoom.None.PanBy(0.5, -0.5));
    }

    [Fact]
    public void ADragStopsAtTheEdge()
    {
        var zoom = LiveZoom.At(2, 0.5, 0.5).PanBy(-10, -10);
        Assert.Equal(0.75, zoom.CenterX, 6);
        Assert.Equal(0.75, zoom.CenterY, 6);
    }

    [Fact]
    public void CropGeometryIsFourBordersNotAWindow()
    {
        // left+top+right+bottom, because libvlc 3.0.21 mis-parses the WxH+X+Y form into
        // borders anyway (see LiveZoom.CropGeometry). Half the picture, centred, is a
        // quarter-picture border on each side.
        Assert.Equal("480+270+480+270", LiveZoom.At(2, 0.5, 0.5).CropGeometry(1920, 1080));

        // Hard into the top-left corner: no border there, and the rest on the far sides.
        Assert.Equal("0+0+960+540", LiveZoom.At(2, 0, 0).CropGeometry(1920, 1080));
        Assert.Equal("960+540+0+0", LiveZoom.At(2, 1, 1).CropGeometry(1920, 1080));
    }

    [Theory]
    [InlineData(1.0, 0.0)]     // hard against the right edge …
    [InlineData(0.0, 1.0)]     // … the left, the top, the bottom
    [InlineData(0.5, 0.5)]
    [InlineData(0.31, 0.77)]
    public void ACropWindowAlwaysFitsAndIsEvenlyAligned(double centerX, double centerY)
    {
        foreach (double factor in new[] { 1.3, 2.0, 3.7, 8.0 })
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 2560), (704, 480), (4256, 1888) })
        {
            var zoom = LiveZoom.At(factor, centerX, centerY);
            string geometry = zoom.CropGeometry(w, h)!;
            var (left, top, right, bottom) = Borders(geometry);
            int cw = w - left - right, ch = h - top - bottom;

            Assert.True(cw > 0 && ch > 0, $"{geometry} leaves nothing of {w}x{h}");
            Assert.True(left >= 0 && top >= 0 && right >= 0 && bottom >= 0, geometry);
            Assert.True(cw <= w && ch <= h, geometry);
            Assert.True(left % 2 == 0 && top % 2 == 0 && cw % 2 == 0 && ch % 2 == 0, geometry);
        }
    }

    [Fact]
    public void ACropWindowKeepsThePicturesShape()
    {
        // Same aspect in as out, so the pane's letterboxing does not shift as it zooms.
        var (left, top, right, bottom) = Borders(LiveZoom.At(3, 0.5, 0.5).CropGeometry(1920, 1080)!);
        double aspect = (1920.0 - left - right) / (1080.0 - top - bottom);
        Assert.Equal(1920.0 / 1080.0, aspect, 2);
    }

    /// <summary>The four border widths of a crop geometry.</summary>
    private static (int Left, int Top, int Right, int Bottom) Borders(string geometry)
    {
        var parts = geometry.Split('+');
        Assert.Equal(4, parts.Length);
        return (int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
    }

    [Fact]
    public void CropGeometryRefusesANonsensePictureSize()
    {
        var zoom = LiveZoom.At(4, 0.5, 0.5);
        Assert.Null(zoom.CropGeometry(0, 1080));
        Assert.Null(zoom.CropGeometry(1920, -1));
    }

    [Fact]
    public void FitKeepsThePicturesShapeInsideThePane()
    {
        // 16:9 in a square pane: full width, bars top and bottom.
        var (w, h) = LiveZoom.Fit(800, 800, 16.0 / 9.0);
        Assert.Equal(800, w, 6);
        Assert.Equal(450, h, 6);

        // 4:3 in a 16:9 pane: full height, bars left and right.
        var (w2, h2) = LiveZoom.Fit(1920, 1080, 4.0 / 3.0);
        Assert.Equal(1440, w2, 6);
        Assert.Equal(1080, h2, 6);

        // Nothing to fit against: the pane itself, so a caller dividing by it gets no NaN.
        Assert.Equal((100.0, 50.0), LiveZoom.Fit(100, 50, 0));
    }

    [Fact]
    public void PickUndoesTheLetterboxing()
    {
        // A 16:9 picture in a square pane: bars top and bottom, each an eighth of the pane.
        // The middle of the pane is the middle of the picture …
        var middle = LiveZoom.Pick(400, 400, 800, 800, 16.0 / 9.0);
        Assert.NotNull(middle);
        Assert.Equal(0.5, middle!.Value.U, 6);
        Assert.Equal(0.5, middle.Value.V, 6);

        // … the top-left corner of the image is where the bar ends, not the pane's corner …
        double barHeight = (800 - 800 * 9.0 / 16.0) / 2;
        var corner = LiveZoom.Pick(0, barHeight, 800, 800, 16.0 / 9.0);
        Assert.NotNull(corner);
        Assert.Equal(0.0, corner!.Value.U, 6);
        Assert.Equal(0.0, corner.Value.V, 6);

        // … and a point on the bar itself is not on the picture at all.
        Assert.Null(LiveZoom.Pick(400, 2, 800, 800, 16.0 / 9.0));
    }

    [Fact]
    public void PickHandlesBarsOnTheSides()
    {
        // A 4:3 picture in a 16:9 pane: bars left and right.
        var hit = LiveZoom.Pick(960, 540, 1920, 1080, 4.0 / 3.0);
        Assert.NotNull(hit);
        Assert.Equal(0.5, hit!.Value.U, 6);
        Assert.Equal(0.5, hit.Value.V, 6);
        Assert.Null(LiveZoom.Pick(10, 540, 1920, 1080, 4.0 / 3.0));
    }

    [Fact]
    public void PickRefusesAPaneOrPictureItCannotFit()
    {
        Assert.Null(LiveZoom.Pick(1, 1, 0, 100, 1.7));
        Assert.Null(LiveZoom.Pick(1, 1, 100, 0, 1.7));
        Assert.Null(LiveZoom.Pick(1, 1, 100, 100, 0));
        Assert.Null(LiveZoom.Pick(1, 1, 100, 100, double.NaN));
    }

    [Fact]
    public void DescribeNamesTheFactor()
    {
        Assert.Equal("2×", LiveZoom.At(2, 0.5, 0.5).Describe());
        Assert.Equal("3.8×", LiveZoom.At(3.81, 0.5, 0.5).Describe());
    }
}
