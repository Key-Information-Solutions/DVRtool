using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The constant block the GPU shader reads, and the claim that the shader's arithmetic is the
/// same arithmetic as the tested CPU geometry.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file is the substitute for a GPU in continuous integration.</b> The Direct3D renderer
/// evaluates the projection in HLSL instead of walking a <see cref="DewarpMap"/>, so there are
/// two copies of the arithmetic and a discrepancy between them does not fail — it ships as a
/// slightly soft or slightly shifted accelerated pane, which nobody can diagnose from a
/// screenshot. <see cref="DewarpShaderConstants.SourceFor"/> is a line-for-line transcription of
/// the HLSL, and <see cref="ProjectedSourceMatchesTheGeometryEverywhere"/> holds it against
/// <see cref="DewarpGeometry.SourceFor"/> across the whole parameter space. The HLSL is then a
/// transcription of something proven rather than of something believed.
/// </para>
/// <para>
/// The ordinal and layout tests look like busywork and are not: the shader switches on
/// <see cref="LensProjection"/>'s integer values and reads the constant buffer by row, so
/// reordering the enum or inserting a field is a silent, total mis-render on the accelerated path
/// only.
/// </para>
/// </remarks>
public class FisheyeShaderTests
{
    /// <summary>
    /// Sub-pixel agreement demanded between the two texts. Set well below bilinear sampling's own
    /// error, and three hundred times tighter than the 6.6-pixel worst case the rejected
    /// coarse-grid experiment produced, so this is a real constraint rather than a rubber stamp.
    /// The residual is fp32 rounding in the constants: the geometry is 2560 pixels across and
    /// float carries about seven digits.
    /// </summary>
    private const double Tolerance = 0.02;

    // ---- Parity with the CPU geometry -------------------------------------------------

    [Fact]
    public void ProjectedSourceMatchesTheGeometryEverywhere()
    {
        int compared = 0, rimFlips = 0;

        foreach (var (calibration, view) in ParameterSpace())
        {
            const int w = 97, h = 61; // Prime-ish, so the sample grid never lands only on nice
                                      // fractions of the pane.
            var geometry = FisheyeProjection.For(calibration, view, w, h);
            var constants = geometry.ShaderConstants();

            for (int row = 0; row < h; row++)
            {
                for (int col = 0; col < w; col++)
                {
                    var expected = geometry.SourceFor(col, row);
                    var actual = constants.SourceFor(col, row);

                    if (expected is null || actual is null)
                    {
                        if (expected is null && actual is null)
                            continue;
                        // The two disagree about whether this pixel is inside the circle. That is
                        // allowed only where it is genuinely ambiguous: thetaMax is a double in
                        // the geometry and a float in the constants, so a ray landing between the
                        // two answers differently. Anywhere else it is a bug.
                        Assert.True(IsOnTheRim(geometry, calibration, col, row),
                            $"inside/outside disagreement away from the rim at ({col},{row}) " +
                            $"for {Describe(calibration, view)}");
                        rimFlips++;
                        continue;
                    }

                    double dx = expected.Value.X - actual.Value.X;
                    double dy = expected.Value.Y - actual.Value.Y;
                    double error = Math.Sqrt(dx * dx + dy * dy);
                    Assert.True(error <= Tolerance,
                        $"{error:0.####} px apart at ({col},{row}) for " +
                        $"{Describe(calibration, view)}");
                    compared++;
                }
            }
        }

        // Guards the sweep itself: a ParameterSpace that silently stopped yielding, or a geometry
        // that returned null everywhere, would otherwise pass this test by comparing nothing.
        Assert.True(compared > 100_000, $"only {compared} pixels were actually compared");
        // And the escape hatch above must stay an escape hatch. A handful of pixels on a rim
        // thousands of pixels long is float rounding; hundreds would mean the boundary itself has
        // moved.
        Assert.True(rimFlips < 32, $"{rimFlips} pixels flipped across the rim");
    }

    [Fact]
    public void RayMatchesTheGeometryEverywhere()
    {
        foreach (var (calibration, view) in ParameterSpace())
        {
            const int w = 41, h = 29;
            var geometry = FisheyeProjection.For(calibration, view, w, h);
            var constants = geometry.ShaderConstants();
            for (int row = 0; row < h; row++)
            {
                for (int col = 0; col < w; col++)
                {
                    var expected = geometry.RayFor(col, row);
                    var actual = constants.RayFor(col, row);
                    // Unit vectors, so an absolute tolerance is a relative one. Compared against
                    // an explicit epsilon rather than a decimal-place count, because rounding to
                    // N places fails two values that straddle a boundary however close they are.
                    Assert.True(Math.Abs(expected.X - actual.X) < 1e-6);
                    Assert.True(Math.Abs(expected.Y - actual.Y) < 1e-6);
                    Assert.True(Math.Abs(expected.Z - actual.Z) < 1e-6);
                }
            }
        }
    }

