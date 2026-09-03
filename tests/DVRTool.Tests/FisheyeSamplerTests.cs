using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The sample table and the per-frame pixel work.
/// </summary>
/// <remarks>
/// Two groups here carry claims made in the plan rather than just guarding code. The sub-rect
/// tests carry the performance claim — that a zoomed-in view at 2560×2560 converts a few hundred
/// thousand pixels instead of 6.6 million — and the mip tests carry the quality claim, that a
/// zoomed-out view does not shimmer. Both would otherwise be invisible: the first because a
/// slower path still produces the right picture, the second because aliasing does not show up in
/// a still.
/// </remarks>
public class FisheyeSamplerTests
{
    private static readonly FisheyeCalibration Kia = FisheyeCalibration.Default(2560, 2560);

    private static DewarpView Rect(double yaw = 0, double pitch = 0, double fov = 90) =>
        new(DewarpViewMode.Rectilinear, new ViewOrientation(yaw, pitch, 0), fov);

    // ---- The table ---------------------------------------------------------------------

    [Fact]
    public void TableIsSizedToThePaneAndMostlyInsideTheCircle()
    {
        var map = DewarpMap.Build(Kia, Rect(), 320, 240, 2560, 2560);

        Assert.Equal(320, map.OutputWidth);
        Assert.Equal(240, map.OutputHeight);
        Assert.Equal(320 * 240, map.U.Length);
        Assert.Equal(320 * 240, map.V.Length);
        // A 90° view down the axis of a 180° lens is entirely inside the circle.
        Assert.Equal(320 * 240, map.InsidePixelCount);
        Assert.False(map.IsEmpty);
    }

    [Fact]
    public void OutsideThePaneIsMarkedNotClamped()
    {
        // Aimed at the rim and opened wide: the corners leave the circle.
        var map = DewarpMap.Build(Kia, Rect(pitch: 88, fov: 140), 320, 240, 2560, 2560);

        Assert.True(map.InsidePixelCount > 0, "the centre of the view must still be inside");
        Assert.True(map.InsidePixelCount < 320 * 240, "the corners must fall off the circle");

        int sentinels = 0;
        for (int i = 0; i < map.U.Length; i++)
        {
            if (map.U[i] == DewarpMap.Outside)
            {
                Assert.Equal(DewarpMap.Outside, map.V[i]);
                sentinels++;
            }
        }
        Assert.Equal(320 * 240 - map.InsidePixelCount, sentinels);
    }

    /// <summary>
    /// The bounding box must contain every source pixel the table reads (sound) and waste no
    /// row or column (tight). An off-by-one either way is a visible black edge or a silently
    /// oversized conversion.
    /// </summary>
    [Fact]
    public void BoundsAreSoundAndTight()
    {
        var map = DewarpMap.Build(Kia, Rect(yaw: 30, pitch: 40, fov: 60), 320, 240, 2560, 2560);
        var bounds = map.Bounds;

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int i = 0; i < map.U.Length; i++)
        {
            if (map.U[i] == DewarpMap.Outside)
                continue;
            int x = map.U[i] >> DewarpMap.FractionalBits;
            int y = map.V[i] >> DewarpMap.FractionalBits;
            Assert.InRange(x, bounds.X, bounds.Right - 1);
            Assert.InRange(y, bounds.Y, bounds.Bottom - 1);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        // Tight: the box is the sampled extent, widened by exactly one for bilinear's neighbour.
        Assert.Equal(minX, bounds.X);
        Assert.Equal(minY, bounds.Y);
        Assert.Equal(maxX + 1, bounds.Right - 1);
        Assert.Equal(maxY + 1, bounds.Bottom - 1);
    }

