namespace DVRTool.Core;

/// <summary>
/// A synthetic fisheye frame: a tiled floor seen through the calibrated lens, so the dewarp path
/// can be exercised — and judged by eye — with no camera on the other end.
/// </summary>
/// <remarks>
/// <para>
/// The pattern is chosen so that a correct dewarp is obvious and a wrong one is too. A ceiling
/// camera looking straight down at a floor of square tiles sees them as curves; a rectilinear
/// view aimed down the axis must show them as straight lines meeting at right angles, and a
/// panorama must show the grout lines as smooth curves with no kink at the seam. The two halves
/// of the floor are tinted differently — the image's +X side towards red, −X towards blue; on a
/// ceiling mount's default view the mount roll puts that boundary horizontally across the pane —
/// so a mirrored pane (the floor-mount trap) reads as "the colours are on the wrong sides" rather
/// than looking plausible. Everything past the horizon is a flat dark band, and everything outside the
/// image circle is black, which is what the rim test looks for.
/// </para>
/// <para>
/// Built from the calibration's own inverse projection rather than a separately written forward
/// one, so the pattern is exactly what that lens would see. That means it cannot catch an error
/// that is in the lens law itself — the lens tests do that — but it does catch every error
/// downstream: the sample table, the shader, the plane upload, the colour matrix, the
/// presentation.
/// </para>
/// </remarks>
public static class FisheyeTestPattern
{
    /// <summary>Tile size in units of the camera's height above the floor.</summary>
    private const double TilePitch = 0.5;

    /// <summary>Half-width of a grout line, in the same units.</summary>
    private const double GroutHalfWidth = 0.02;

    /// <summary>Incidence angle past which the floor is treated as the horizon band.</summary>
    private const double HorizonRad = 84 * Math.PI / 180;

    /// <summary>
    /// The pattern for a calibration, at the calibration's own source size (or scaled down so the
    /// longer side is at most <paramref name="maxSide"/> pixels, keeping the circle in place).
    /// I420, limited-range BT.709 — what a software decoder produces.
    /// </summary>
    public static DewarpFrame Frame(in FisheyeCalibration calibration, int maxSide = 1600)
    {
        var cal = calibration.Normalized();
        int width = cal.SourceWidth;
        int height = cal.SourceHeight;
        if (maxSide > 0 && Math.Max(width, height) > maxSide)
        {
            double scale = (double)maxSide / Math.Max(width, height);
            width = Math.Max(2, (int)Math.Round(width * scale));
            height = Math.Max(2, (int)Math.Round(height * scale));
            cal = cal.ScaledTo(width, height);
        }

        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        var y = new byte[width * height];
        var u = new byte[chromaWidth * chromaHeight];
        var v = new byte[chromaWidth * chromaHeight];

        double focal = cal.FocalPixels;
        double thetaMax = cal.ThetaMaxRad;
        double cosRoll = Math.Cos(cal.RollDegrees * Math.PI / 180);
        double sinRoll = Math.Sin(cal.RollDegrees * Math.PI / 180);
        bool valid = double.IsFinite(focal) && focal > 0;

        Parallel.For(0, height, row =>
        {
            int rowBase = row * width;
            for (int col = 0; col < width; col++)
            {
                y[rowBase + col] = valid ? Luma(cal, focal, thetaMax, cosRoll, sinRoll, col + 0.5, row + 0.5) : (byte)16;
            }
        });

        Parallel.For(0, chromaHeight, crow =>
        {
            int rowBase = crow * chromaWidth;
            for (int ccol = 0; ccol < chromaWidth; ccol++)
            {
                // One sample per 2×2 block, at its centre.
                var (cu, cv) = valid
                    ? Chroma(cal, focal, thetaMax, cosRoll, sinRoll, ccol * 2 + 1.0, crow * 2 + 1.0)
                    : ((byte)128, (byte)128);
                u[rowBase + ccol] = cu;
                v[rowBase + ccol] = cv;
            }
        });

        return DewarpFrame.I420(width, height, y, width, u, v, chromaWidth, YuvRange.Bt709Limited);
    }

    /// <summary>
    /// Where a source pixel looks: incidence angle and azimuth in the lens frame, or null when the
    /// pixel is outside the image circle. The inverse of <see cref="DewarpGeometry.SourceForRay"/>.
    /// </summary>
    private static (double Theta, double Phi)? Direction(in FisheyeCalibration cal, double focal,
        double thetaMax, double cosRoll, double sinRoll, double sx, double sy)
    {
        double du = sx - cal.CenterX;
        double dv = (sy - cal.CenterY) / cal.Ellipticity;
        // Undo the calibration roll, as OutputFor does.
        double ru = du * cosRoll + dv * sinRoll;
        double rv = -du * sinRoll + dv * cosRoll;
        double r = Math.Sqrt(ru * ru + rv * rv);
        double theta = LensModel.ThetaFromRadiusOverFocal(cal.Projection, r / focal);
        if (double.IsNaN(theta) || theta > thetaMax)
            return null;
        return (theta, Math.Atan2(rv, ru));
    }

    private static byte Luma(in FisheyeCalibration cal, double focal, double thetaMax,
        double cosRoll, double sinRoll, double sx, double sy)
    {
        var direction = Direction(cal, focal, thetaMax, cosRoll, sinRoll, sx, sy);
        if (direction is null)
            return 16; // outside the circle: black
        var (theta, phi) = direction.Value;
        if (theta >= HorizonRad)
            return 60; // the horizon band

        // The floor point this direction hits, one unit below the camera.
        double t = Math.Tan(theta);
        double fx = t * Math.Cos(phi);
        double fy = t * Math.Sin(phi);
        double gx = fx / TilePitch;
        double gy = fy / TilePitch;
        double distX = Math.Abs(gx - Math.Round(gx)) * TilePitch;
        double distY = Math.Abs(gy - Math.Round(gy)) * TilePitch;
        if (distX < GroutHalfWidth || distY < GroutHalfWidth)
            return 235; // grout: bright
        bool even = ((int)Math.Floor(gx) + (int)Math.Floor(gy)) % 2 == 0;
        return even ? (byte)150 : (byte)90;
    }

    private static (byte U, byte V) Chroma(in FisheyeCalibration cal, double focal, double thetaMax,
        double cosRoll, double sinRoll, double sx, double sy)
    {
        var direction = Direction(cal, focal, thetaMax, cosRoll, sinRoll, sx, sy);
        if (direction is null)
            return (128, 128);
        var (theta, phi) = direction.Value;
        if (theta >= HorizonRad)
            return (128, 128);
        // Tinted by side: +X (image right) towards red, −X towards blue — the handedness tell.
        double side = Math.Cos(phi);
        byte u = (byte)Math.Round(128 - 36 * side);
        byte v = (byte)Math.Round(128 + 36 * side);
        return (u, v);
    }
}