    [Fact]
    public void MirroredMountsAgreeToo()
    {
        // Called out separately because the handedness flip is applied in two different places —
        // to the pane coordinate here, and inside the rotation nowhere — and a transcription that
        // dropped it would still pass every ceiling-mount case.
        var calibration = FisheyeCalibration.Default(1024, 1024) with
        {
            Mount = FisheyeMount.Floor,
        };
        var view = new DewarpView(DewarpViewMode.Rectilinear, new ViewOrientation(37, 22, 4), 80);
        var geometry = FisheyeProjection.For(calibration, view, 64, 48);
        var constants = geometry.ShaderConstants();

        Assert.True(constants.IsMirrored);
        for (int row = 0; row < 48; row += 3)
        {
            for (int col = 0; col < 64; col += 3)
            {
                var expected = geometry.SourceFor(col, row);
                var actual = constants.SourceFor(col, row);
                Assert.Equal(expected.HasValue, actual.HasValue);
                if (expected is null)
                    continue;
                Assert.True(Math.Abs(expected.Value.X - actual!.Value.X) < Tolerance);
                Assert.True(Math.Abs(expected.Value.Y - actual.Value.Y) < Tolerance);
            }
        }
    }

    // ---- What the shader indexes by ---------------------------------------------------

    [Fact]
    public void LensProjectionOrdinalsAreWhatTheShaderSwitchesOn()
    {
        // Dewarp.hlsl reads these as integers. Reordering the enum would compile, pass every
        // other test, and dewarp through the wrong lens law on the GPU only.
        Assert.Equal(0, (int)LensProjection.Equidistant);
        Assert.Equal(1, (int)LensProjection.Stereographic);
        Assert.Equal(2, (int)LensProjection.EquisolidAngle);
        Assert.Equal(3, (int)LensProjection.Orthographic);
    }

    [Fact]
    public void ConstantBufferIsPackedWhereTheShaderExpects()
    {
        var calibration = new FisheyeCalibration(
            LensProjection.Stereographic, FisheyeMount.Ceiling,
            CenterX: 640, CenterY: 480, RadiusX: 400, Ellipticity: 1.25,
            RollDegrees: 0, FieldOfViewDegrees: 200, SourceWidth: 1280, SourceHeight: 960);
        var view = new DewarpView(DewarpViewMode.Panorama360, new ViewOrientation(90, 0, 0), 360);
        var buffer = FisheyeProjection.For(calibration, view, 800, 200)
            .ShaderConstants(YuvRange.Bt709Limited, 0xFF102030, lodBias: -0.5)
            .ToFloats();

        Assert.Equal(DewarpShaderConstants.FloatCount, buffer.Length);
        Assert.Equal(176, DewarpShaderConstants.ByteCount);
        Assert.Equal(0, DewarpShaderConstants.ByteCount % 16);

        Assert.Equal(800, buffer[0]);
        Assert.Equal(200, buffer[1]);
        Assert.Equal(1280, buffer[2]);
        Assert.Equal(960, buffer[3]);
        Assert.Equal(640, buffer[4]);
        Assert.Equal(480, buffer[5]);
        Assert.Equal(calibration.FocalPixels, buffer[6], 3);
        Assert.Equal(calibration.ThetaMaxRad, buffer[7], 5);
        Assert.Equal(1.25f, buffer[8]);
        Assert.Equal(1, buffer[16]);                        // is a panorama
        Assert.Equal((int)LensProjection.Stereographic, buffer[17]);
        Assert.Equal(-0.5f, buffer[18]);
        Assert.Equal(Math.PI, buffer[14], 5);               // half of a 360 degree span
        Assert.Equal(Math.PI / 2, buffer[15], 5);           // yaw 90 degrees

        var m = YuvMatrix.For(YuvRange.Bt709Limited);
        Assert.Equal(m.YScale, buffer[32], 5);
        Assert.Equal(m.YOffset, buffer[33], 5);
        Assert.Equal(m.Bu, buffer[37], 5);

        // The outside colour arrives as BGRA and leaves as RGBA floats, which is the one place a
        // channel swap could hide unnoticed until the edge of a pane came out blue.
        Assert.Equal(0x10 / 255f, buffer[40], 5);
        Assert.Equal(0x20 / 255f, buffer[41], 5);
        Assert.Equal(0x30 / 255f, buffer[42], 5);
        Assert.Equal(1f, buffer[43], 5);
    }