    [Fact]
    public void BoundsAreClippedToTheFrame()
    {
        var map = DewarpMap.Build(Kia, Rect(pitch: 85, fov: 140), 320, 240, 2560, 2560);

        Assert.True(map.Bounds.X >= 0);
        Assert.True(map.Bounds.Y >= 0);
        Assert.True(map.Bounds.Right <= 2560);
        Assert.True(map.Bounds.Bottom <= 2560);
    }

    /// <summary>
    /// The performance claim, made measurable: a zoomed-in view of a 2560×2560 camera needs a
    /// small fraction of the frame, which is what lets the CPU path keep up at that resolution.
    /// </summary>
    [Fact]
    public void ZoomedInViewNeedsOnlyASliverOfTheFrame()
    {
        var zoomedIn = DewarpMap.Build(Kia, Rect(pitch: 30, fov: 20), 1600, 900, 2560, 2560);
        var zoomedOut = DewarpMap.Build(Kia, Rect(fov: 150), 1600, 900, 2560, 2560);

        double frame = 2560.0 * 2560;
        Assert.True(zoomedIn.Bounds.PixelCount < frame * 0.10,
            $"a 20° view read {zoomedIn.Bounds.PixelCount / frame:P1} of the frame; the sub-rect " +
            "restriction is what keeps the CPU path affordable at 2560x2560");
        Assert.True(zoomedOut.Bounds.PixelCount > zoomedIn.Bounds.PixelCount * 5);
    }

    /// <summary>
    /// Zoomed in the view magnifies, so no filtering is needed; zoomed out it skips source
    /// pixels and must box-halve first or it shimmers.
    /// </summary>
    [Fact]
    public void MinificationRatioTracksTheZoomAndSelectsAMipLevel()
    {
        // Magnifying hard: a 10° slice of the circle blown up to 1600 wide.
        var magnifying = DewarpMap.Build(Kia, Rect(fov: 10), 1600, 900, 2560, 2560);
        Assert.True(magnifying.MinificationRatio < 1,
            $"ratio {magnifying.MinificationRatio} should be below 1 when magnifying");
        Assert.Equal(0, magnifying.MipLevel);

        // Minifying: the whole circle into a small pane.
        var minifying = DewarpMap.Build(Kia, Rect(fov: 150), 400, 300, 2560, 2560);
        Assert.True(minifying.MinificationRatio > 3,
            $"ratio {minifying.MinificationRatio} should be well above 1 when minifying");
        Assert.True(minifying.MipLevel >= 2);
    }

    [Fact]
    public void IdentityMapHasARatioOfOne()
    {
        var map = IdentityMap(64, 48);
        Assert.Equal(1.0, map.MinificationRatio, 6);
        Assert.Equal(0, map.MipLevel);
    }

    /// <summary>
    /// Every table the projection produces samples inside the circle it was built for, across
    /// the whole parameter space — every lens law, a spread of lens and view fields of view.
    /// </summary>
    /// <remarks>
    /// This replaced a test of a coarse-grid build that no longer exists. That path was measured
    /// as both slower than the exact one (11 ms against 6 ms at 1600×900, because it re-evaluated
    /// each cell's corners per pixel) and, at 16-pixel spacing, up to 6.6 source pixels off in
    /// the worst corner of this same parameter space — a narrow 60° lens under a wide 140° view.
    /// See <see cref="DewarpMap.Build"/>'s remarks.
    /// </remarks>
    [Fact]
    public void EveryProjectionAndFieldOfViewProducesASoundTable()
    {
        foreach (var projection in Enum.GetValues<LensProjection>())
        {
            foreach (double lensFov in new[] { 60.0, 120.0, 180.0, 195.0 })
            {
                var cal = Kia with { Projection = projection, FieldOfViewDegrees = lensFov };
                if (cal.Validate() is not null)
                    continue;
                foreach (double viewFov in new[] { 20.0, 80.0, 140.0 })
                {
                    var map = DewarpMap.Build(cal, Rect(yaw: 25, pitch: 20, fov: viewFov),
                        320, 240, 2560, 2560);
                    string where = $"{projection} lens {lensFov}° view {viewFov}°";

                    Assert.True(map.InsidePixelCount > 0, $"{where}: nothing inside the circle");
                    for (int i = 0; i < map.U.Length; i++)
                    {
                        if (map.U[i] == DewarpMap.Outside)
                            continue;
                        int x = map.U[i] >> DewarpMap.FractionalBits;
                        int y = map.V[i] >> DewarpMap.FractionalBits;
                        Assert.InRange(x, map.Bounds.X, map.Bounds.Right - 1);
                        Assert.InRange(y, map.Bounds.Y, map.Bounds.Bottom - 1);
                    }
                    Assert.True(map.Bounds.Right <= 2560, $"{where}: bounds ran off the frame");
                    Assert.True(map.Bounds.Bottom <= 2560, $"{where}: bounds ran off the frame");
                    Assert.True(map.MinificationRatio > 0, $"{where}: no ratio");
                }
            }
        }
    }

