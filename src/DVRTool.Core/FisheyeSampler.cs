namespace DVRTool.Core;

/// <summary>
/// Which YUV-to-RGB matrix and range a decoded frame uses.
/// </summary>
/// <remarks>
/// Getting this wrong does not break the picture, it shifts it — and the symptom arrives as
/// "why is the dewarped view washed out compared to the normal one?", because the dewarped pane
/// sits next to a LibVLC-rendered view of the same camera that got it right. Limited range maps
/// luma 16–235 onto 0–255; full range (libav's <c>J420</c>) uses the whole byte. BT.709 is
/// correct for HD and is what these cameras emit; BT.601 is kept for standard-definition
/// material and older encoders.
/// </remarks>
public enum YuvRange
{
    Bt601Limited,
    Bt601Full,
    Bt709Limited,
    Bt709Full,
}

/// <summary>
/// The six coefficients of one YUV-to-RGB conversion, in floating point.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="DewarpSampler"/>'s inner fixed-point matrix so that the GPU shader
/// and the CPU converter read the <i>same</i> numbers rather than two hand-copied tables. A
/// dewarped pane is shown next to a LibVLC view of the same camera, and the accelerated pane is
/// shown next to the CPU one when a fallback happens — a colour difference between any two of
/// those three reads as a bug, and the cheapest way to not have one is to only write the
/// coefficients down once.
/// </para>
/// <para>
/// <c>R = Y' + Rv·Cr</c>, <c>G = Y' + Gu·Cb + Gv·Cr</c>, <c>B = Y' + Bu·Cb</c>, where
/// <c>Y' = (Y − YOffset)·YScale</c> and Cb/Cr are the chroma bytes less 128.
/// </para>
/// </remarks>
public readonly record struct YuvMatrix(
    double YScale, double YOffset, double Rv, double Gu, double Gv, double Bu)
{
    /// <summary>The coefficients for a range and primaries.</summary>
    public static YuvMatrix For(YuvRange range) => range switch
    {
        // Limited range stretches luma 16-235 to 0-255, hence the 255/219 = 1.164383 scale.
        YuvRange.Bt601Limited =>
            new YuvMatrix(1.164383, 16, 1.596027, -0.391762, -0.812968, 2.017232),
        YuvRange.Bt601Full =>
            new YuvMatrix(1.0, 0, 1.402000, -0.344136, -0.714136, 1.772000),
        YuvRange.Bt709Limited =>
            new YuvMatrix(1.164383, 16, 1.792741, -0.213249, -0.532909, 2.112402),
        _ =>
            new YuvMatrix(1.0, 0, 1.574800, -0.187324, -0.468124, 1.855600),
    };
}

/// <summary>
/// The per-frame pixel work: colour conversion over a sub-rect, box-halving for minification,
/// and the gather through a <see cref="DewarpMap"/>.
/// </summary>
/// <remarks>
/// <para>
/// The order is convert, then halve if needed, then gather — not gather straight out of the
/// planar YUV. Converting first is one sequential pass that vectorizes well, and leaves the
/// gather with a single random 4-byte read per output pixel; sampling YUV directly would mean
/// three random reads in three planes and roughly triples the cache misses.
/// </para>
/// <para>
/// <b>Everything here is parallel across rows, and that is not optional.</b> Measured on PS
/// Kia's 2560×2560 main stream into a 1600×900 pane, one whole frame — convert, halve, gather:
/// </para>
/// <list type="table">
///   <listheader><term>view</term><description>1 thread → 4 cores → 24 cores</description></listheader>
///   <item><term>zoomed in, 20°</term><description>15.0 → 4.97 → 2.15 ms  (sub-rect is 0.8 % of the frame)</description></item>
///   <item><term>mid, 60°</term><description>15.4 → 2.96 → 2.52 ms  (7.4 %)</description></item>
///   <item><term>zoomed out, 150°</term><description>57.3 → 9.87 → 5.00 ms  (59.8 %)</description></item>
///   <item><term>360° panorama</term><description>77.5 → 17.5 → 6.85 ms  (100 %, one mip level)</description></item>
/// </list>
/// <para>
/// Single-threaded, the two wide cases miss a 50 ms frame budget at 20 fps outright. Parallel,
/// the worst case costs a third of one frame on a four-core laptop — which is the measurement
/// that keeps this a CPU renderer rather than a Direct3D one. A serial fallback handles regions
/// too small to be worth the scheduling.
/// </para>
/// <para>
/// <b>Arrays rather than spans, deliberately.</b> A <see cref="Span{T}"/> cannot be captured by
/// a parallel loop body, and this codebase does not use <c>unsafe</c> — the two P/Invoke
/// projects both set <c>AllowUnsafeBlocks</c> to false on purpose and marshal explicitly
/// instead. Arrays keep that stance and still cost nothing at the boundary: a frame buffer
/// allocated with <c>GC.AllocateArray&lt;byte&gt;(n, pinned: true)</c> lives on the pinned
/// object heap, so it has a stable address to hand a native decoder <i>and</i> is directly
/// readable here with no copy.
/// </para>
/// </remarks>
public static class DewarpSampler
{
    /// <summary>Fully transparent: an option for output outside the image circle.</summary>
    public const uint Transparent = 0x00000000;