    [Fact]
    public void WriteToRefusesAnUndersizedBuffer()
    {
        var constants = FisheyeProjection
            .For(FisheyeCalibration.Default(256, 256), Rect(), 64, 64)
            .ShaderConstants();
        Assert.Throws<ArgumentException>(() =>
            constants.WriteTo(new float[DewarpShaderConstants.FloatCount - 1]));
    }

    // ---- Per-pixel mip selection -------------------------------------------------------

    [Fact]
    public void AMagnifyingViewSamplesTheTopMipLevel()
    {
        // Five degrees across a 2560-pixel circle is a deeply zoomed-in view: every output pixel
        // covers a fraction of a source pixel, so there is nothing to filter away.
        var constants = FisheyeProjection
            .For(FisheyeCalibration.Default(2560, 2560), Rect(fov: 5), 1600, 900)
            .ShaderConstants();
        Assert.Equal(0, constants.LodFor(800, 450), 6);
        Assert.Equal(0, constants.LodFor(20, 20), 6);
    }

    [Fact]
    public void AZoomedOutViewSamplesDownTheChain()
    {
        var calibration = FisheyeCalibration.Default(2560, 2560);
        var view = new DewarpView(DewarpViewMode.Panorama360, ViewOrientation.Center, 360);
        var constants = FisheyeProjection.For(calibration, view, 1200, 300).ShaderConstants();
        var map = DewarpMap.Build(calibration, view, 1200, 300, 2560, 2560);

        double lod = constants.LodFor(600, 150);
        Assert.True(lod > 0.5, $"a 360 degree unroll into 1200x300 should minify, got {lod}");
        // The CPU renderer picks one whole level for the pane out of a mean ratio; the shader
        // picks a fractional one per pixel. They should be in the same neighbourhood, or one of
        // the two is measuring the wrong thing.
        Assert.True(Math.Abs(lod - map.MipLevel) < 1.5,
            $"per-pixel lod {lod:0.##} is nowhere near the pane's mip level {map.MipLevel}");
    }

    [Fact]
    public void MinificationVariesAcrossAWideRectilinearPane()
    {
        // The quality claim for per-pixel selection: a wide flat window does not minify
        // uniformly, so one level for the whole pane is wrong somewhere. If these two came out
        // equal there would be no point sampling per pixel.
        //
        // The middle is the minified end, which is the opposite of the intuition and worth
        // spelling out: a rectilinear pane is a flat plane, so it spreads angle as tan(theta) and
        // a pixel out at the periphery covers only cos^2(theta) as much of the image circle as
        // one at the centre. At 140 degrees that is roughly eightfold, so the corners of a wide
        // flat view are magnifying while its middle is throwing pixels away.
        var constants = FisheyeProjection
            .For(FisheyeCalibration.Default(2560, 2560), Rect(fov: 140), 1280, 720)
            .ShaderConstants();
        double middle = constants.LodFor(640, 360);
        double corner = constants.LodFor(4, 4);
        Assert.True(middle > corner + 0.5,
            $"middle lod {middle:0.##} should be well above the corner's {corner:0.##}");
    }

    [Fact]
    public void TheRimDoesNotBlowUpTheMipLevel()
    {
        // The reason the shader takes three explicit taps instead of the hardware's free screen
        // -space derivatives. At the boundary of the circle one neighbour is outside; using its
        // coordinate anyway makes the footprint enormous, the level the coarsest available, and
        // the edge of the pane a blurred halo. Dropping it keeps the level sane.
        var calibration = FisheyeCalibration.Default(1024, 1024);
        var view = new DewarpView(DewarpViewMode.Rectilinear,
            new ViewOrientation(0, 88, 0), 120);
        var constants = FisheyeProjection.For(calibration, view, 640, 480).ShaderConstants();

        double worst = 0;
        int insideCount = 0;
        for (int row = 0; row < 480; row++)
        {
            for (int col = 0; col < 640; col++)
            {
                if (constants.SourceFor(col, row) is null)
                    continue;
                insideCount++;
                worst = Math.Max(worst, constants.LodFor(col, row));
            }
        }

        Assert.True(insideCount > 1000, "the test view should straddle the rim, not miss it");
        // Four levels is a 16x reduction. A rim pixel that had picked up its outside neighbour
        // would be far past this.
        Assert.True(worst < 4, $"worst lod on a rim-straddling pane was {worst:0.##}");
    }