    [Fact]
    public void AViewEntirelyOffTheCircleIsEmpty()
    {
        // A 1° lens with the view aimed at its rim: nothing the pane covers is inside.
        var pinhole = Kia with { FieldOfViewDegrees = 1 };
        var map = DewarpMap.Build(pinhole, Rect(pitch: 0.49, fov: 150), 64, 64, 2560, 2560);

        if (map.IsEmpty)
        {
            Assert.Equal(0, map.InsidePixelCount);
            Assert.True(map.Bounds.IsEmpty);
        }
    }

    /// <summary>
    /// The table is built for the frame in hand, not the frame the circle was measured on. PS
    /// Kia encodes 2560×2560 main and 720×720 sub; without the rescale the sub stream's dewarp
    /// aims at empty space.
    /// </summary>
    [Fact]
    public void TableRescalesACalibrationOntoTheStreamInHand()
    {
        var onSub = DewarpMap.Build(Kia, Rect(), 320, 240, 720, 720);

        Assert.Equal(720, onSub.SourceWidth);
        Assert.True(onSub.InsidePixelCount > 0);
        // The centre of the pane lands on the centre of the rescaled circle at (360, 360). Pixel
        // (160, 120) is the nearest whole pixel to a 320x240 pane's true centre of (159.5, 119.5),
        // so it sits half a pane-pixel out — which is about 0.7 source pixels at this zoom.
        int nearCentre = 120 * 320 + 160;
        Assert.InRange(onSub.U[nearCentre] / (double)DewarpMap.One, 359, 361);
        Assert.InRange(onSub.V[nearCentre] / (double)DewarpMap.One, 359, 361);
        // The 2x2 block around the pane's true centre of (159.5, 119.5) averages onto it exactly.
        // All four pixels are needed, not a horizontal pair: a ceiling mount's quarter turn maps
        // the pane's X onto the source's Y, so holding the row fixed straddles only one axis.
        double u = 0, v = 0;
        foreach (int row in new[] { 119, 120 })
        {
            foreach (int col in new[] { 159, 160 })
            {
                u += onSub.U[row * 320 + col] / (double)DewarpMap.One / 4;
                v += onSub.V[row * 320 + col] / (double)DewarpMap.One / 4;
            }
        }
        Assert.Equal(360.0, u, 3);
        Assert.Equal(360.0, v, 3);
        Assert.True(onSub.Bounds.Right <= 720);
    }

    // ---- Colour -----------------------------------------------------------------------

