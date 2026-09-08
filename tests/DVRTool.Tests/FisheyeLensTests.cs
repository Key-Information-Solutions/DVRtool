using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The lens radius/angle laws and the calibration derived from them.
/// </summary>
/// <remarks>
/// <para>
/// The four values of <c>r/f</c> at 90° — pi/2, 2, sqrt(2) and 1 — <i>are</i> the lens model. A
/// typo in any one formula moves exactly one of them, and their ordering catches two formulas
/// swapped, which a round-trip test on its own would not. So both are asserted explicitly.
/// </para>
/// <para>
/// The other load-bearing test here is <see cref="Equidistant180PutsTheHorizonExactlyOnTheRim"/>:
/// confusing the full field of view with half of it is the commonest bug in this arithmetic and
/// produces a picture that looks plausible but is wrong everywhere.
/// </para>
/// </remarks>
public class FisheyeLensTests
{
    private static readonly LensProjection[] AllProjections =
        Enum.GetValues<LensProjection>();

    [Fact]
    public void EveryProjectionPutsTheOpticalAxisAtTheCentre()
    {
        foreach (var projection in AllProjections)
            Assert.Equal(0, LensModel.RadiusOverFocal(projection, 0), 12);
    }

    [Fact]
    public void RadiusAndAngleRoundTrip()
    {
        foreach (var projection in AllProjections)
        {
            double max = LensModel.MaxTheta(projection);
            // Stereographic's radius runs to infinity as the angle approaches pi, so the last
            // degree is skipped rather than asserted against a number that overflows.
            double limit = projection == LensProjection.Stereographic ? max - 0.02 : max;
            for (double theta = 0; theta <= limit; theta += Math.PI / 180)
            {
                double r = LensModel.RadiusOverFocal(projection, theta);
                double back = LensModel.ThetaFromRadiusOverFocal(projection, r);
                Assert.Equal(theta, back, 9);
            }
        }
    }

    [Fact]
    public void RadiusGrowsStrictlyWithAngle()
    {
        foreach (var projection in AllProjections)
        {
            double max = LensModel.MaxTheta(projection);
            double previous = -1;
            for (double theta = 0; theta <= max; theta += max / 200)
            {
                double r = LensModel.RadiusOverFocal(projection, theta);
                Assert.True(r > previous,
                    $"{projection} is not monotonic at {theta} rad: {r} followed {previous}");
                previous = r;
            }
        }
    }

    [Theory]
    [InlineData(LensProjection.Equidistant, 1.5707963267948966)]   // pi/2
    [InlineData(LensProjection.Stereographic, 2.0)]                // 2*tan(45°)
    [InlineData(LensProjection.EquisolidAngle, 1.4142135623730951)] // 2*sin(45°) = sqrt(2)
    [InlineData(LensProjection.Orthographic, 1.0)]                 // sin(90°)
    public void KnownRadiusAtNinetyDegrees(LensProjection projection, double expected)
    {
        Assert.Equal(expected, LensModel.RadiusOverFocal(projection, Math.PI / 2), 12);
    }

    /// <summary>
    /// Catches two formulas transposed. A round trip stays self-consistent under a swap, so it
    /// would pass; the ordering of the four laws at a common angle would not.
    /// </summary>
    [Fact]
    public void TheFourLawsAreOrderedAtNinetyDegrees()
    {
        double ortho = LensModel.RadiusOverFocal(LensProjection.Orthographic, Math.PI / 2);
        double equisolid = LensModel.RadiusOverFocal(LensProjection.EquisolidAngle, Math.PI / 2);
        double equidistant = LensModel.RadiusOverFocal(LensProjection.Equidistant, Math.PI / 2);
        double stereographic = LensModel.RadiusOverFocal(LensProjection.Stereographic, Math.PI / 2);

        Assert.True(ortho < equisolid, $"{ortho} !< {equisolid}");
        Assert.True(equisolid < equidistant, $"{equisolid} !< {equidistant}");
        Assert.True(equidistant < stereographic, $"{equidistant} !< {stereographic}");
    }

    [Fact]
    public void OrthographicStopsAtNinetyDegreesAndTheOthersDoNot()
    {
        Assert.Equal(Math.PI / 2, LensModel.MaxTheta(LensProjection.Orthographic), 12);
        Assert.Equal(Math.PI, LensModel.MaxTheta(LensProjection.Equidistant), 12);
        Assert.Equal(Math.PI, LensModel.MaxTheta(LensProjection.EquisolidAngle), 12);
        Assert.Equal(Math.PI, LensModel.MaxTheta(LensProjection.Stereographic), 12);
    }

