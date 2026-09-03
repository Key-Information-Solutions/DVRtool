using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The dewarp geometry. These are the tests that catch a sign, an axis convention or a
/// half-pixel wrong — the class of bug that produces a plausible-looking picture that is wrong
/// everywhere, and which live video hides because nobody knows what the corrected view should
/// look like.
/// </summary>
public class FisheyeProjectionTests
{
    private static readonly FisheyeCalibration Kia = FisheyeCalibration.Default(2560, 2560);
    private static readonly FisheyeCalibration CasaMaya = FisheyeCalibration.Default(2592, 1944);

    private static DewarpGeometry Geometry(
        FisheyeCalibration? calibration = null,
        DewarpView? view = null,
        int width = 640,
        int height = 480) =>
        FisheyeProjection.For(
            calibration ?? Kia,
            view ?? DewarpView.DefaultFor((calibration ?? Kia).Mount, DewarpViewMode.Rectilinear),
            width, height);

    /// <summary>
    /// A view aimed down the optical axis puts the centre of the pane on the centre of the
    /// circle. Most sign errors and most half-pixel errors break this one test.
    /// </summary>
    [Fact]
    public void ViewDownTheAxisMapsPaneCentreToCircleCentre()
    {
        // Even output dimensions so the pane's centre falls on a pixel boundary at W/2 - 0.5.
        var geometry = Geometry(width: 640, height: 480);
        var source = geometry.SourceFor(319.5, 239.5);

        Assert.NotNull(source);
        Assert.Equal(Kia.CenterX, source!.Value.X, 9);
        Assert.Equal(Kia.CenterY, source.Value.Y, 9);
    }

    [Fact]
    public void ViewDownTheAxisMapsPaneCentreToCircleCentreOnEveryMount()
    {
        foreach (var mount in Enum.GetValues<FisheyeMount>())
        {
            var cal = Kia with { Mount = mount };
            var geometry = Geometry(cal, DewarpView.DefaultFor(mount, DewarpViewMode.Rectilinear));
            var source = geometry.SourceFor(319.5, 239.5);

            Assert.NotNull(source);
            Assert.Equal(cal.CenterX, source!.Value.X, 9);
            Assert.Equal(cal.CenterY, source.Value.Y, 9);
        }
    }

    /// <summary>
    /// Pins the +0.5 pixel-centre convention. A future GPU path samples texel centres the same
    /// way; a half-pixel disagreement between the two ships as "the accelerated view looks
    /// slightly soft" and is miserable to track down after the fact.
    /// </summary>
    /// <remarks>
    /// The coordinates here are 0-based pixel <i>indices</i> and the sample sits at the pixel's
    /// centre, half a pixel further on — so a pane of width W is centred on index
    /// <c>(W-1)/2</c>, which is 319.5 for 640 and 0.5 for 2. Passing 0.5 and 1.5 to a 2-wide
    /// pane would be the centre and a corner, not a straddling pair.
    /// </remarks>
    [Fact]
    public void PixelCentresAreOffsetByHalfAPixel()
    {
        // The smallest pane that can straddle its own centre: indices 0 and 1 about index 0.5.
        var tiny = Geometry(width: 2, height: 2);
        var a = tiny.SourceFor(0, 0);
        var b = tiny.SourceFor(1, 1);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(Kia.CenterX, (a!.Value.X + b!.Value.X) / 2, 9);
        Assert.Equal(Kia.CenterY, (a.Value.Y + b.Value.Y) / 2, 9);
        // ...and genuinely straddle it, rather than both landing on it.
        Assert.NotEqual(a.Value.X, b.Value.X, 3);

        // The same convention at a real pane size: the two middle pixels sit symmetrically
        // either side of the circle centre, and the half-index between them lands on it.
        var pane = Geometry(width: 640, height: 480);
        var left = pane.SourceFor(319, 239.5)!.Value;
        var right = pane.SourceFor(320, 239.5)!.Value;
        Assert.Equal(Kia.CenterX, (left.X + right.X) / 2, 9);
        Assert.Equal(Kia.CenterX, pane.SourceFor(319.5, 239.5)!.Value.X, 9);
    }