    /// <summary>Opaque black, the sensible default for the area outside the circle.</summary>
    public const uint OpaqueBlack = 0xFF000000;

    /// <summary>
    /// Below this many pixels a region is converted or gathered on one thread: the scheduling
    /// costs more than the work saved. Roughly the point where a deeply zoomed-in view's
    /// sub-rect stops being worth splitting.
    /// </summary>
    private const int ParallelThreshold = 32 * 1024;

    /// <summary>
    /// Converts a sub-rect of an I420 (planar 4:2:0) frame into BGRA.
    /// </summary>
    /// <param name="rect">
    /// The region of the <i>source frame</i> to convert, in absolute frame coordinates — normally
    /// <see cref="DewarpMap.Bounds"/>. The destination holds just this region.
    /// </param>
    /// <remarks>
    /// Chroma is indexed by <b>absolute</b> frame coordinate, not by position within the rect.
    /// With 4:2:0 subsampling one chroma sample covers a 2×2 luma block, so a rect starting at an
    /// odd X or Y begins mid-block: dividing the rect-relative coordinate instead lands half a
    /// chroma sample off and tints the edge of the view. That is the trap this signature exists
    /// to make hard to fall into.
    /// </remarks>
    public static void ConvertI420ToBgra(
        byte[] yPlane, int yPitch,
        byte[] uPlane, byte[] vPlane, int uvPitch,
        SourceRect rect, YuvRange range,
        uint[] dest, int destStride)
    {
        ArgumentNullException.ThrowIfNull(yPlane);
        ArgumentNullException.ThrowIfNull(uPlane);
        ArgumentNullException.ThrowIfNull(vPlane);
        ArgumentNullException.ThrowIfNull(dest);
        if (rect.IsEmpty)
            return;
        var m = Matrix.For(range);
        int width = rect.Width;
        int rectX = rect.X, rectY = rect.Y;

        void Row(int row)
        {
            int absY = rectY + row;
            int yRow = absY * yPitch;
            int cRow = absY / 2 * uvPitch;
            int destRow = row * destStride;
            for (int col = 0; col < width; col++)
            {
                int absX = rectX + col;
                int c = cRow + absX / 2;
                dest[destRow + col] = m.ToBgra(yPlane[yRow + absX], uPlane[c], vPlane[c]);
            }
        }

        RunRows(rect.Height, rect.PixelCount, Row);
    }

    /// <summary>
    /// Converts a sub-rect of an NV12 (planar luma, interleaved chroma 4:2:0) frame into BGRA.
    /// </summary>
    /// <remarks>
    /// NV12 is what a hardware decoder's readback produces naturally, so it is worth accepting
    /// rather than forcing a decoder to convert. The chroma plane holds U and V interleaved, so a
    /// chroma column is two bytes wide; the absolute-coordinate rule from
    /// <see cref="ConvertI420ToBgra"/> applies identically.
    /// </remarks>
    public static void ConvertNv12ToBgra(
        byte[] yPlane, int yPitch,
        byte[] uvPlane, int uvPitch,
        SourceRect rect, YuvRange range,
        uint[] dest, int destStride)
    {
        ArgumentNullException.ThrowIfNull(yPlane);
        ArgumentNullException.ThrowIfNull(uvPlane);
        ArgumentNullException.ThrowIfNull(dest);
        if (rect.IsEmpty)
            return;
        var m = Matrix.For(range);
        int width = rect.Width;
        int rectX = rect.X, rectY = rect.Y;

        void Row(int row)
        {
            int absY = rectY + row;
            int yRow = absY * yPitch;
            int cRow = absY / 2 * uvPitch;
            int destRow = row * destStride;
            for (int col = 0; col < width; col++)
            {
                int absX = rectX + col;
                int c = cRow + absX / 2 * 2;
                dest[destRow + col] = m.ToBgra(
                    yPlane[yRow + absX], uvPlane[c], uvPlane[c + 1]);
            }
        }

        RunRows(rect.Height, rect.PixelCount, Row);
    }

