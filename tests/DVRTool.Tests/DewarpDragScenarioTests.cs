using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The gesture that exposed the degenerate case on screen on 2026-09-03: grabbing the centre of
/// a ceiling camera's default view and dragging diagonally. The nadir can only appear on the
/// pane's vertical centre line under the mount's roll rule, so the sideways part of the drag is
/// unreachable; the solve must satisfy the vertical part and leave the yaw alone rather than
/// fling the view.
/// </summary>
/// <remarks>
/// Pixel centres are at +0.5, so the exact centre of an 883×580 pane is (441.0, 289.5); a press
/// at (441, 290) grabs a source pixel half a pane pixel below the nadir, which is what the tests
/// measure — the grabbed pixel, never an assumed one.
/// </remarks>
public class DewarpDragScenarioTests
{
    private static readonly FisheyeCalibration Kia = FisheyeCalibration.Default(2560, 2560);
    private const int W = 883, H = 580;
    private const double Cx = 441.0, Cy = 289.5;

    private static readonly DewarpView Down =
        new(DewarpViewMode.Rectilinear, ViewOrientation.Center, 90);

    private static (double X, double Y) Grab(in DewarpView view, double px, double py)
    {
        var source = FisheyeProjection.SourceFor(Kia, view, W, H, px, py);
        Assert.NotNull(source);
        return source.Value;
    }

    private static (double X, double Y) Lands(in DewarpView view, (double X, double Y) source)
    {
        var at = FisheyeProjection.OutputFor(Kia, view, W, H, source.X, source.Y);
        Assert.NotNull(at);
        return at.Value;
    }

    [Fact]
    public void ADiagonalDragFromTheNadirTiltsToThePointersHeightAndKeepsTheYaw()
    {
        var grabbed = Grab(Down, Cx, Cy);
        Assert.Equal(1280, grabbed.X, 6);
        Assert.Equal(1280, grabbed.Y, 6);

        var dragged = DewarpDrag.Drag(Kia, Down, W, H, Cx, Cy, Cx + 250, Cy + 125);
        var now = Lands(dragged, grabbed);
        // The reachable half of the request: the nadir sits at the pointer's height ...
        Assert.Equal(Cy + 125, now.Y, 2);
        // ... and on the centre line, because nothing else is possible.
        Assert.Equal(Cx, now.X, 2);
        // 125 px below centre in a 580-high pane at 90° wide is a 15.8° tilt.
        Assert.InRange(dragged.Orientation.PitchDegrees, 14, 18);
        Assert.Equal(0, dragged.Orientation.YawDegrees, 6);
    }

    [Fact]
    public void ASidewaysDragFromTheNadirDoesNothingRatherThanSomethingWild()
    {
        var dragged = DewarpDrag.Drag(Kia, Down, W, H, Cx, Cy, Cx + 250, Cy);
        Assert.InRange(dragged.Orientation.PitchDegrees, 0, 0.01);
    }

    [Fact]
    public void AVerticalDragFromTheNadirIsExact()
    {
        var grabbed = Grab(Down, Cx, Cy);
        var dragged = DewarpDrag.Drag(Kia, Down, W, H, Cx, Cy, Cx, 500);
        var now = Lands(dragged, grabbed);
        Assert.Equal(500, now.Y, 2);
        Assert.Equal(Cx, now.X, 2);
        Assert.Equal(0, dragged.Orientation.YawDegrees, 6);
    }

    [Fact]
    public void AGrabHalfAPixelOffTheCentreIsSolvedForThePixelActuallyGrabbed()
    {
        // The press that misled the first version of these tests: (441, 290) is half a pane
        // pixel below the centre, so its source pixel is not the nadir and its rest position after
        // a vertical drag is not the nadir's either.
        var grabbed = Grab(Down, 441, 290);
        // Under the ceiling mount's quarter-turn roll, pane-down is source +X, so the half pixel
        // shows up in X rather than Y — either way, it is not the nadir.
        Assert.True(Math.Abs(grabbed.X - 1280) + Math.Abs(grabbed.Y - 1280) > 0.5,
            $"grabbed ({grabbed.X:0.00}, {grabbed.Y:0.00}) is the nadir");
        var dragged = DewarpDrag.Drag(Kia, Down, W, H, 441, 290, 441, 500);
        var now = Lands(dragged, grabbed);
        Assert.Equal(500, now.Y, 2);
    }

    [Fact]
    public void AGrabJustOffTheCentreDoesWhatItCanWithoutFlinging()
    {
        // 20 pixels off centre is 2.6° from the nadir, so the grabbed point is confined almost as
        // tightly to the centre line as the nadir itself. A drag far to the right cannot be
        // honoured; the solve must still put the point at about the pointer's height, tilt
        // moderately, and not run off to the rim.
        var grabbed = Grab(Down, Cx + 20, Cy + 10);
        var dragged = DewarpDrag.Drag(Kia, Down, W, H, Cx + 20, Cy + 10, Cx + 260, Cy + 130);
        var now = Lands(dragged, grabbed);
        Assert.InRange(now.Y, Cy + 120, Cy + 140);
        Assert.True(now.X > Cx, $"landed at x {now.X:0.0}, left of centre");
        Assert.InRange(dragged.Orientation.PitchDegrees, 10, 30);
    }

    [Fact]
    public void OnceTiltedASidewaysDragRotatesAroundTheAxis()
    {
        var start = Down with { Orientation = new ViewOrientation(0, 40, 0) };
        var grabbed = Grab(start, Cx, Cy);
        var dragged = DewarpDrag.Drag(Kia, start, W, H, Cx, Cy, Cx + 200, Cy);
        var now = Lands(dragged, grabbed);
        Assert.Equal(Cx + 200, now.X, 1);
        Assert.Equal(Cy, now.Y, 1);
        Assert.NotEqual(0, Math.Round(dragged.Orientation.YawDegrees));
    }
}