    [Fact]
    public void SourceAndOutputRoundTripAcrossThePane()
    {
        var view = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear)
            with { Orientation = new ViewOrientation(37, 25, 0) };
        var geometry = Geometry(view: view);

        int checked_ = 0;
        for (double py = 10.5; py < 470; py += 47)
        {
            for (double px = 10.5; px < 630; px += 61)
            {
                var source = geometry.SourceFor(px, py);
                if (source is null)
                    continue;
                var back = geometry.OutputFor(source.Value.X, source.Value.Y);
                Assert.NotNull(back);
                Assert.Equal(px, back!.Value.X, 6);
                Assert.Equal(py, back.Value.Y, 6);
                checked_++;
            }
        }
        Assert.True(checked_ > 50, $"only {checked_} pixels round-tripped; the test is not covering");
    }

    [Fact]
    public void RoundTripHoldsForEveryProjectionAndMount()
    {
        foreach (var projection in Enum.GetValues<LensProjection>())
        {
            foreach (var mount in Enum.GetValues<FisheyeMount>())
            {
                var cal = Kia with { Projection = projection, Mount = mount };
                var view = DewarpView.DefaultFor(mount, DewarpViewMode.Rectilinear)
                    with { Orientation = new ViewOrientation(20, 15, 0) };
                var geometry = Geometry(cal, view);

                var source = geometry.SourceFor(200.5, 150.5);
                Assert.NotNull(source);
                var back = geometry.OutputFor(source!.Value.X, source.Value.Y);
                Assert.NotNull(back);
                Assert.Equal(200.5, back!.Value.X, 6);
                Assert.Equal(150.5, back.Value.Y, 6);
            }
        }
    }

    /// <summary>
    /// Turning the view about the axis and rolling the calibration about the axis are the same
    /// rotation of the sampled points. Catches a roll applied in the wrong space or with the
    /// wrong sign — which a round-trip test cannot see, because it stays self-consistent.
    /// </summary>
    [Fact]
    public void ViewYawAndCalibrationRollAreTheSameRotation()
    {
        const double delta = 17;
        var baseView = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear)
            with { Orientation = new ViewOrientation(0, 30, 0) };

        // Wall's mount roll cancels yaw by design, so this identity is asserted on a ceiling
        // mount, where the roll about the axis is a constant.
        var yawed = Geometry(Kia, baseView with
        {
            Orientation = baseView.Orientation with { YawDegrees = delta },
        });
        var rolled = Geometry(Kia with { RollDegrees = delta }, baseView);

        var fromYaw = yawed.SourceFor(200.5, 150.5);
        var fromRoll = rolled.SourceFor(200.5, 150.5);
        Assert.NotNull(fromYaw);
        Assert.NotNull(fromRoll);

        Assert.Equal(fromRoll!.Value.X, fromYaw!.Value.X, 6);
        Assert.Equal(fromRoll.Value.Y, fromYaw.Value.Y, 6);
    }

    [Fact]
    public void EllipticityIsAPureVerticalScale()
    {
        var view = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear)
            with { Orientation = new ViewOrientation(0, 30, 0) };
        var round = Geometry(Kia, view);
        var squashed = Geometry(Kia with { Ellipticity = 2 }, view);

        for (double px = 40.5; px < 600; px += 97)
        {
            var a = round.SourceFor(px, 200.5);
            var b = squashed.SourceFor(px, 200.5);
            Assert.NotNull(a);
            Assert.NotNull(b);

            // X untouched; every Y offset from the centre doubled.
            Assert.Equal(a!.Value.X, b!.Value.X, 6);
            Assert.Equal((a.Value.Y - Kia.CenterY) * 2, b.Value.Y - Kia.CenterY, 6);
        }
    }

    /// <summary>
    /// A view opened wider than the lens can see must report the corners as outside the circle,
    /// not clamp them onto the rim. Clamping is what smears the edge or wraps it to the far side.
    /// </summary>
    [Fact]
    public void RaysPastTheRimAreNullNotClamped()
    {
        // Aimed at the rim of a 180° lens and opened wide: the centre is inside, corners are not.
        var view = new DewarpView(DewarpViewMode.Rectilinear,
            new ViewOrientation(0, 88, 0), 140);
        var geometry = Geometry(Kia, view);

        Assert.NotNull(geometry.SourceFor(319.5, 239.5));

        int outside = 0;
        (double, double)[] corners = [(0.5, 0.5), (639.5, 0.5), (0.5, 479.5), (639.5, 479.5)];
        foreach (var (px, py) in corners)
        {
            if (geometry.SourceFor(px, py) is null)
                outside++;
        }
        Assert.True(outside > 0, "a view aimed at the rim and opened to 140° must fall off the circle");
    }

    [Fact]
    public void EveryReturnedSourcePointLiesInsideTheImageCircle()
    {
        var view = new DewarpView(DewarpViewMode.Rectilinear,
            new ViewOrientation(0, 80, 0), 140);
        var geometry = Geometry(Kia, view);

        for (double py = 0.5; py < 480; py += 13)
        {
            for (double px = 0.5; px < 640; px += 13)
            {
                var source = geometry.SourceFor(px, py);
                if (source is null)
                    continue;
                double dx = source.Value.X - Kia.CenterX;
                double dy = (source.Value.Y - Kia.CenterY) / Kia.Ellipticity;
                double r = Math.Sqrt(dx * dx + dy * dy);
                Assert.True(r <= Kia.RadiusX + 1e-6,
                    $"({px},{py}) sampled radius {r}, outside the {Kia.RadiusX} circle");
            }
        }
    }

    /// <summary>
    /// A ceiling camera's rim is the horizon, so 90° off the axis must land exactly on the
    /// circle's edge for a 180° equidistant lens. This ties the geometry to the lens model's own
    /// FOV/half-FOV identity.
    /// </summary>
    [Fact]
    public void TheHorizonLandsOnTheRim()
    {
        var geometry = Geometry();
        // Straight out to the side in the lens frame: incidence 90°, azimuth 0.
        var source = geometry.SourceForRay(1, 0, 0);

        Assert.NotNull(source);
        Assert.Equal(Kia.CenterX + Kia.RadiusX, source!.Value.X, 6);
        Assert.Equal(Kia.CenterY, source.Value.Y, 6);
    }

    [Fact]
    public void MountRollsAreTheDocumentedQuarterTurns()
    {
        Assert.Equal(Math.PI / 2, FisheyeProjection.MountRollRad(FisheyeMount.Ceiling, 0), 12);
        Assert.Equal(-Math.PI / 2, FisheyeProjection.MountRollRad(FisheyeMount.Floor, 0), 12);
        // A wall mount cancels the yaw so the horizon stays level as the view sweeps.
        Assert.Equal(-1.234, FisheyeProjection.MountRollRad(FisheyeMount.Wall, 1.234), 12);

        Assert.True(FisheyeProjection.IsMirrored(FisheyeMount.Floor));
        Assert.False(FisheyeProjection.IsMirrored(FisheyeMount.Ceiling));
        Assert.False(FisheyeProjection.IsMirrored(FisheyeMount.Wall));
    }

    /// <summary>
    /// A floor-mounted camera is physically flipped over, so its source image is already a
    /// mirror of what a ceiling camera in the same place would produce — and dewarping it with
    /// the ceiling's arithmetic would hand the room back mirrored. The flip corrects that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted as a handedness, which is convention-independent and therefore cannot be made
    /// to pass by choosing a different sign somewhere: walk one pixel right and one pixel down
    /// the pane, and take the cross product of the two steps in source space. The mount roll is
    /// a <i>rotation</i>, so it cannot change that sign; only a mirror can. Ceiling and wall
    /// must therefore agree, and floor must be the opposite.
    /// </para>
    /// <para>
    /// Without the flip all three would agree, and a floor camera would ship mirror-imaged —
    /// text unreadable and a drag to the right moving the view left. That is invisible until
    /// someone actually has one mounted that way.
    /// </para>
    /// </remarks>
    [Fact]
    public void FloorUndoesTheMirrorThatCeilingAndWallDoNotNeed()
    {
        double Handedness(FisheyeMount mount)
        {
            var geometry = Geometry(Kia with { Mount = mount },
                new DewarpView(DewarpViewMode.Rectilinear, new ViewOrientation(0, 30, 0), 90));
            var origin = geometry.SourceFor(300, 240)!.Value;
            var right = geometry.SourceFor(301, 240)!.Value;
            var down = geometry.SourceFor(300, 241)!.Value;
            double ax = right.X - origin.X, ay = right.Y - origin.Y;
            double bx = down.X - origin.X, by = down.Y - origin.Y;
            return ax * by - ay * bx;
        }

        double ceiling = Handedness(FisheyeMount.Ceiling);
        double wall = Handedness(FisheyeMount.Wall);
        double floor = Handedness(FisheyeMount.Floor);

        Assert.NotEqual(0, ceiling, 6);
        Assert.True(Math.Sign(ceiling) == Math.Sign(wall),
            $"ceiling ({ceiling}) and wall ({wall}) must share a handedness — the mount roll is " +
            "a rotation and cannot flip one");
        Assert.True(Math.Sign(floor) != Math.Sign(ceiling),
            $"floor ({floor}) must be the opposite handedness to ceiling ({ceiling}); without " +
            "that flip a floor camera dewarps mirror-imaged");
        // A reflection rather than a rescale, so the magnitudes agree closely — but not exactly:
        // the mirror maps this pane index onto a different part of the scene, and the projection
        // is nonlinear, so the local area there differs slightly. Measured at 0.06 %.
        Assert.Equal(1.0, Math.Abs(floor) / Math.Abs(ceiling), 2);
    }

    [Fact]
    public void LookAtRecoversTheDirectionTheViewIsAimedAt()
    {
        var view = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear)
            with { Orientation = new ViewOrientation(40, 25, 0) };
        var geometry = Geometry(view: view);

        var (theta, phi) = geometry.LookAt(319.5, 239.5);

        Assert.Equal(25 * Math.PI / 180, theta, 9);
        Assert.Equal(40 * Math.PI / 180, phi, 9);
    }

    /// <summary>
    /// The drag gesture, end to end: read the direction under the pointer, aim the view there,
    /// and that direction is now under the centre of the pane. This is what "grab the picture
    /// and move it" reduces to.
    /// </summary>
    [Fact]
    public void AimingAtTheDirectionUnderAPixelCentresThatPixel()
    {
        var view = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear);
        var geometry = Geometry(view: view);

        var (theta, phi) = geometry.LookAt(500.5, 120.5);
        var aimed = view.AimedAt(theta, phi, Kia);
        var moved = Geometry(view: aimed);

        var (centreTheta, centrePhi) = moved.LookAt(319.5, 239.5);
        Assert.Equal(theta, centreTheta, 6);
        Assert.Equal(phi, centrePhi, 6);
    }

    [Fact]
    public void ZoomingInNarrowsTheSourceRegionSampled()
    {
        var wide = Geometry(view: new DewarpView(DewarpViewMode.Rectilinear,
            new ViewOrientation(0, 20, 0), 120));
        var narrow = Geometry(view: new DewarpView(DewarpViewMode.Rectilinear,
            new ViewOrientation(0, 20, 0), 20));

        double Span(DewarpGeometry g)
        {
            var left = g.SourceFor(0.5, 239.5)!.Value;
            var right = g.SourceFor(639.5, 239.5)!.Value;
            return Math.Sqrt(Math.Pow(right.X - left.X, 2) + Math.Pow(right.Y - left.Y, 2));
        }

        // The whole point of zooming a 2560×2560 source: fewer source pixels fill the same pane,
        // so detail resolves rather than being upscaled.
        Assert.True(Span(narrow) < Span(wide) / 3,
            $"narrow span {Span(narrow)} is not meaningfully smaller than wide {Span(wide)}");
    }

    // ---- The point of the whole exercise ------------------------------------------------

    /// <summary>
    /// A straight line in the room comes out straight in a rectilinear view. This is what
    /// "dewarp" means, and it is the one property no amount of self-consistent arithmetic can
    /// fake: the round-trip tests would all still pass with a wrong perspective projection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set up as a ceiling camera one unit above a floor. A line drawn on that floor is a set of
    /// collinear 3D points; each is turned into a ray, projected through the lens onto the
    /// source image (where it is emphatically <i>not</i> straight — that is the fisheye
    /// distortion), and then dewarped back into the pane. The pane points must be collinear
    /// again, to well under a pixel.
    /// </para>
    /// <para>
    /// The lens curvature is asserted too, so the test cannot pass by accident on a projection
    /// that never bent anything in the first place.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(LensProjection.Equidistant)]
    [InlineData(LensProjection.Stereographic)]
    [InlineData(LensProjection.EquisolidAngle)]
    [InlineData(LensProjection.Orthographic)]
    public void StraightLinesInTheRoomComeOutStraight(LensProjection projection)
    {
        var cal = Kia with { Projection = projection };
        // Aimed off-axis so the line lands where the lens bends hardest, not through the middle.
        var view = new DewarpView(DewarpViewMode.Rectilinear,
            new ViewOrientation(0, 35, 0), 70);
        var geometry = Geometry(cal, view, 800, 600);

        // A line on the floor, one unit below a ceiling camera: the floor is the plane z = 1 in
        // the lens frame, and this line runs across it well off to one side.
        var sourcePoints = new List<(double X, double Y)>();
        var panePoints = new List<(double X, double Y)>();
        for (double s = -0.45; s <= 0.45; s += 0.05)
        {
            // Held at a constant y so it is a straight line, swept in x.
            var ray = (X: s, Y: 0.62, Z: 1.0);
            var source = geometry.SourceForRay(ray.X, ray.Y, ray.Z);
            Assert.NotNull(source);
            var pane = geometry.OutputFor(source!.Value.X, source.Value.Y);
            Assert.NotNull(pane);
            sourcePoints.Add(source.Value);
            panePoints.Add(pane!.Value);
        }
        Assert.True(panePoints.Count >= 15);

        // The fisheye really did bend it: the line is measurably curved in the source image.
        double sourceBow = MaxDeviationFromLine(sourcePoints);
        Assert.True(sourceBow > 5,
            $"{projection}: the source line bowed only {sourceBow:F2} px, so this test is not " +
            "exercising any distortion");

        // ...and the dewarp straightened it.
        double paneBow = MaxDeviationFromLine(panePoints);
        Assert.True(paneBow < 0.02,
            $"{projection}: dewarped line still bows {paneBow:F4} px (source bowed " +
            $"{sourceBow:F1} px)");
    }

    /// <summary>
    /// Largest perpendicular distance from any point to the best-fit line through the first and
    /// last of them. Zero when the points are collinear.
    /// </summary>
    private static double MaxDeviationFromLine(List<(double X, double Y)> points)
    {
        var (x0, y0) = points[0];
        var (x1, y1) = points[^1];
        double dx = x1 - x0, dy = y1 - y0;
        double length = Math.Sqrt(dx * dx + dy * dy);
        Assert.True(length > 1, "the line is too short to measure straightness against");
        double worst = 0;
        foreach (var (x, y) in points)
        {
            // Perpendicular distance via the 2D cross product.
            double distance = Math.Abs((x - x0) * dy - (y - y0) * dx) / length;
            worst = Math.Max(worst, distance);
        }
        return worst;
    }

    // ---- Site H: the 4:3 frame iVMS will not dewarp at all ---------------------------

    [Fact]
    public void ANonSquareFrameIsAnOrdinaryCalibration()
    {
        var geometry = Geometry(CasaMaya, width: 640, height: 480);
        var centre = geometry.SourceFor(319.5, 239.5);

        Assert.NotNull(centre);
        Assert.Equal(1296, centre!.Value.X, 9);
        Assert.Equal(972, centre.Value.Y, 9);

        // The horizon still lands on the rim, inscribed in the height and cropped left and right.
        var horizon = geometry.SourceForRay(0, 1, 0);
        Assert.NotNull(horizon);
        Assert.Equal(972 + 972, horizon!.Value.Y, 6);
    }

    [Fact]
    public void CasaMayaRoundTripsAndStaysInsideItsCircle()
    {
        var view = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear)
            with { Orientation = new ViewOrientation(115, 40, 0) };
        var geometry = Geometry(CasaMaya, view);

        for (double py = 20.5; py < 470; py += 53)
        {
            for (double px = 20.5; px < 630; px += 67)
            {
                var source = geometry.SourceFor(px, py);
                if (source is null)
                    continue;
                Assert.InRange(source.Value.X, 1296 - 972 - 1e-6, 1296 + 972 + 1e-6);
                Assert.InRange(source.Value.Y, 0 - 1e-6, 1944 + 1e-6);

                var back = geometry.OutputFor(source.Value.X, source.Value.Y);
                Assert.NotNull(back);
                Assert.Equal(px, back!.Value.X, 6);
                Assert.Equal(py, back.Value.Y, 6);
            }
        }
    }

    // ---- Panoramas ---------------------------------------------------------------------

    [Fact]
    public void FullPanoramaSweepsExactlyOneTurnAndMonotonically()
    {
        var geometry = Geometry(
            view: DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Panorama360),
            width: 720, height: 240);

        double previous = double.NegativeInfinity;
        double first = 0, last = 0;
        for (int px = 0; px < 720; px++)
        {
            // Halfway down the strip, where the incidence angle is well clear of both ends.
            var (_, phi) = geometry.LookAt(px, 120);
            double unwrapped = phi < previous - Math.PI ? phi + 2 * Math.PI : phi;
            if (px == 0) first = phi;
            if (px == 719) last = unwrapped;
            previous = phi;
        }

        // 720 columns, 360°: one turn less one column's worth.
        Assert.Equal(2 * Math.PI * 719 / 720, last - first, 6);
    }

    [Fact]
    public void HalfPanoramaSweepsExactlyHalfATurn()
    {
        var geometry = Geometry(
            view: DewarpView.DefaultFor(FisheyeMount.Wall, DewarpViewMode.Panorama180),
            width: 720, height: 240);

        var (_, firstPhi) = geometry.LookAt(0, 120);
        var (_, lastPhi) = geometry.LookAt(719, 120);

        Assert.Equal(Math.PI * 719 / 720, lastPhi - firstPhi, 6);
    }

    [Fact]
    public void PanoramaPutsTheRimAtTheTopAndTheAxisAtTheBottom()
    {
        var geometry = Geometry(
            view: DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Panorama360),
            width: 720, height: 240);

        var (topTheta, _) = geometry.LookAt(360, 0);
        var (bottomTheta, _) = geometry.LookAt(360, 239);

        // Top is the horizon of a ceiling camera, which is where real-world up belongs.
        Assert.True(topTheta > bottomTheta);
        // The top row's *centre* sits half a row inside the rim, not on it — the same +0.5
        // convention as everywhere else, so the tolerance is one row's worth of angle.
        double perRow = Kia.ThetaMaxRad / 240;
        Assert.Equal(Kia.ThetaMaxRad - perRow / 2, topTheta, 9);
        Assert.Equal(perRow / 2, bottomTheta, 9);
    }

    [Fact]
    public void PanoramaRoundTrips()
    {
        var geometry = Geometry(
            view: DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Panorama360)
                with { Orientation = new ViewOrientation(50, 0, 0) },
            width: 720, height: 240);

        for (int py = 20; py < 235; py += 37)
        {
            for (int px = 5; px < 715; px += 71)
            {
                var source = geometry.SourceFor(px, py);
                if (source is null)
                    continue;
                var back = geometry.OutputFor(source.Value.X, source.Value.Y);
                Assert.NotNull(back);
                Assert.Equal(px, back!.Value.X, 5);
                Assert.Equal(py, back.Value.Y, 5);
            }
        }
    }
}