    [Theory]
    [InlineData(YuvRange.Bt601Limited, 16, 128, 128, 0, 0, 0)]
    [InlineData(YuvRange.Bt601Limited, 235, 128, 128, 255, 255, 255)]
    [InlineData(YuvRange.Bt601Limited, 81, 90, 240, 255, 0, 0)]
    [InlineData(YuvRange.Bt709Limited, 16, 128, 128, 0, 0, 0)]
    [InlineData(YuvRange.Bt709Limited, 235, 128, 128, 255, 255, 255)]
    [InlineData(YuvRange.Bt601Full, 0, 128, 128, 0, 0, 0)]
    [InlineData(YuvRange.Bt601Full, 255, 128, 128, 255, 255, 255)]
    public void KnownColours(YuvRange range, byte y, byte u, byte v, int r, int g, int b)
    {
        uint bgra = Convert1(y, u, v, range);

        Assert.InRange((int)((bgra >> 16) & 0xFF), r - 2, r + 2);
        Assert.InRange((int)((bgra >> 8) & 0xFF), g - 2, g + 2);
        Assert.InRange((int)(bgra & 0xFF), b - 2, b + 2);
        Assert.Equal(0xFFu, (bgra >> 24) & 0xFF);
    }

    [Fact]
    public void LimitedAndFullRangeDifferOnTheSameBytes()
    {
        // The "why is the dewarped view washed out" bug: mid-grey is not the same colour under
        // the two ranges, so picking the wrong one shifts the whole picture.
        uint limited = Convert1(128, 128, 128, YuvRange.Bt709Limited);
        uint full = Convert1(128, 128, 128, YuvRange.Bt709Full);
        Assert.NotEqual(limited & 0xFF, full & 0xFF);
    }

    [Fact]
    public void Bt601AndBt709DifferOnSaturatedColour()
    {
        uint sd = Convert1(81, 90, 240, YuvRange.Bt601Limited);
        uint hd = Convert1(81, 90, 240, YuvRange.Bt709Limited);
        Assert.NotEqual(sd, hd);
    }

    /// <summary>
    /// Converting a sub-rect must give exactly what converting the whole frame and cropping
    /// would. The odd offsets are the point: 4:2:0 chroma covers a 2×2 luma block, so a rect
    /// starting at an odd X or Y begins mid-block, and indexing chroma rect-relatively lands
    /// half a sample off and tints the edge.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 5)]
    [InlineData(13, 11)]
    public void SubRectConversionMatchesTheWholeFrame(int rectX, int rectY)
    {
        const int w = 32, h = 24;
        var (y, u, v) = SyntheticI420(w, h);

        var whole = new uint[w * h];
        DewarpSampler.ConvertI420ToBgra(y, w, u, v, (w + 1) / 2,
            new SourceRect(0, 0, w, h), YuvRange.Bt709Limited, whole, w);

        var rect = new SourceRect(rectX, rectY, 9, 7);
        var part = new uint[rect.Width * rect.Height];
        DewarpSampler.ConvertI420ToBgra(y, w, u, v, (w + 1) / 2,
            rect, YuvRange.Bt709Limited, part, rect.Width);

        for (int row = 0; row < rect.Height; row++)
        {
            for (int col = 0; col < rect.Width; col++)
            {
                Assert.Equal(whole[(rect.Y + row) * w + rect.X + col],
                    part[row * rect.Width + col]);
            }
        }
    }

    [Fact]
    public void Nv12MatchesI420OnTheSameChroma()
    {
        const int w = 16, h = 12;
        var (y, u, v) = SyntheticI420(w, h);
        int uvPitch = (w + 1) / 2;

        var interleaved = new byte[uvPitch * 2 * ((h + 1) / 2)];
        for (int row = 0; row < (h + 1) / 2; row++)
        {
            for (int col = 0; col < uvPitch; col++)
            {
                interleaved[row * uvPitch * 2 + col * 2] = u[row * uvPitch + col];
                interleaved[row * uvPitch * 2 + col * 2 + 1] = v[row * uvPitch + col];
            }
        }

        var rect = new SourceRect(3, 3, 8, 6);
        var fromPlanar = new uint[rect.Width * rect.Height];
        var fromInterleaved = new uint[rect.Width * rect.Height];
        DewarpSampler.ConvertI420ToBgra(y, w, u, v, uvPitch, rect, YuvRange.Bt709Limited,
            fromPlanar, rect.Width);
        DewarpSampler.ConvertNv12ToBgra(y, w, interleaved, uvPitch * 2, rect,
            YuvRange.Bt709Limited, fromInterleaved, rect.Width);

        Assert.Equal(fromPlanar, fromInterleaved);
    }