    [Theory]
    [InlineData(LensProjection.Orthographic, 1.5)]      // past sin's turnover
    [InlineData(LensProjection.EquisolidAngle, 2.5)]    // past 2*sin(pi/2)
    [InlineData(LensProjection.Equidistant, 4.0)]       // past pi
    [InlineData(LensProjection.Equidistant, -0.1)]      // behind the lens
    public void RadiiNoAngleReachesAreNotANumber(LensProjection projection, double rOverF)
    {
        Assert.True(double.IsNaN(LensModel.ThetaFromRadiusOverFocal(projection, rOverF)),
            $"{projection} answered {LensModel.ThetaFromRadiusOverFocal(projection, rOverF)} " +
            $"for r/f {rOverF}; outside the circle must be NaN, never a clamp.");
    }

    [Fact]
    public void AnglesOutsideTheLensAreNotANumber()
    {
        Assert.True(double.IsNaN(LensModel.RadiusOverFocal(LensProjection.Orthographic, 2.0)));
        Assert.True(double.IsNaN(LensModel.RadiusOverFocal(LensProjection.Equidistant, -0.1)));
        Assert.True(double.IsNaN(LensModel.RadiusOverFocal(LensProjection.Equidistant, 4.0)));
    }

    // ---- Calibration -------------------------------------------------------------------

    /// <summary>
    /// The test that catches full-field-of-view versus half-field-of-view confusion. For a 180°
    /// equidistant lens the focal length must be exactly <c>R / (pi/2)</c>, and the horizon —
    /// 90° off the axis — must land exactly on the rim of the circle, not at 63 % of it (which
    /// is what using the full 180° as the half-angle gives).
    /// </summary>
    [Fact]
    public void Equidistant180PutsTheHorizonExactlyOnTheRim()
    {
        var cal = FisheyeCalibration.Default(2560, 2560);

        Assert.Equal(1280, cal.RadiusX, 9);
        Assert.Equal(1280 / (Math.PI / 2), cal.FocalPixels, 9);

        double rimRadius = cal.FocalPixels *
            LensModel.RadiusOverFocal(LensProjection.Equidistant, Math.PI / 2);
        Assert.Equal(cal.RadiusX, rimRadius, 9);
    }

    /// <summary>
    /// A lens wider than 180° sees past the horizon, so 90° must land strictly inside the rim —
    /// at exactly the fraction of the radius the angles are in, for an equidistant lens.
    /// </summary>
    [Fact]
    public void AWiderLensPutsTheHorizonInsideTheRim()
    {
        var cal = FisheyeCalibration.Default(2560, 2560) with { FieldOfViewDegrees = 195 };

        double horizon = cal.FocalPixels *
            LensModel.RadiusOverFocal(LensProjection.Equidistant, Math.PI / 2);

        Assert.True(horizon < cal.RadiusX);
        Assert.Equal(cal.RadiusX * (90.0 / 97.5), horizon, 9);
    }

    [Fact]
    public void FocalLengthScalesWithTheCircleAndIgnoresWhereItSits()
    {
        var small = FisheyeCalibration.Default(2560, 2560) with { RadiusX = 500 };
        var large = small with { RadiusX = 1000 };
        var moved = small with { CenterX = 17, CenterY = 2000 };

        Assert.Equal(2 * small.FocalPixels, large.FocalPixels, 9);
        Assert.Equal(small.FocalPixels, moved.FocalPixels, 12);
    }

    [Fact]
    public void DefaultsInscribeTheCircleInTheShorterSide()
    {
        // Site C: square.
        var siteC = FisheyeCalibration.Default(2560, 2560);
        Assert.Equal(1280, siteC.CenterX, 9);
        Assert.Equal(1280, siteC.CenterY, 9);
        Assert.Equal(1280, siteC.RadiusX, 9);

        // Site H: 4:3, and iVMS will not dewarp it at all. The circle is inscribed in the
        // height and cropped left and right, which is an ordinary calibration here.
        var siteH = FisheyeCalibration.Default(2592, 1944);
        Assert.Equal(1296, siteH.CenterX, 9);
        Assert.Equal(972, siteH.CenterY, 9);
        Assert.Equal(972, siteH.RadiusX, 9);
        Assert.Null(siteH.Validate());
    }

    [Fact]
    public void RoundCircleHasEqualRadii()
    {
        var cal = FisheyeCalibration.Default(2560, 2560);
        Assert.Equal(cal.RadiusX, cal.RadiusY, 12);

        var squashed = cal with { Ellipticity = 0.5 };
        Assert.Equal(cal.RadiusX * 0.5, squashed.RadiusY, 12);
    }

