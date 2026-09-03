using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The aimable view state: normalization, clamping against the lens, zoom behaviour and Quad's
/// composition out of the rectilinear primitive.
/// </summary>
public class FisheyeViewTests
{
    private static readonly FisheyeCalibration Kia = FisheyeCalibration.Default(2560, 2560);

    private static FisheyeCalibration Wide =>
        FisheyeCalibration.Default(2560, 2560) with { FieldOfViewDegrees = 195 };

    [Fact]
    public void CenterIsDownTheOpticalAxis()
    {
        Assert.Equal(0, ViewOrientation.Center.YawDegrees, 12);
        Assert.Equal(0, ViewOrientation.Center.PitchDegrees, 12);
        Assert.Equal(0, ViewOrientation.Center.RollDegrees, 12);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(370, 10, 10, 10)]
    [InlineData(-30, 20, 330, 20)]
    [InlineData(720, 5, 0, 5)]
    public void YawWrapsIntoOneTurn(double yaw, double pitch, double wrappedYaw, double keptPitch)
    {
        var normalized = new ViewOrientation(yaw, pitch, 0).Normalized();
        Assert.Equal(wrappedYaw, normalized.YawDegrees, 9);
        Assert.Equal(keptPitch, normalized.PitchDegrees, 9);
    }

    /// <summary>
    /// A drag that pulls the aim through the centre of the circle and out the other side arrives
    /// as a negative tilt. Folding it to "the same tilt, 180° around" is what keeps the gesture
    /// continuous instead of stopping dead at the axis.
    /// </summary>
    [Fact]
    public void NegativeTiltFoldsToTheOppositeAzimuth()
    {
        var folded = new ViewOrientation(0, -30, 0).Normalized();
        Assert.Equal(30, folded.PitchDegrees, 9);
        Assert.Equal(180, folded.YawDegrees, 9);

        var alsoFolded = new ViewOrientation(90, -15, 0).Normalized();
        Assert.Equal(15, alsoFolded.PitchDegrees, 9);
        Assert.Equal(270, alsoFolded.YawDegrees, 9);
    }

    [Fact]
    public void OrientationNormalizationIsIdempotent()
    {
        ViewOrientation[] wild =
        [
            new(-750, -95, 400),
            new(double.NaN, 30, 0),
            new(45, double.PositiveInfinity, 0),
            new(0, 0, -900),
        ];
        foreach (var orientation in wild)
        {
            var once = orientation.Normalized();
            Assert.Equal(once, once.Normalized());
            Assert.InRange(once.YawDegrees, 0, 360);
            Assert.True(once.PitchDegrees >= 0);
            Assert.InRange(once.RollDegrees, -180, 180);
        }
    }