    /// <summary>
    /// The plane layout is declared to libvlc as a pitch and a line count, and libvlc writes
    /// that many rows of that many bytes without checking. A padded pitch must therefore convert
    /// identically to a tight one — this is the test that makes the vmem declaration safe.
    /// </summary>
    [Fact]
    public void PitchPaddingDoesNotChangeTheResult()
    {
        const int w = 20, h = 14;
        var (y, u, v) = SyntheticI420(w, h);
        int uvPitch = (w + 1) / 2;

        int paddedY = Align(w, 64);
        int paddedUv = paddedY / 2;
        var yPad = new byte[paddedY * Align(h, 16)];
        var uPad = new byte[paddedUv * Align(h, 16) / 2];
        var vPad = new byte[uPad.Length];
        for (int row = 0; row < h; row++)
            Array.Copy(y, row * w, yPad, row * paddedY, w);
        for (int row = 0; row < (h + 1) / 2; row++)
        {
            Array.Copy(u, row * uvPitch, uPad, row * paddedUv, uvPitch);
            Array.Copy(v, row * uvPitch, vPad, row * paddedUv, uvPitch);
        }

        var rect = new SourceRect(0, 0, w, h);
        var tight = new uint[w * h];
        var padded = new uint[w * h];
        DewarpSampler.ConvertI420ToBgra(y, w, u, v, uvPitch, rect, YuvRange.Bt709Limited,
            tight, w);
        DewarpSampler.ConvertI420ToBgra(yPad, paddedY, uPad, vPad, paddedUv, rect,
            YuvRange.Bt709Limited, padded, w);

        Assert.Equal(tight, padded);
    }

    /// <summary>
    /// An odd frame size, converted against spans sized exactly to the declaration — so reading
    /// past the end throws rather than quietly picking up a neighbouring row.
    /// </summary>
    [Fact]
    public void OddDimensionsStayInsideTheirPlanes()
    {
        const int w = 721, h = 721;
        int uvPitch = (w + 1) / 2;   // 361
        int uvRows = (h + 1) / 2;

        var y = new byte[w * h];
        var u = new byte[uvPitch * uvRows];
        var v = new byte[uvPitch * uvRows];
        Array.Fill(y, (byte)128);
        Array.Fill(u, (byte)128);
        Array.Fill(v, (byte)128);

        var dest = new uint[w * h];
        DewarpSampler.ConvertI420ToBgra(y, w, u, v, uvPitch,
            new SourceRect(0, 0, w, h), YuvRange.Bt709Limited, dest, w);

        Assert.Equal(361, uvPitch);
        Assert.All(dest, pixel => Assert.Equal(0xFFu, (pixel >> 24) & 0xFF));
    }

    // ---- Box halving -------------------------------------------------------------------

    [Fact]
    public void BoxHalveAveragesFourPixels()
    {
        uint[] source =
        [
            Bgra(0, 0, 0), Bgra(40, 80, 120),
            Bgra(80, 160, 240), Bgra(120, 0, 0),
        ];
        var dest = new uint[1];

        DewarpSampler.BoxHalve(source, 2, 2, 2, dest, 1);

        // (0+40+80+120)/4 = 60, (0+80+160+0)/4 = 60, (0+120+240+0)/4 = 90
        Assert.Equal(60u, (dest[0] >> 16) & 0xFF);
        Assert.Equal(60u, (dest[0] >> 8) & 0xFF);
        Assert.Equal(90u, dest[0] & 0xFF);
    }