    [Fact]
    public void NormalizedIsIdempotent()
    {
        FisheyeCalibration[] wild =
        [
            FisheyeCalibration.Default(2560, 2560) with { RollDegrees = 900 },
            FisheyeCalibration.Default(2592, 1944) with { Ellipticity = -3 },
            FisheyeCalibration.Default(720, 720) with { FieldOfViewDegrees = 5000 },
            FisheyeCalibration.Default(720, 720) with { RadiusX = double.NaN },
            FisheyeCalibration.Default(1, 1) with { CenterX = double.PositiveInfinity },
            new FisheyeCalibration(LensProjection.Orthographic, FisheyeMount.Wall,
                0, 0, 0, 0, 0, 0, 0, 0),
        ];

        foreach (var cal in wild)
        {
            var once = cal.Normalized();
            var twice = once.Normalized();
            Assert.Equal(once, twice);
            Assert.Null(once.Validate());
        }
    }

    [Fact]
    public void ValidateRejectsWhatCannotBeDewarped()
    {
        var ok = FisheyeCalibration.Default(2560, 2560);
        Assert.Null(ok.Validate());

        Assert.NotNull((ok with { RadiusX = 0 }).Validate());
        Assert.NotNull((ok with { Ellipticity = 0 }).Validate());
        Assert.NotNull((ok with { RollDegrees = 31 }).Validate());
        Assert.NotNull((ok with { RollDegrees = -31 }).Validate());
        Assert.NotNull((ok with { FieldOfViewDegrees = 0 }).Validate());
        Assert.NotNull((ok with { FieldOfViewDegrees = 361 }).Validate());
        Assert.NotNull((ok with { SourceWidth = 0 }).Validate());
        // The circle pushed entirely off the left edge.
        Assert.NotNull((ok with { CenterX = -1281 }).Validate());
        // ...and entirely off the right.
        Assert.NotNull((ok with { CenterX = 2560 + 1281 }).Validate());
    }

    [Fact]
    public void OrthographicCannotBeWiderThanOneHundredEighty()
    {
        var wide = FisheyeCalibration.Default(2560, 2560) with
        {
            Projection = LensProjection.Orthographic,
            FieldOfViewDegrees = 220,
        };

        Assert.NotNull(wide.Validate());
        // Normalizing narrows it rather than leaving a value Validate refuses.
        Assert.Equal(180, wide.Normalized().FieldOfViewDegrees, 9);
        Assert.Null(wide.Normalized().Validate());
    }

    /// <summary>
    /// One camera, two encodings. Site C's fisheyes are 2560×2560 on the main stream and
    /// 720×720 on the sub, so a circle calibrated on one describes the other only after being
    /// rescaled.
    /// </summary>
    [Fact]
    public void CalibrationRescalesBetweenMainAndSubStream()
    {
        var main = FisheyeCalibration.Default(2560, 2560);
        var sub = main.ScaledTo(720, 720);

        Assert.Equal(360, sub.CenterX, 9);
        Assert.Equal(360, sub.CenterY, 9);
        Assert.Equal(360, sub.RadiusX, 9);
        Assert.Equal(1.0, sub.Ellipticity, 12);
        Assert.Equal(720, sub.SourceWidth);
        // Angles and the installation itself are unchanged by the encoding.
        Assert.Equal(main.FieldOfViewDegrees, sub.FieldOfViewDegrees, 12);
        Assert.Equal(main.Mount, sub.Mount);
        Assert.Equal(main.Projection, sub.Projection);

        // And back again.
        Assert.Equal(main, sub.ScaledTo(2560, 2560));
    }

    [Fact]
    public void RescalingToADifferentAspectRatioBecomesElliptical()
    {
        var square = FisheyeCalibration.Default(2560, 2560);
        // A square circle re-expressed in a frame squashed to half height is an ellipse.
        var squashed = square.ScaledTo(2560, 1280);

        Assert.Equal(1280, squashed.RadiusX, 9);
        Assert.Equal(0.5, squashed.Ellipticity, 12);
        Assert.Equal(640, squashed.RadiusY, 9);
    }

    [Fact]
    public void RescalingToTheSameSizeOrAnUnknownSizeChangesNothing()
    {
        var cal = FisheyeCalibration.Default(2560, 2560);
        Assert.Equal(cal, cal.ScaledTo(2560, 2560));
        Assert.Equal(cal, cal.ScaledTo(0, 0));
        Assert.Equal(cal, cal.ScaledTo(-5, 100));
    }
}