    [Fact]
    public void DefaultsAimDownTheAxisExceptQuad()
    {
        foreach (var mount in Enum.GetValues<FisheyeMount>())
        {
            var rect = DewarpView.DefaultFor(mount, DewarpViewMode.Rectilinear);
            Assert.Equal(0, rect.Orientation.PitchDegrees, 12);
            Assert.Equal(0, rect.Orientation.YawDegrees, 12);
        }

        // Four panes all looking straight down the axis would be four copies of one picture.
        var quad = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Quad);
        Assert.Equal(45, quad.Orientation.PitchDegrees, 12);
    }

    [Fact]
    public void PanoramaFieldOfViewIsTheArcItUnrolls()
    {
        Assert.Equal(180,
            DewarpView.DefaultFor(FisheyeMount.Wall, DewarpViewMode.Panorama180)
                .HorizontalFovDegrees, 12);
        Assert.Equal(360,
            DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Panorama360)
                .HorizontalFovDegrees, 12);
    }

    [Fact]
    public void OnlyFlatModesZoom()
    {
        Assert.True(DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear)
            .SupportsZoom);
        Assert.True(DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Quad)
            .SupportsZoom);
        Assert.False(DewarpView.DefaultFor(FisheyeMount.Wall, DewarpViewMode.Panorama180)
            .SupportsZoom);
        Assert.False(DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Panorama360)
            .SupportsZoom);
    }

    /// <summary>
    /// A 180° lens sees 90° off its axis, so the aim stops just inside 90 — never past the rim,
    /// and never exactly on it (a view centred on the last ring of pixels is more than half
    /// outside the circle).
    /// </summary>
    [Fact]
    public void TiltStopsJustInsideTheRim()
    {
        var view = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear);

        var overAimed = view.Pan(0, 500, Kia);
        Assert.True(overAimed.Orientation.PitchDegrees < 90);
        Assert.Equal(90 * 0.98, overAimed.Orientation.PitchDegrees, 9);

        // A wider lens sees past the horizon, so it can be aimed further out.
        var wider = view.Pan(0, 500, Wide);
        Assert.Equal(97.5 * 0.98, wider.Orientation.PitchDegrees, 9);
        Assert.True(wider.Orientation.PitchDegrees > overAimed.Orientation.PitchDegrees);
    }

    [Fact]
    public void PanWrapsAzimuthFreelyAndClampsTilt()
    {
        var view = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear);

        var spun = view.Pan(400, 10, Kia);
        Assert.Equal(40, spun.Orientation.YawDegrees, 9);
        Assert.Equal(10, spun.Orientation.PitchDegrees, 9);

        // Dragged back through the centre: the tilt folds and the azimuth flips.
        var through = spun.Pan(0, -30, Kia);
        Assert.Equal(20, through.Orientation.PitchDegrees, 9);
        Assert.Equal(220, through.Orientation.YawDegrees, 9);
    }

    [Fact]
    public void AimedAtSetsTheDirectionAbsolutely()
    {
        var view = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear);
        var aimed = view.AimedAt(Math.PI / 6, Math.PI / 2, Kia);

        Assert.Equal(30, aimed.Orientation.PitchDegrees, 9);
        Assert.Equal(90, aimed.Orientation.YawDegrees, 9);
    }

    [Fact]
    public void ZoomIsMultiplicativeAndReversible()
    {
        var view = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear);
        Assert.Equal(90, view.HorizontalFovDegrees, 12);

        var inOnce = view.Zoom(0.5, Kia);
        Assert.Equal(45, inOnce.HorizontalFovDegrees, 9);

        // Reversible inside the clamps, which is what makes a wheel feel predictable.
        Assert.Equal(view.HorizontalFovDegrees, inOnce.Zoom(2, Kia).HorizontalFovDegrees, 9);
    }

    [Fact]
    public void ZoomIsBoundedAtBothEnds()
    {
        var view = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear);

        var wayIn = view;
        for (int i = 0; i < 40; i++)
            wayIn = wayIn.Zoom(0.7, Kia);
        Assert.Equal(DewarpView.MinHorizontalFovDegrees, wayIn.HorizontalFovDegrees, 9);

        var wayOut = view;
        for (int i = 0; i < 40; i++)
            wayOut = wayOut.Zoom(1.3, Kia);
        Assert.Equal(DewarpView.MaxHorizontalFovDegrees, wayOut.HorizontalFovDegrees, 9);
    }

    [Fact]
    public void ZoomDoesNothingToAPanorama()
    {
        var pano = DewarpView.DefaultFor(FisheyeMount.Wall, DewarpViewMode.Panorama180);
        Assert.Equal(180, pano.Zoom(0.25, Kia).HorizontalFovDegrees, 12);

        var full = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Panorama360);
        Assert.Equal(360, full.Zoom(4, Kia).HorizontalFovDegrees, 12);
    }

    [Fact]
    public void ClampRepairsNonsenseFieldsOfView()
    {
        var view = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear) with
        {
            HorizontalFovDegrees = double.NaN,
        };
        Assert.Equal(90, view.ClampedTo(Kia).HorizontalFovDegrees, 9);
    }

    [Fact]
    public void ANonQuadViewIsItsOwnOnlyPane()
    {
        var view = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear);
        var panes = view.Panes;

        Assert.Single(panes);
        Assert.Equal(view, panes[0].View);
        Assert.Equal(0, panes[0].Column);
        Assert.Equal(0, panes[0].Row);
    }

    /// <summary>
    /// Quad is a composition of the rectilinear primitive, so each pane must be exactly a
    /// standalone rectilinear view at that azimuth — same tilt, same width. This is what stops
    /// the composition drifting away from the primitive it is made of.
    /// </summary>
    [Fact]
    public void QuadIsFourRectilinearViewsNinetyDegreesApart()
    {
        var quad = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Quad)
            with { Orientation = new ViewOrientation(20, 45, 0) };

        var panes = quad.Panes;
        Assert.Equal(4, panes.Count);

        for (int i = 0; i < 4; i++)
        {
            var expected = new DewarpView(
                DewarpViewMode.Rectilinear,
                new ViewOrientation(20 + i * 90, 45, 0).Normalized(),
                quad.HorizontalFovDegrees);

            Assert.Equal(expected, panes[i].View);
            // All four panes sit at one tilt: the point of parameterizing by azimuth-about-axis.
            Assert.Equal(45, panes[i].View.Orientation.PitchDegrees, 9);
        }

        Assert.Equal([(0, 0), (1, 0), (0, 1), (1, 1)],
            panes.Select(p => (p.Column, p.Row)).ToArray());
    }

    [Fact]
    public void OnlyTheFullPanoramaNeedsTheWholeCircle()
    {
        Assert.True(DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Panorama360)
            .NeedsFullCircle);
        Assert.False(DewarpView.DefaultFor(FisheyeMount.Wall, DewarpViewMode.Panorama180)
            .NeedsFullCircle);
        Assert.False(DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear)
            .NeedsFullCircle);
    }
}