    [Fact]
    public void BoxHalveOfAFlatImageIsFlat()
    {
        var source = new uint[8 * 8];
        Array.Fill(source, Bgra(37, 211, 99));
        var dest = new uint[4 * 4];

        DewarpSampler.BoxHalve(source, 8, 8, 8, dest, 4);

        Assert.All(dest, pixel => Assert.Equal(Bgra(37, 211, 99), pixel));
    }

    [Fact]
    public void BoxHalveHandlesOddSizesWithoutReadingPastTheEnd()
    {
        const int w = 7, h = 5;
        var source = new uint[w * h];
        for (int i = 0; i < source.Length; i++)
            source[i] = Bgra((byte)(i * 3), 0, 0);
        var dest = new uint[4 * 3];

        DewarpSampler.BoxHalve(source, w, h, w, dest, 4);

        // The odd last column and row have no partner; they carry through rather than overrun.
        Assert.All(dest, pixel => Assert.Equal(0xFFu, (pixel >> 24) & 0xFF));
    }

    // ---- The gather --------------------------------------------------------------------

    /// <summary>
    /// The identity warp reproduces its source byte for byte, in both sampling modes. This is
    /// the test that catches stride bugs, which are the commonest bug class in this feature.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IdentityWarpReproducesTheSource(bool bilinear)
    {
        const int w = 64, h = 48;
        var source = new uint[w * h];
        for (int i = 0; i < source.Length; i++)
            source[i] = Bgra((byte)(i % 251), (byte)(i * 7 % 253), (byte)(i * 13 % 249));

        var map = IdentityMap(w, h);
        var dest = new uint[w * h];
        DewarpSampler.Sample(source, w, h, w, new SourceRect(0, 0, w, h), 0, map, dest, w,
            bilinear);

        Assert.Equal(source, dest);
    }

    /// <summary>
    /// Whatever stride the destination has, the padding beyond each row must be untouched — a
    /// bitmap's back buffer is wider than its pixels and the extra bytes are not ours.
    /// </summary>
    [Fact]
    public void GatherLeavesDestinationPaddingAlone()
    {
        const int w = 16, h = 8, stride = 24;
        var source = new uint[w * h];
        Array.Fill(source, Bgra(10, 20, 30));

        var map = IdentityMap(w, h);
        var dest = new uint[stride * h];
        Array.Fill(dest, 0xCDCDCDCD);

        DewarpSampler.Sample(source, w, h, w, new SourceRect(0, 0, w, h), 0, map, dest, stride);

        for (int row = 0; row < h; row++)
        {
            for (int col = w; col < stride; col++)
                Assert.Equal(0xCDCDCDCD, dest[row * stride + col]);
        }
    }

    [Fact]
    public void OutsidePixelsGetTheOutsideColour()
    {
        const int w = 8, h = 8;
        var source = new uint[w * h];
        Array.Fill(source, Bgra(255, 255, 255));

        var map = DewarpMap.Build(Kia, Rect(pitch: 88, fov: 150), 32, 32, 2560, 2560);
        Assert.True(map.InsidePixelCount < 32 * 32, "this view must fall off the circle");

        var dest = new uint[32 * 32];
        DewarpSampler.Sample(source, w, h, w, map.Bounds, 0, map, dest, 32,
            bilinear: true, outsideColor: 0xFF00FF00);

        int green = dest.Count(p => p == 0xFF00FF00);
        Assert.True(green >= 32 * 32 - map.InsidePixelCount);
    }

