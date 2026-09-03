namespace DVRTool.Core;

/// <summary>
/// The exact constant block the GPU dewarp shader reads, plus a transcription of the shader's own
/// arithmetic in C# so the two can be proved to agree with no GPU in the room.
/// </summary>
/// <remarks>
/// <para>
/// <b>The GPU path has no <see cref="DewarpMap"/>.</b> That table exists because the trigonometry
/// costs 6–15 ms per rebuild on a CPU and the view only changes when the operator moves it — so
/// paying it once and walking memory afterwards is the whole design. On a GPU the same
/// trigonometry is about thirty flops in a pixel shader, which at 1920×1080 and 30 fps is under
/// 6 GFLOP/s: well under a percent of any adapter DVRTool will meet. So the shader evaluates the
/// projection per pixel and the table disappears, which is what removes the last cost on the
/// interaction path — a drag on the GPU renderer rebuilds <i>nothing</i>, it just writes 176
/// bytes of constants.
/// </para>
/// <para>
/// <b>That leaves one real risk, and this type exists to close it: the shader and
/// <see cref="DewarpGeometry"/> drifting apart.</b> Two copies of this arithmetic in two
/// languages, and a mismatch does not crash — it ships as "the accelerated view looks slightly
/// soft" or "the GPU one is half a pixel off", which nobody can debug from a screenshot. So the
/// constants are taken <i>from</i> <see cref="DewarpGeometry"/>'s own precomputed fields rather
/// than re-derived (there is no second copy of the rotation build, the focal length or the
/// tangents), and <see cref="SourceFor"/> is a line-for-line transcription of the HLSL that the
/// tests assert against <see cref="DewarpGeometry.SourceFor"/> across the parameter space. The
/// HLSL is then a transcription of something tested, rather than of something hoped for.
/// </para>
/// <para>
/// <b>Floats, not doubles.</b> A constant buffer is fp32, so the fields are fp32 and the
/// transcription reads them back at fp32 — the parity test therefore measures the formulation
/// with the precision loss already in it, instead of comparing two doubles and finding the
/// discrepancy later on real hardware.
/// </para>
/// </remarks>
public readonly struct DewarpShaderConstants
{
    /// <summary>Floats in the packed buffer: eleven <c>float4</c> rows.</summary>
    public const int FloatCount = 44;

    /// <summary>Bytes in the packed buffer. A multiple of 16, as Direct3D requires.</summary>
    public const int ByteCount = FloatCount * 4;

    // Geometry. Every one of these is copied from DewarpGeometry rather than recomputed.
    private readonly float _outputWidth;
    private readonly float _outputHeight;
    private readonly float _sourceWidth;
    private readonly float _sourceHeight;
    private readonly float _centerX;
    private readonly float _centerY;
    private readonly float _focal;
    private readonly float _thetaMax;
    private readonly float _ellipticity;
    private readonly float _cosRoll;
    private readonly float _sinRoll;
    private readonly float _mirrored;
    private readonly float _tanHalfW;
    private readonly float _tanHalfH;
    private readonly float _panoramaHalfSpan;
    private readonly float _yaw;
    private readonly float _isPanorama;
    private readonly float _projection;
    private readonly float _lodBias;
    private readonly float _r00, _r01, _r02;
    private readonly float _r10, _r11, _r12;
    private readonly float _r20, _r21, _r22;

    // Colour and the area outside the image circle: render options rather than geometry, so
    // SourceFor ignores them and the parity test is unaffected by them.
    private readonly float _yScale, _yOffset, _rv, _gu, _gv, _bu;
    private readonly float _outsideR, _outsideG, _outsideB, _outsideA;

    internal DewarpShaderConstants(
        int outputWidth, int outputHeight,
        in FisheyeCalibration calibration, in DewarpView view, in Rot3 rotation,
        double focal, double thetaMax, double tanHalfW, double tanHalfH,
        double cosRoll, double sinRoll, bool mirrored,
        YuvRange range, uint outsideColor, double lodBias)
    {
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
        _sourceWidth = calibration.SourceWidth;
        _sourceHeight = calibration.SourceHeight;
        _centerX = (float)calibration.CenterX;
        _centerY = (float)calibration.CenterY;
        _focal = (float)focal;
        _thetaMax = (float)thetaMax;
        _ellipticity = (float)calibration.Ellipticity;
        _cosRoll = (float)cosRoll;
        _sinRoll = (float)sinRoll;
        _mirrored = mirrored ? 1 : 0;
        _tanHalfW = (float)tanHalfW;
        _tanHalfH = (float)tanHalfH;

        bool panorama = view.Mode is DewarpViewMode.Panorama180 or DewarpViewMode.Panorama360;
        _isPanorama = panorama ? 1 : 0;
        // Stored already halved, because that is the form the shader uses.
        _panoramaHalfSpan = (float)((view.Mode == DewarpViewMode.Panorama360 ? 360 : 180)
            * Math.PI / 180 / 2);
        _yaw = (float)view.Orientation.YawRad;
        _projection = (int)calibration.Projection;
        _lodBias = double.IsFinite(lodBias) ? (float)lodBias : 0f;

        _r00 = (float)rotation.M00; _r01 = (float)rotation.M01; _r02 = (float)rotation.M02;
        _r10 = (float)rotation.M10; _r11 = (float)rotation.M11; _r12 = (float)rotation.M12;
        _r20 = (float)rotation.M20; _r21 = (float)rotation.M21; _r22 = (float)rotation.M22;

        var m = YuvMatrix.For(range);
        _yScale = (float)m.YScale;
        _yOffset = (float)m.YOffset;
        _rv = (float)m.Rv;
        _gu = (float)m.Gu;
        _gv = (float)m.Gv;
        _bu = (float)m.Bu;

        // BGRA, the order the CPU sampler's uint pixels are in, to 0..1 floats.
        _outsideB = ((outsideColor >> 0) & 0xFF) / 255f;
        _outsideG = ((outsideColor >> 8) & 0xFF) / 255f;
        _outsideR = ((outsideColor >> 16) & 0xFF) / 255f;
        _outsideA = ((outsideColor >> 24) & 0xFF) / 255f;
    }

    /// <summary>Pane width in pixels.</summary>
    public int OutputWidth => (int)_outputWidth;

    /// <summary>Pane height in pixels.</summary>
    public int OutputHeight => (int)_outputHeight;

    /// <summary>Source frame width the constants were built against.</summary>
    public int SourceWidth => (int)_sourceWidth;

    /// <summary>Source frame height the constants were built against.</summary>
    public int SourceHeight => (int)_sourceHeight;

    /// <summary>The lens law, as the ordinal the shader switches on.</summary>
    public LensProjection Projection => (LensProjection)(int)_projection;

    /// <summary>True when the view is one of the two panorama unrolls.</summary>
    public bool IsPanorama => _isPanorama != 0;

    /// <summary>True when the mount hands the scene the other way round.</summary>
    public bool IsMirrored => _mirrored != 0;

    /// <summary>
    /// Writes the packed buffer, in the row order the HLSL <c>cbuffer</c> declares.
    /// </summary>
    /// <remarks>
    /// Every row is a full <c>float4</c> — including the three rotation rows, which are padded
    /// rather than packed three-to-a-row. HLSL never straddles a <c>float3</c> across a 16-byte
    /// boundary, so a hand-packed layout and the compiler's idea of it are only ever one edit
    /// away from disagreeing; explicit padding costs 12 bytes once and cannot.
    /// </remarks>
    public void WriteTo(Span<float> destination)
    {
        if (destination.Length < FloatCount)
            throw new ArgumentException(
                $"The constant buffer needs {FloatCount} floats.", nameof(destination));
        destination[0] = _outputWidth;
        destination[1] = _outputHeight;
        destination[2] = _sourceWidth;
        destination[3] = _sourceHeight;

        destination[4] = _centerX;
        destination[5] = _centerY;
        destination[6] = _focal;
        destination[7] = _thetaMax;

        destination[8] = _ellipticity;
        destination[9] = _cosRoll;
        destination[10] = _sinRoll;
        destination[11] = _mirrored;

        destination[12] = _tanHalfW;
        destination[13] = _tanHalfH;
        destination[14] = _panoramaHalfSpan;
        destination[15] = _yaw;

        destination[16] = _isPanorama;
        destination[17] = _projection;
        destination[18] = _lodBias;
        destination[19] = 0;

        destination[20] = _r00; destination[21] = _r01; destination[22] = _r02;
        destination[23] = 0;
        destination[24] = _r10; destination[25] = _r11; destination[26] = _r12;
        destination[27] = 0;
        destination[28] = _r20; destination[29] = _r21; destination[30] = _r22;
        destination[31] = 0;

        destination[32] = _yScale;
        destination[33] = _yOffset;
        destination[34] = _rv;
        destination[35] = _gu;

        destination[36] = _gv;
        destination[37] = _bu;
        destination[38] = 0;
        destination[39] = 0;

        destination[40] = _outsideR;
        destination[41] = _outsideG;
        destination[42] = _outsideB;
        destination[43] = _outsideA;
    }

    /// <summary>The packed buffer as a new array. For tests and one-off uploads.</summary>
    public float[] ToFloats()
    {
        var buffer = new float[FloatCount];
        WriteTo(buffer);
        return buffer;
    }

    // ---- The transcription -------------------------------------------------------------
    //
    // Everything below is the C# twin of Dewarp.hlsl. It reads nothing but the fields above, in
    // the same order and the same shape as the shader, so that a test can hold it against
    // DewarpGeometry. Keep the two in step: an edit here without the matching edit there — or the
    // other way round — is exactly the drift this type exists to prevent.

    /// <summary>
    /// The direction an output pixel looks along, in the lens frame — the shader's
    /// <c>RayFor</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="px"/> and <paramref name="py"/> are pixel indices, so the half-pixel to
    /// the centre is added here. In HLSL that addition is already done: <c>SV_Position.xy</c>
    /// arrives at the pixel centre. That is the one place the two texts legitimately differ,
    /// which is why it is called out rather than left to be noticed.
    /// </remarks>
    public (double X, double Y, double Z) RayFor(double px, double py)
    {
        double halfW = _outputWidth / 2.0;
        double halfH = _outputHeight / 2.0;
        double ndcX = (px + 0.5 - halfW) / halfW;
        double ndcY = (py + 0.5 - halfH) / halfH;
        if (_mirrored != 0)
            ndcX = -ndcX;

        if (_isPanorama != 0)
        {
            double phi = _yaw + ndcX * _panoramaHalfSpan;
            double theta = _thetaMax * (1 - (py + 0.5) / _outputHeight);
            double sinT = Math.Sin(theta);
            return (Math.Cos(phi) * sinT, Math.Sin(phi) * sinT, Math.Cos(theta));
        }

        double cx = ndcX * _tanHalfW;
        double cy = ndcY * _tanHalfH;
        double len = Math.Sqrt(cx * cx + cy * cy + 1);
        double dx = cx / len, dy = cy / len, dz = 1 / len;
        return (_r00 * dx + _r01 * dy + _r02 * dz,
                _r10 * dx + _r11 * dy + _r12 * dz,
                _r20 * dx + _r21 * dy + _r22 * dz);
    }

    /// <summary>
    /// The source pixel a lens-frame direction lands on, or null when the lens never saw it —
    /// the shader's <c>SourceForRay</c>.
    /// </summary>
    public (double X, double Y)? SourceForRay(double dx, double dy, double dz)
    {
        double hyp = Math.Sqrt(dx * dx + dy * dy);
        double theta = Math.Atan2(hyp, dz);
        if (!(theta <= _thetaMax))
            return null;

        double rOverF = RadiusOverFocal(theta);
        if (double.IsNaN(rOverF) || !double.IsFinite(_focal))
            return null;
        double r = _focal * rOverF;

        double ux = hyp > 0 ? dx / hyp : 0;
        double uy = hyp > 0 ? dy / hyp : 0;
        double u = r * ux;
        double v = r * uy;
        double ru = u * _cosRoll - v * _sinRoll;
        double rv = u * _sinRoll + v * _cosRoll;
        return (_centerX + ru, _centerY + rv * _ellipticity);
    }

    /// <summary>
    /// The source pixel an output pixel samples, or null when its ray leaves the image circle.
    /// The transcription the parity test holds against
    /// <see cref="DewarpGeometry.SourceFor(double, double)"/>.
    /// </summary>
    public (double X, double Y)? SourceFor(double px, double py)
    {
        var (dx, dy, dz) = RayFor(px, py);
        return SourceForRay(dx, dy, dz);
    }

    /// <summary>
    /// The mip level to sample an output pixel at: the base-2 log of how many source pixels one
    /// step across the pane covers, plus the bias.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the quality argument for the GPU path, not just the speed one.</b> The CPU
    /// renderer picks one <see cref="DewarpMap.MipLevel"/> for the whole pane out of a mean
    /// minification, which is the best a single pre-halved buffer can do — but a dewarped pane
    /// does not minify uniformly, and it varies by more than a whole level. A wide rectilinear
    /// view minifies <i>hardest in the middle</i>: its flat image plane spreads angle as
    /// <c>tan θ</c>, so a pixel near the edge covers <c>cos²θ</c> as much of the circle as one at
    /// the centre — at a 140° view that is an eight-fold difference, and measurably a 0.3-versus
    /// -1.8 spread in level. One global figure is therefore either soft at the periphery or
    /// crawling in the middle. Here the footprint is measured per pixel and the hardware fetches
    /// from the level that fits.
    /// </para>
    /// <para>
    /// <b>Three taps, rather than the derivatives the hardware would give free.</b>
    /// <c>Sample()</c> takes its level from the screen-space derivative of the coordinate it is
    /// handed, which is right in the interior and wrong on the rim: across the boundary of the
    /// image circle one pixel of the 2×2 quad is outside, its coordinate is whatever the branch
    /// left behind, and the derivative explodes — so the hardware picks the coarsest mip and the
    /// edge of the view comes out as a blurred halo one or two pixels thick. Evaluating the
    /// projection at the two neighbours explicitly costs about sixty more flops per pixel, which
    /// measures as nothing, and each of the three answers is exact. A neighbour that lands
    /// outside the circle is dropped rather than used.
    /// </para>
    /// </remarks>
    public double LodFor(double px, double py)
    {
        var here = SourceFor(px, py);
        if (here is null)
            return 0;
        double footprint = 0;
        var right = SourceFor(px + 1, py);
        if (right is not null)
            footprint = Math.Max(footprint, Distance(here.Value, right.Value));
        var down = SourceFor(px, py + 1);
        if (down is not null)
            footprint = Math.Max(footprint, Distance(here.Value, down.Value));
        if (!(footprint > 1))
            return Math.Max(0, _lodBias);
        return Math.Max(0, Math.Log2(footprint) + _lodBias);

        static double Distance((double X, double Y) a, (double X, double Y) b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    /// <summary>
    /// <see cref="LensModel.RadiusOverFocal"/> as the shader spells it: a switch on the ordinal,
    /// with the validity check written out, because HLSL has no NaN to carry it.
    /// </summary>
    private double RadiusOverFocal(double theta)
    {
        if (theta < 0)
            return double.NaN;
        return (int)_projection switch
        {
            (int)LensProjection.Equidistant =>
                theta > Math.PI ? double.NaN : theta,
            (int)LensProjection.Stereographic =>
                theta > Math.PI ? double.NaN : 2 * Math.Tan(theta / 2),
            (int)LensProjection.EquisolidAngle =>
                theta > Math.PI ? double.NaN : 2 * Math.Sin(theta / 2),
            (int)LensProjection.Orthographic =>
                theta > Math.PI / 2 ? double.NaN : Math.Sin(theta),
            _ => double.NaN,
        };
    }
}
