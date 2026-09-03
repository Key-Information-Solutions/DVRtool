using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// Grab-and-drag on a dewarped pane: the source pixel under the pointer must stay under the
/// pointer, on every mount and in every mode.
/// </summary>
public class DewarpDragTests
{
    private static readonly FisheyeCalibration Kia = FisheyeCalibration.Default(2560, 2560);
    private const int W = 800, H = 450;

    private static DewarpView Rect(double yaw, double pitch, double fov = 80) =>
        new(DewarpViewMode.Rectilinear, new ViewOrientation(yaw, pitch, 0), fov);

    private static (double X, double Y) SourceUnder(in FisheyeCalibration cal, in DewarpView view,
        double px, double py)
    {
        var source = FisheyeProjection.SourceFor(cal, view, W, H, px, py);
        Assert.NotNull(source);
        return source.Value;
    }

    [Theory]
    [InlineData(FisheyeMount.Ceiling, 30, 40)]
    [InlineData(FisheyeMount.Ceiling, 0, 0)]
    [InlineData(FisheyeMount.Ceiling, 200, 70)]
    [InlineData(FisheyeMount.Wall, 10, 30)]
    [InlineData(FisheyeMount.Wall, 350, 60)]
    [InlineData(FisheyeMount.Floor, 120, 45)]
    public void TheGrabbedSourcePixelFollowsThePointer(FisheyeMount mount, double yaw, double pitch)
    {
        var cal = Kia with { Mount = mount };
        var start = Rect(yaw, pitch);
        (double x0, double y0) = (300, 200);
        (double x1, double y1) = (470, 275);

        var grabbed = SourceUnder(cal, start, x0, y0);
        var dragged = DewarpDrag.Drag(cal, start, W, H, x0, y0, x1, y1);
        var nowUnder = SourceUnder(cal, dragged, x1, y1);

        Assert.Equal(grabbed.X, nowUnder.X, 1);
        Assert.Equal(grabbed.Y, nowUnder.Y, 1);
    }

    [Fact]
    public void ALongDragIsSolvedToo()
    {
        // Two thirds of the pane, zoomed in: the fixed point takes more of its rounds to settle
        // and must still land within a tenth of a source pixel.
        var start = Rect(45, 30, fov: 40);
        var grabbed = SourceUnder(Kia, start, 100, 80);
        var dragged = DewarpDrag.Drag(Kia, start, W, H, 100, 80, 650, 380);
        var nowUnder = SourceUnder(Kia, dragged, 650, 380);
        Assert.Equal(grabbed.X, nowUnder.X, 1);
        Assert.Equal(grabbed.Y, nowUnder.Y, 1);
    }

    [Fact]
    public void ADragThatGoesNowhereChangesNothing()
    {
        var start = Rect(30, 40);
        var dragged = DewarpDrag.Drag(Kia, start, W, H, 300, 200, 300, 200);
        Assert.Equal(start.ClampedTo(Kia).Orientation.YawDegrees, dragged.Orientation.YawDegrees, 9);
        Assert.Equal(start.ClampedTo(Kia).Orientation.PitchDegrees, dragged.Orientation.PitchDegrees, 9);
        Assert.Equal(start.HorizontalFovDegrees, dragged.HorizontalFovDegrees, 9);
    }

    [Fact]
    public void DraggingKeepsTheFieldOfViewAndTheRoll()
    {
        var start = Rect(30, 40, fov: 65) with
        {
            Orientation = new ViewOrientation(30, 40, 12),
        };
        var dragged = DewarpDrag.Drag(Kia, start, W, H, 300, 200, 500, 300);
        Assert.Equal(65, dragged.HorizontalFovDegrees, 9);
        Assert.Equal(12, dragged.Orientation.RollDegrees, 9);
        Assert.Equal(DewarpViewMode.Rectilinear, dragged.Mode);
    }

    [Fact]
    public void DraggingPastTheRimClampsInsteadOfFailing()
    {
        // Aimed near the rim and dragged further out: the view stops a hair inside the rim, as
        // ClampedTo dictates, and nothing throws or goes NaN.
        var start = Rect(0, 85);
        var dragged = DewarpDrag.Drag(Kia, start, W, H, 400, 225, 400, 440);
        Assert.True(double.IsFinite(dragged.Orientation.PitchDegrees));
        Assert.True(dragged.Orientation.PitchDegrees <= 90 * 0.98 + 1e-9);
    }

    [Theory]
    [InlineData(DewarpViewMode.Panorama180)]
    [InlineData(DewarpViewMode.Panorama360)]
    public void APanoramaDragShiftsTheYawByTheAzimuthDifference(DewarpViewMode mode)
    {
        var start = DewarpView.DefaultFor(FisheyeMount.Ceiling, mode) with
        {
            Orientation = new ViewOrientation(90, 0, 0),
        };
        var grabbed = SourceUnder(Kia, start, 200, 150);
        var dragged = DewarpDrag.Drag(Kia, start, W, H, 200, 150, 560, 150);
        var nowUnder = SourceUnder(Kia, dragged, 560, 150);
        Assert.Equal(grabbed.X, nowUnder.X, 1);
        Assert.Equal(grabbed.Y, nowUnder.Y, 1);
        // Rows are fixed by the mode: nothing about the tilt changes.
        Assert.Equal(0, dragged.Orientation.PitchDegrees, 9);
        Assert.Equal(mode, dragged.Mode);
    }

    [Fact]
    public void A360DragAcrossTheSeamTakesTheShortWay()
    {
        var start = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Panorama360) with
        {
            Orientation = new ViewOrientation(5, 0, 0),
        };
        // 20 pixels of an 800-wide 360 strip is 9°; the yaw must move by 9°, not by 351°.
        var dragged = DewarpDrag.Drag(Kia, start, W, H, 400, 100, 420, 100);
        double moved = Math.Abs(Math.IEEERemainder(dragged.Orientation.YawDegrees - 5, 360));
        Assert.Equal(9, moved, 6);
    }

    [Fact]
    public void WheelAwayZoomsInAndTowardsZoomsOut()
    {
        var view = Rect(0, 0, fov: 90);
        var zoomedIn = DewarpDrag.Wheel(Kia, view, 120);
        var zoomedOut = DewarpDrag.Wheel(Kia, view, -120);
        Assert.Equal(90 / DewarpDrag.WheelZoomStep, zoomedIn.HorizontalFovDegrees, 9);
        Assert.Equal(90 * DewarpDrag.WheelZoomStep, zoomedOut.HorizontalFovDegrees, 9);

        // Two notches in one message is two steps, and a zero delta is nothing.
        Assert.Equal(90 / (DewarpDrag.WheelZoomStep * DewarpDrag.WheelZoomStep),
            DewarpDrag.Wheel(Kia, view, 240).HorizontalFovDegrees, 9);
        Assert.Equal(90, DewarpDrag.Wheel(Kia, view, 0).HorizontalFovDegrees, 9);
    }

    [Fact]
    public void WheelIsANoOpOnAPanorama()
    {
        var view = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Panorama360);
        Assert.Equal(360, DewarpDrag.Wheel(Kia, view, 120).HorizontalFovDegrees, 9);
    }

    [Fact]
    public void NonFiniteInputIsIgnored()
    {
        var start = Rect(30, 40);
        var dragged = DewarpDrag.Drag(Kia, start, W, H, double.NaN, 200, 300, 200);
        Assert.Equal(start.ClampedTo(Kia), dragged);
        Assert.Equal(start.ClampedTo(Kia), DewarpDrag.Drag(Kia, start, 0, 0, 1, 1, 2, 2));
    }
}