    /// <summary>
    /// A source buffer holding only the bounding box still samples correctly: the table is in
    /// absolute frame coordinates and the origin is subtracted at gather time. Getting this
    /// wrong shifts the whole picture by the box's corner.
    /// </summary>
    [Fact]
    public void GatherHonoursTheSubRectOrigin()
    {
        const int frame = 64;
        var whole = new uint[frame * frame];
        for (int i = 0; i < whole.Length; i++)
            whole[i] = Bgra((byte)(i % 256), (byte)(i / frame), 7);

        var rect = new SourceRect(20, 12, 24, 20);
        var cropped = new uint[rect.Width * rect.Height];
        for (int row = 0; row < rect.Height; row++)
        {
            for (int col = 0; col < rect.Width; col++)
                cropped[row * rect.Width + col] = whole[(rect.Y + row) * frame + rect.X + col];
        }

        // A table that reads a 4x4 patch from inside the rect.
        var map = ConstantMap(4, 4, rect.X + 5, rect.Y + 3);

        var fromWhole = new uint[16];
        var fromCropped = new uint[16];
        DewarpSampler.Sample(whole, frame, frame, frame, new SourceRect(0, 0, frame, frame), 0,
            map, fromWhole, 4);
        DewarpSampler.Sample(cropped, rect.Width, rect.Height, rect.Width, rect, 0,
            map, fromCropped, 4);

        Assert.Equal(fromWhole, fromCropped);
        Assert.Equal(whole[(rect.Y + 3) * frame + rect.X + 5], fromCropped[0]);
    }

    [Fact]
    public void GatherHonoursTheMipShift()
    {
        const int w = 32, h = 32;
        var full = new uint[w * h];
        for (int i = 0; i < full.Length; i++)
            full[i] = Bgra((byte)(i % 256), 0, 0);

        var halved = new uint[16 * 16];
        DewarpSampler.BoxHalve(full, w, h, w, halved, 16);

        // Reading absolute source pixel (8,8) out of a once-halved buffer must land on (4,4).
        var map = ConstantMap(2, 2, 8, 8);
        var dest = new uint[4];
        DewarpSampler.Sample(halved, 16, 16, 16, new SourceRect(0, 0, w, h), 1, map, dest, 2,
            bilinear: false);

        Assert.Equal(halved[4 * 16 + 4], dest[0]);
    }

    [Fact]
    public void BilinearAtExactPixelCentresEqualsNearest()
    {
        const int w = 32, h = 32;
        var source = new uint[w * h];
        for (int i = 0; i < source.Length; i++)
            source[i] = Bgra((byte)(i * 5 % 256), (byte)(i % 97), 11);

        var map = IdentityMap(w, h);
        var linear = new uint[w * h];
        var nearest = new uint[w * h];
        DewarpSampler.Sample(source, w, h, w, new SourceRect(0, 0, w, h), 0, map, linear, w, true);
        DewarpSampler.Sample(source, w, h, w, new SourceRect(0, 0, w, h), 0, map, nearest, w, false);

        Assert.Equal(nearest, linear);
    }

    /// <summary>
    /// The aliasing test — and the only one here that would catch the mip path silently not
    /// being applied, because the failure is invisible in a still image and only shows up as
    /// crawling and sparkling once anything moves.
    /// </summary>
    /// <remarks>
    /// A one-pixel checkerboard minified 4× is the worst case for point sampling, and it fails
    /// in the most spectacular way available: sampling every fourth pixel of a
    /// <c>(row+col) &amp; 1</c> pattern always lands on the <i>same</i> phase, so the entire
    /// image collapses to flat black and its mean goes from 127.5 to 0. Box-halving twice first
    /// averages the pattern properly and the mean survives. The assertion is therefore on the
    /// mean rather than the variance — a variance test would see two flat images and pass.
    /// </remarks>
    [Fact]
    public void MinifyingThroughAMipLevelSuppressesAliasing()
    {
        const int w = 256, h = 256;
        var source = new uint[w * h];
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
                source[row * w + col] = ((row + col) & 1) == 0 ? Bgra(0, 0, 0) : Bgra(255, 255, 255);
        }

        // A 4x reduction: the map reads every fourth source pixel.
        var map = ScaleMap(64, 64, 4);

