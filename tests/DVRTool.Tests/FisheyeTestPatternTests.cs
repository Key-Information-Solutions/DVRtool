using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The synthetic floor seen through a calibrated lens: black outside the circle, tiles inside,
/// and straight once dewarped.
/// </summary>
public class FisheyeTestPatternTests
{
    private static readonly FisheyeCalibration Kia = FisheyeCalibration.Default(2560, 2560);

    [Fact]
    public void IsScaledDownToTheRequestedSideAndKeepsTheCircleInPlace()
    {
        var frame = FisheyeTestPattern.Frame(Kia, maxSide: 640);
        Assert.Equal(640, frame.Width);
        Assert.Equal(640, frame.Height);
        Assert.Equal(DewarpFrameFormat.I420, frame.Format);
        Assert.Equal(640, frame.YPitch);
        Assert.Equal(320, frame.ChromaPitch);
        Assert.Equal(320 * 320, frame.UPlane!.Length);

        // Corners lie outside an inscribed circle: black, neutral chroma.
        Assert.Equal(16, frame.YPlane[0]);
        Assert.Equal(16, frame.YPlane[639]);
        Assert.Equal(16, frame.YPlane[639 * 640 + 639]);
        Assert.Equal(128, frame.UPlane[0]);
        Assert.Equal(128, frame.VPlane![0]);

        // Just inside the rim, on the horizontal midline: the horizon band, not black.
        Assert.Equal(60, frame.YPlane[320 * 640 + 3]);
    }

    [Fact]
    public void ANonSquareFrameKeepsTheCircleWhereTheCalibrationPutsIt()
    {
        // Site H's shape: a circle inscribed in the height of a 4:3 frame, so the frame has black
        // bands left and right of the circle. Scaled to 648×486 the circle is radius 243 about
        // (324, 243): the midline is black at x = 0, horizon just inside the rim, and floor at the
        // centre.
        var casaMaya = FisheyeCalibration.Default(2592, 1944);
        var frame = FisheyeTestPattern.Frame(casaMaya, maxSide: 648);
        Assert.Equal(648, frame.Width);
        Assert.Equal(486, frame.Height);
        int mid = frame.Height / 2;
        Assert.Equal(16, frame.YPlane[mid * frame.YPitch + 0]);
        Assert.Equal(16, frame.YPlane[mid * frame.YPitch + frame.Width - 1]);
        Assert.Equal(60, frame.YPlane[mid * frame.YPitch + 324 - 243 + 2]);
        Assert.NotEqual(16, frame.YPlane[mid * frame.YPitch + 324]);
        Assert.NotEqual(60, frame.YPlane[mid * frame.YPitch + 324]);
        // Top and bottom edges touch the circle at its poles only; the corners are black.
        Assert.Equal(16, frame.YPlane[0]);
        Assert.Equal(16, frame.YPlane[(frame.Height - 1) * frame.YPitch + frame.Width - 1]);
    }

    [Fact]
    public void TheTwoSidesAreTintedOppositeWays()
    {
        var frame = FisheyeTestPattern.Frame(Kia, maxSide: 640);
        int row = 160; // chroma row at luma 320
        byte uLeft = frame.UPlane![row * frame.ChromaPitch + 60];
        byte uRight = frame.UPlane[row * frame.ChromaPitch + 260];
        byte vLeft = frame.VPlane![row * frame.ChromaPitch + 60];
        byte vRight = frame.VPlane[row * frame.ChromaPitch + 260];
        Assert.True(uLeft > 128 && uRight < 128, $"U left {uLeft}, right {uRight}");
        Assert.True(vLeft < 128 && vRight > 128, $"V left {vLeft}, right {vRight}");
    }

    [Fact]
    public void ARectilinearViewDownTheAxisShowsStraightGrout()
    {
        // Render the pattern through the CPU renderer aimed straight down, then walk a grout line
        // found near the centre: on a correct dewarp it stays bright along a whole row.
        var frame = FisheyeTestPattern.Frame(Kia, maxSide: 1024);
        var cal = Kia.ScaledTo(frame.Width, frame.Height);
        var view = new DewarpView(DewarpViewMode.Rectilinear, ViewOrientation.Center, 60);
        const int w = 400, h = 400;
        using var renderer = new CpuDewarpRenderer();
        renderer.Render(frame, new DewarpRenderRequest(cal, view, w, h));
        var pixels = new uint[w * h];
        renderer.CopyOutput(pixels, w);

        static int Luma(uint bgra) => (int)((bgra >> 8) & 0xFF); // green is close enough

        // Find the brightest row near the middle: a horizontal grout line.
        int bestRow = -1;
        double bestMean = 0;
        for (int row = h / 2 - 60; row < h / 2 + 60; row++)
        {
            double mean = 0;
            for (int col = 0; col < w; col++)
                mean += Luma(pixels[row * w + col]);
            mean /= w;
            if (mean > bestMean)
            {
                bestMean = mean;
                bestRow = row;
            }
        }
        Assert.True(bestRow >= 0);

        // Along that row, the great majority of pixels are grout-bright. A curved line would leave
        // the row within a few pixels of the middle.
        int bright = 0;
        for (int col = 0; col < w; col++)
            if (Luma(pixels[bestRow * w + col]) > 180)
                bright++;
        Assert.True(bright > w * 0.9, $"only {bright} of {w} pixels on row {bestRow} are grout");
    }

    [Fact]
    public void ADegenerateCalibrationProducesAFrameRatherThanAnException()
    {
        var broken = Kia with { RadiusX = 0 };
        var frame = FisheyeTestPattern.Frame(broken, maxSide: 64);
        Assert.Equal(64, frame.Width);
        Assert.All(frame.YPlane, b => Assert.Equal(16, b));
    }
}