    /// <summary>
    /// Averages a BGRA image down by two in each direction, into an image
    /// <c>ceil(w/2) × ceil(h/2)</c>.
    /// </summary>
    /// <remarks>
    /// One level of the mip chain a zoomed-out view needs. An odd final column or row has no
    /// partner to average with and is taken as-is rather than reading past the end.
    /// </remarks>
    public static void BoxHalve(uint[] source, int sourceWidth, int sourceHeight,
        int sourceStride, uint[] dest, int destStride)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dest);
        int halfWidth = (sourceWidth + 1) / 2;
        int halfHeight = (sourceHeight + 1) / 2;
        if (halfWidth <= 0 || halfHeight <= 0)
            return;

        void Row(int row)
        {
            int y0 = row * 2;
            int y1 = Math.Min(y0 + 1, sourceHeight - 1);
            int r0 = y0 * sourceStride;
            int r1 = y1 * sourceStride;
            int destRow = row * destStride;
            for (int col = 0; col < halfWidth; col++)
            {
                int x0 = col * 2;
                int x1 = Math.Min(x0 + 1, sourceWidth - 1);
                dest[destRow + col] = Average(
                    source[r0 + x0], source[r0 + x1], source[r1 + x0], source[r1 + x1]);
            }
        }

        RunRows(halfHeight, (long)halfWidth * halfHeight, Row);
    }

    /// <summary>
    /// Gathers a dewarped pane out of a converted BGRA source through a
    /// <see cref="DewarpMap"/>.
    /// </summary>
    /// <param name="sourceOrigin">
    /// Where <paramref name="source"/> sits in the frame — <see cref="DewarpMap.Bounds"/>'s
    /// corner, in full-resolution frame coordinates even when <paramref name="mipShift"/> is
    /// non-zero.
    /// </param>
    /// <param name="mipShift">
    /// How many times <paramref name="source"/> has been box-halved, from
    /// <see cref="DewarpMap.MipLevel"/>. The table's coordinates are scaled down to match.
    /// </param>
    /// <param name="bilinear">
    /// Blend the four neighbouring source pixels. Worth it when magnifying; when minifying, the
    /// mip level does the real work and this only smooths what is left.
    /// </param>
    public static void Sample(
        uint[] source, int sourceWidth, int sourceHeight, int sourceStride,
        SourceRect sourceOrigin, int mipShift,
        DewarpMap map, uint[] dest, int destStride,
        bool bilinear = true, uint outsideColor = OpaqueBlack)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(dest);

        // DewarpMap exposes its tables as spans, which a parallel body cannot capture; the
        // backing arrays can be.
        var (u, v) = map.Tables;
        int width = map.OutputWidth;
        // The table is in absolute full-resolution frame coordinates; the buffer starts at the
        // bounding box's corner and may have been halved. Shifting the origin by the same amount
        // as the coordinates keeps the two in step.
        int originX = sourceOrigin.X << DewarpMap.FractionalBits >> mipShift;
        int originY = sourceOrigin.Y << DewarpMap.FractionalBits >> mipShift;

        void Row(int row)
        {
            int mapRow = row * width;
            int destRow = row * destStride;
            for (int col = 0; col < width; col++)
            {
                int su = u[mapRow + col];
                if (su == DewarpMap.Outside)
                {
                    dest[destRow + col] = outsideColor;
                    continue;
                }
                // Arithmetic shift, so this floors correctly left of the origin.
                int fx = (su >> mipShift) - originX;
                int fy = (v[mapRow + col] >> mipShift) - originY;
                dest[destRow + col] = bilinear
                    ? Bilinear(source, sourceWidth, sourceHeight, sourceStride, fx, fy, outsideColor)
                    : Nearest(source, sourceWidth, sourceHeight, sourceStride, fx, fy, outsideColor);
            }
        }

        RunRows(map.OutputHeight, (long)width * map.OutputHeight, Row);
    }

    /// <summary>
    /// Runs a per-row body across every row, in parallel when there is enough work to pay for
    /// the scheduling.
    /// </summary>
    private static void RunRows(int rows, long pixels, Action<int> row)
    {
        if (rows <= 1 || pixels < ParallelThreshold)
        {
            for (int i = 0; i < rows; i++)
                row(i);
            return;
        }
        Parallel.For(0, rows, row);
    }

    private static uint Nearest(uint[] source, int width, int height, int stride,
        int fx, int fy, uint outside)
    {
        // Rounded to the nearest pixel centre rather than truncated, so nearest sampling of an
        // identity table is the identity rather than shifted half a pixel.
        int x = (fx + DewarpMap.One / 2) >> DewarpMap.FractionalBits;
        int y = (fy + DewarpMap.One / 2) >> DewarpMap.FractionalBits;
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            return outside;
        return source[y * stride + x];
    }

    private static uint Bilinear(uint[] source, int width, int height, int stride,
        int fx, int fy, uint outside)
    {
        int x0 = fx >> DewarpMap.FractionalBits;
        int y0 = fy >> DewarpMap.FractionalBits;
        if ((uint)x0 >= (uint)width || (uint)y0 >= (uint)height)
            return outside;

        int tx = fx & (DewarpMap.One - 1);
        int ty = fy & (DewarpMap.One - 1);
        // The last row and column have no neighbour to blend with; repeating the edge pixel
        // there is what keeps an identity table byte-exact instead of fading at two edges.
        int x1 = x0 + 1 < width ? x0 + 1 : x0;
        int y1 = y0 + 1 < height ? y0 + 1 : y0;

        int r0 = y0 * stride;
        int r1 = y1 * stride;
        return Blend(source[r0 + x0], source[r0 + x1], source[r1 + x0], source[r1 + x1], tx, ty);
    }

    /// <summary>Bilinear blend of four pixels, one channel at a time.</summary>
    /// <remarks>
    /// No clamping: a weighted average of four values already in 0–255 with weights summing to
    /// one is in 0–255 by construction. The clamp this used to carry cost four
    /// <see cref="Math.Clamp(int,int,int)"/> calls per output pixel to defend against nothing.
    /// </remarks>
    private static uint Blend(uint p00, uint p10, uint p01, uint p11, int tx, int ty)
    {
        // Weights in 8 bits: enough for byte output, and keeps every product inside 32 bits.
        int wx = tx >> (DewarpMap.FractionalBits - 8);
        int wy = ty >> (DewarpMap.FractionalBits - 8);
        return Channel(p00, p10, p01, p11, 0, wx, wy)
            | Channel(p00, p10, p01, p11, 8, wx, wy)
            | Channel(p00, p10, p01, p11, 16, wx, wy)
            | Channel(p00, p10, p01, p11, 24, wx, wy);
    }

    private static uint Channel(uint p00, uint p10, uint p01, uint p11, int shift, int wx, int wy)
    {
        int a = (int)(p00 >> shift) & 0xFF;
        int b = (int)(p10 >> shift) & 0xFF;
        int c = (int)(p01 >> shift) & 0xFF;
        int d = (int)(p11 >> shift) & 0xFF;
        int top = a + ((b - a) * wx >> 8);
        int bottom = c + ((d - c) * wx >> 8);
        return (uint)(top + ((bottom - top) * wy >> 8)) << shift;
    }

    private static uint Average(uint a, uint b, uint c, uint d) =>
        AverageChannel(a, b, c, d, 0)
        | AverageChannel(a, b, c, d, 8)
        | AverageChannel(a, b, c, d, 16)
        | AverageChannel(a, b, c, d, 24);

    private static uint AverageChannel(uint a, uint b, uint c, uint d, int shift)
    {
        int sum = (int)((a >> shift) & 0xFF) + (int)((b >> shift) & 0xFF)
            + (int)((c >> shift) & 0xFF) + (int)((d >> shift) & 0xFF);
        return (uint)((sum + 2) / 4) << shift;
    }

    /// <summary>
    /// One YUV-to-BGRA matrix in 16-bit fixed point. Integer throughout: this runs once per
    /// source pixel of the sub-rect, up to 6.6 million of them per frame at 2560×2560.
    /// </summary>
    private readonly struct Matrix
    {
        private const int Shift = 16;
        private const int Half = 1 << (Shift - 1);

        private readonly int _yScale;
        private readonly int _yOffset;
        private readonly int _rv;
        private readonly int _gu;
        private readonly int _gv;
        private readonly int _bu;

        private Matrix(in YuvMatrix m)
        {
            _yScale = (int)Math.Round(m.YScale * (1 << Shift));
            _yOffset = (int)Math.Round(m.YOffset);
            _rv = (int)Math.Round(m.Rv * (1 << Shift));
            _gu = (int)Math.Round(m.Gu * (1 << Shift));
            _gv = (int)Math.Round(m.Gv * (1 << Shift));
            _bu = (int)Math.Round(m.Bu * (1 << Shift));
        }

        public static Matrix For(YuvRange range) => new(YuvMatrix.For(range));

        public uint ToBgra(byte y, byte u, byte v)
        {
            int luma = (y - _yOffset) * _yScale;
            int cb = u - 128;
            int cr = v - 128;
            int r = (luma + _rv * cr + Half) >> Shift;
            int g = (luma + _gu * cb + _gv * cr + Half) >> Shift;
            int b = (luma + _bu * cb + Half) >> Shift;
            return 0xFF000000u
                | (uint)Clamp(r) << 16
                | (uint)Clamp(g) << 8
                | (uint)Clamp(b);
        }

        private static int Clamp(int value) => value < 0 ? 0 : value > 255 ? 255 : value;
    }
}