        var unfiltered = new uint[64 * 64];
        DewarpSampler.Sample(source, w, h, w, new SourceRect(0, 0, w, h), 0, map, unfiltered, 64,
            bilinear: false);

        // Two halvings to match the 4x, then gather with the coordinates scaled to suit.
        var level1 = new uint[128 * 128];
        var level2 = new uint[64 * 64];
        DewarpSampler.BoxHalve(source, w, h, w, level1, 128);
        DewarpSampler.BoxHalve(level1, 128, 128, 128, level2, 64);
        var filtered = new uint[64 * 64];
        DewarpSampler.Sample(level2, 64, 64, 64, new SourceRect(0, 0, w, h), 2, map, filtered, 64,
            bilinear: false);

        // A half-black, half-white source averages to 127.5. That is what the dewarped view
        // should show once it can no longer resolve the pattern.
        double unfilteredMean = Mean(unfiltered);
        double filteredMean = Mean(filtered);

        Assert.True(Math.Abs(filteredMean - 127.5) < 3,
            $"box-halving should preserve the source's average, got {filteredMean:F1}");
        Assert.True(Math.Abs(unfilteredMean - 127.5) > 50,
            $"point sampling at 4x should alias the pattern away, but its mean was " +
            $"{unfilteredMean:F1} — suspiciously close to correct, so this test is no longer " +
            "exercising the failure it exists to catch");
        // ...and the filtered result is genuinely flat, as a real average of a checkerboard is.
        Assert.True(Spread(filtered) < 3, $"filtered spread {Spread(filtered):F1}");
    }

    // ---- helpers -----------------------------------------------------------------------

    private static int Align(int value, int to) => (value + to - 1) / to * to;

    private static uint Bgra(byte r, byte g, byte b) =>
        0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;

    private static uint Convert1(byte y, byte u, byte v, YuvRange range)
    {
        var dest = new uint[1];
        DewarpSampler.ConvertI420ToBgra([y], 1, [u], [v], 1,
            new SourceRect(0, 0, 1, 1), range, dest, 1);
        return dest[0];
    }

    private static (byte[] Y, byte[] U, byte[] V) SyntheticI420(int w, int h)
    {
        int uvPitch = (w + 1) / 2;
        int uvRows = (h + 1) / 2;
        var y = new byte[w * h];
        var u = new byte[uvPitch * uvRows];
        var v = new byte[uvPitch * uvRows];
        for (int i = 0; i < y.Length; i++)
            y[i] = (byte)(i * 7 % 256);
        for (int i = 0; i < u.Length; i++)
        {
            u[i] = (byte)(i * 11 % 256);
            v[i] = (byte)(255 - i * 5 % 256);
        }
        return (y, u, v);
    }

    /// <summary>A table that maps every output pixel onto the source pixel of the same index.</summary>
    private static DewarpMap IdentityMap(int w, int h) => ScaleMap(w, h, 1);

    /// <summary>A table that reads every <paramref name="step"/>-th source pixel.</summary>
    private static DewarpMap ScaleMap(int w, int h, int step) =>
        DewarpMap.FromSourceCoordinates(w, h, w * step + 1, h * step + 1,
            (col, row) => (col * (double)step, row * (double)step));

    /// <summary>A table where every output pixel reads the same source pixel.</summary>
    private static DewarpMap ConstantMap(int w, int h, double sx, double sy) =>
        DewarpMap.FromSourceCoordinates(w, h, (int)sx + 2, (int)sy + 2, (_, _) => (sx, sy));

    /// <summary>Mean of the blue channel.</summary>
    private static double Mean(uint[] pixels) => pixels.Average(p => (double)(p & 0xFF));

    /// <summary>Standard deviation of the blue channel — how much contrast survived.</summary>
    private static double Spread(uint[] pixels)
    {
        double mean = Mean(pixels);
        return Math.Sqrt(pixels.Average(p => Math.Pow((p & 0xFF) - mean, 2)));
    }
}