    [Fact]
    public void LodBiasShiftsTheWholePane()
    {
        var calibration = FisheyeCalibration.Default(2560, 2560);
        var view = new DewarpView(DewarpViewMode.Panorama360, ViewOrientation.Center, 360);
        var plain = FisheyeProjection.For(calibration, view, 1200, 300).ShaderConstants();
        var sharper = FisheyeProjection.For(calibration, view, 1200, 300)
            .ShaderConstants(lodBias: -1);
        Assert.Equal(plain.LodFor(600, 150) - 1, sharper.LodFor(600, 150), 5);
        // Never past the top of the chain, whatever the bias.
        var absurd = FisheyeProjection.For(calibration, view, 1200, 300)
            .ShaderConstants(lodBias: -50);
        Assert.Equal(0, absurd.LodFor(600, 150), 6);
    }

    // ---- Helpers ----------------------------------------------------------------------

    private static DewarpView Rect(double yaw = 0, double pitch = 0, double fov = 90) =>
        new(DewarpViewMode.Rectilinear, new ViewOrientation(yaw, pitch, 0), fov);

    /// <summary>
    /// The corners of the parameter space the two texts have to agree in: every lens law, every
    /// mount, both pane shapes, a non-round and rolled circle, and aims from the optical axis out
    /// to the rim. Chosen to cover the cases where the arithmetic branches, rather than to be
    /// exhaustive.
    /// </summary>
    private static IEnumerable<(FisheyeCalibration Calibration, DewarpView View)> ParameterSpace()
    {
        var mounts = new[] { FisheyeMount.Ceiling, FisheyeMount.Wall, FisheyeMount.Floor };
        var projections = new[]
        {
            LensProjection.Equidistant, LensProjection.Stereographic,
            LensProjection.EquisolidAngle, LensProjection.Orthographic,
        };

        foreach (var projection in projections)
        {
            foreach (var mount in mounts)
            {
                // Deliberately not a centred round circle: an off-centre, elliptical, rolled
                // circle in a non-square frame exercises every term of the sensor-space step.
                var calibration = new FisheyeCalibration(
                    projection, mount,
                    CenterX: 1290, CenterY: 968, RadiusX: 964, Ellipticity: 1.02,
                    RollDegrees: -7, FieldOfViewDegrees: 185,
                    SourceWidth: 2592, SourceHeight: 1944).Normalized();

                foreach (var view in Views())
                    yield return (calibration, view);
            }
        }

        // And the plain square case both fleet fisheyes actually present.
        foreach (var view in Views())
            yield return (FisheyeCalibration.Default(2560, 2560), view);

        static IEnumerable<DewarpView> Views()
        {
            yield return Rect();                              // down the axis
            yield return Rect(fov: 150);                      // as wide as flat goes
            yield return Rect(fov: 5);                        // as narrow as it goes
            yield return Rect(yaw: 143, pitch: 61, fov: 70);  // aimed off-axis
            yield return Rect(yaw: 300, pitch: 88, fov: 110) with
            {
                Orientation = new ViewOrientation(300, 88, 25),
            };                                                // rolled and near the rim
            yield return new DewarpView(DewarpViewMode.Panorama180,
                new ViewOrientation(15, 0, 0), 180);
            yield return new DewarpView(DewarpViewMode.Panorama360,
                new ViewOrientation(210, 0, 0), 360);
        }
    }

    /// <summary>
    /// True when a pixel's ray lands close enough to the rim that fp32 rounding in the constants
    /// can legitimately put it on the other side.
    /// </summary>
    private static bool IsOnTheRim(in DewarpGeometry geometry,
        in FisheyeCalibration calibration, int col, int row)
    {
        var (dx, dy, dz) = geometry.RayFor(col, row);
        double theta = Math.Atan2(Math.Sqrt(dx * dx + dy * dy), dz);
        return Math.Abs(theta - calibration.ThetaMaxRad) < 1e-5;
    }

    private static string Describe(in FisheyeCalibration calibration, in DewarpView view) =>
        $"{calibration.Projection}/{calibration.Mount} {view.Mode} " +
        $"yaw {view.Orientation.YawDegrees:0.#} pitch {view.Orientation.PitchDegrees:0.#} " +
        $"fov {view.HorizontalFovDegrees:0.#}";
}
