namespace DVRTool.Core;

/// <summary>A rectangle of source pixels.</summary>
public readonly record struct SourceRect(int X, int Y, int Width, int Height)
{
    public static SourceRect Empty => new(0, 0, 0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public long PixelCount => (long)Math.Max(0, Width) * Math.Max(0, Height);
}

/// <summary>
/// A frozen output-to-source table for one view: for every pixel of the pane, which source
/// pixel it samples, plus the region of source it needs and how hard it is minifying.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is the performance design.</b> The mapping changes only when the operator pans,
/// zooms or recalibrates — never from frame to frame — so the trigonometry is paid once and
/// every frame after it is a memory walk. Coordinates are 16.16 fixed point, which is enough
/// sub-pixel precision for bilinear sampling out of a 2560-pixel source and keeps the inner
/// loop in integers.
/// </para>
/// <para>
/// <b><see cref="Bounds"/> is what makes 2560×2560 affordable.</b> Converting a whole 6.6 Mpx
/// I420 frame to BGRA every frame is the dominant cost at that size, and a zoomed-in view
/// touches maybe 15 % of the circle. The bounding box of the table's own in-bounds samples is
/// free to compute while building it, so the converter can be handed a sub-rect instead of the
/// frame.
/// </para>
/// <para>
/// <b><see cref="MinificationRatio"/> is what keeps a zoomed-out view from shimmering.</b> At a
/// full 180° a 1.4 Mpx pane gathers from a 6.6 Mpx circle — roughly four source pixels skipped
/// per output pixel — and point-sampling that crawls and sparkles on fine detail whenever
/// anything moves. A GPU gets the fix free from hardware trilinear mipmapping; on the CPU the
/// source has to be box-halved first, and <see cref="MipLevel"/> says how many times.
/// </para>
/// <para>
/// Pure, allocating only its two tables, so it is testable with no device, GPU or window.
/// </para>
/// </remarks>
public sealed class DewarpMap
{
    /// <summary>
    /// Marks an output pixel whose ray leaves the image circle. A sentinel rather than a clamped
    /// coordinate, all the way up from <see cref="LensModel.ThetaFromRadiusOverFocal"/>: clamping
    /// is what smears a dewarped edge along the rim or wraps it to the far side of the circle.
    /// </summary>
    public const int Outside = int.MinValue;

    /// <summary>Fractional bits in the fixed-point coordinates.</summary>
    public const int FractionalBits = 16;

    /// <summary>One whole pixel in the fixed-point coordinates.</summary>
    public const int One = 1 << FractionalBits;

    private readonly int[] _u;
    private readonly int[] _v;

    private DewarpMap(int outputWidth, int outputHeight, int[] u, int[] v,
        int insidePixelCount, SourceRect bounds, double minificationRatio,
        int sourceWidth, int sourceHeight)
    {
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        _u = u;
        _v = v;
        InsidePixelCount = insidePixelCount;
        Bounds = bounds;
        MinificationRatio = minificationRatio;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    /// <summary>Pane width in pixels.</summary>
    public int OutputWidth { get; }

    /// <summary>Pane height in pixels.</summary>
    public int OutputHeight { get; }

    /// <summary>Source frame width the table was built against.</summary>
    public int SourceWidth { get; }

    /// <summary>Source frame height the table was built against.</summary>
    public int SourceHeight { get; }

    /// <summary>
    /// Source X per output pixel, row-major, 16.16 fixed point, or <see cref="Outside"/>.
    /// </summary>
    public ReadOnlySpan<int> U => _u;

    /// <summary>
    /// Source Y per output pixel, row-major, 16.16 fixed point, or <see cref="Outside"/>.
    /// </summary>
    public ReadOnlySpan<int> V => _v;

    /// <summary>
    /// The backing tables, for a caller that needs to read them from a parallel loop — a
    /// <see cref="ReadOnlySpan{T}"/> cannot be captured by a lambda. Do not write to them; the
    /// arrays are the table's own state, and <see cref="Bounds"/> and
    /// <see cref="MinificationRatio"/> were computed from their current contents.
    /// </summary>
    internal (int[] U, int[] V) Tables => (_u, _v);

    /// <summary>How many output pixels actually land inside the image circle.</summary>
    public int InsidePixelCount { get; }

    /// <summary>
    /// The tight bounding box of every source pixel this table reads, already widened by one for
    /// the bilinear neighbour and clipped to the frame. Empty when nothing is inside the circle.
    /// </summary>
    public SourceRect Bounds { get; }

    /// <summary>
    /// Mean source pixels traversed per output pixel. Above 1 the view is minifying and needs
    /// <see cref="MipLevel"/> applied; at or below 1 it is magnifying, which is the zoomed-in
    /// case and needs no filtering.
    /// </summary>
    public double MinificationRatio { get; }

    /// <summary>
    /// How many times to box-halve the source before gathering, from
    /// <see cref="MinificationRatio"/>.
    /// </summary>
    /// <remarks>
    /// The first threshold sits at 1.6 rather than 2 because point-sampling starts to visibly
    /// crawl well before it is skipping a whole pixel, and halving one step early costs a
    /// fraction of a millisecond on a sub-rect. Capped at 3 levels: past a 12× reduction the
    /// pane is a thumbnail and further halving buys nothing.
    /// </remarks>
    public int MipLevel
    {
        get
        {
            if (!(MinificationRatio >= 1.6))
                return 0;
            int level = 1 + (int)Math.Floor(Math.Log2(MinificationRatio / 1.6));
            return Math.Clamp(level, 0, 3);
        }
    }

    /// <summary>True when no output pixel lands inside the image circle.</summary>
    public bool IsEmpty => InsidePixelCount == 0;

    /// <summary>
    /// Builds the table by evaluating the projection at every pixel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Parallel across rows, because this runs on the interaction path — it is rebuilt on every
    /// drag frame and every scroll notch. Measured on a 24-core workstation against Site C's
    /// 2560×2560: <b>6.2 ms at 1600×900, 8.9 ms at 1920×1080, 15.1 ms at 2560×1440</b>. It
    /// scales with core count, so expect roughly 19 ms at 1600×900 on eight cores — still inside
    /// a drag, and a drag repaints from the frame already in hand, so a missed tick costs
    /// smoothness rather than a blank pane.
    /// </para>
    /// <para>
    /// <b>There is deliberately no coarse-grid variant.</b> One was written and measured, and it
    /// was both slower and less accurate: interpolating between exact nodes every 16 pixels came
    /// out <i>6.6 source pixels</i> off in the worst corner of the parameter space (a narrow
    /// 60° lens under a wide 140° view, where the perspective stretch is most nonlinear), which
    /// is far outside bilinear sampling's own error and would be visible. If a slow machine ever
    /// does need one, it wants a cached node grid and a spacing driven by measured local
    /// curvature — not a fixed 16 — and it should be built against a real measurement rather
    /// than added speculatively.
    /// </para>
    /// </remarks>
    public static DewarpMap Build(in FisheyeCalibration calibration, in DewarpView view,
        int outputWidth, int outputHeight, int sourceWidth, int sourceHeight) =>
        BuildCore(calibration, view, outputWidth, outputHeight, sourceWidth, sourceHeight);

    /// <summary>
    /// Builds a table from explicit source coordinates: <paramref name="coordinate"/> is asked
    /// for the source pixel each output pixel samples, or null for outside the circle.
    /// </summary>
    /// <remarks>
    /// The escape hatch for anything that is not this projection — and what lets
    /// <see cref="DewarpSampler.Sample"/> be tested against tables with known contents, rather
    /// than only through whatever the geometry happens to produce. It shares
    /// <see cref="Build"/>'s bounds and ratio arithmetic, so those get exercised here too.
    /// One delegate call per pixel, so this is not the path for a live view.
    /// </remarks>
    public static DewarpMap FromSourceCoordinates(int outputWidth, int outputHeight,
        int sourceWidth, int sourceHeight, Func<int, int, (double X, double Y)?> coordinate)
    {
        ArgumentNullException.ThrowIfNull(coordinate);
        int w = Math.Max(1, outputWidth);
        int h = Math.Max(1, outputHeight);
        var u = new int[w * h];
        var v = new int[u.Length];
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                var source = coordinate(col, row);
                int index = row * w + col;
                if (source is null ||
                    double.IsNaN(source.Value.X) || double.IsNaN(source.Value.Y))
                {
                    u[index] = Outside;
                    v[index] = Outside;
                    continue;
                }
                u[index] = ToFixed(source.Value.X);
                v[index] = ToFixed(source.Value.Y);
            }
        }
        return Reduce(w, h, u, v, Math.Max(1, sourceWidth), Math.Max(1, sourceHeight));
    }

    /// <summary>
    /// Walks a filled table to work out how much of it is inside the circle, which source
    /// region it needs, and how hard it is minifying. Shared so every table — projected or
    /// supplied — reports these the same way.
    /// </summary>
    private static DewarpMap Reduce(int w, int h, int[] u, int[] v, int sw, int sh)
    {
        int inside = 0, minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;
        double stepSum = 0;
        int stepCount = 0;

        for (int row = 0; row < h; row++)
        {
            bool havePrevious = false;
            double previousX = 0, previousY = 0;
            for (int col = 0; col < w; col++)
            {
                int index = row * w + col;
                if (u[index] == Outside)
                {
                    havePrevious = false;
                    continue;
                }
                inside++;
                double sx = u[index] / (double)One;
                double sy = v[index] / (double)One;

                int fx = (int)Math.Floor(sx);
                int fy = (int)Math.Floor(sy);
                if (fx < minX) minX = fx;
                if (fy < minY) minY = fy;
                if (fx > maxX) maxX = fx;
                if (fy > maxY) maxY = fy;

                // How far the source travels for one step across the pane: the minification
                // ratio, measured rather than derived, so it is right for every mode and lens.
                if (havePrevious)
                {
                    double dx = sx - previousX;
                    double dy = sy - previousY;
                    stepSum += Math.Sqrt(dx * dx + dy * dy);
                    stepCount++;
                }
                previousX = sx;
                previousY = sy;
                havePrevious = true;
            }
        }

        SourceRect bounds;
        if (inside == 0)
        {
            bounds = SourceRect.Empty;
        }
        else
        {
            // One extra pixel right and down for bilinear's neighbour, then clipped to the frame.
            int x0 = Math.Clamp(minX, 0, sw - 1);
            int y0 = Math.Clamp(minY, 0, sh - 1);
            int x1 = Math.Clamp(maxX + 1, 0, sw - 1);
            int y1 = Math.Clamp(maxY + 1, 0, sh - 1);
            bounds = new SourceRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        }

        double ratio = stepCount > 0 ? stepSum / stepCount : 1;
        if (!double.IsFinite(ratio) || ratio <= 0)
            ratio = 1;

        return new DewarpMap(w, h, u, v, inside, bounds, ratio, sw, sh);
    }

    private static DewarpMap BuildCore(in FisheyeCalibration calibration, in DewarpView view,
        int outputWidth, int outputHeight, int sourceWidth, int sourceHeight)
    {
        int w = Math.Max(1, outputWidth);
        int h = Math.Max(1, outputHeight);
        int sw = Math.Max(1, sourceWidth);
        int sh = Math.Max(1, sourceHeight);

        // The calibration describes whatever frame it was measured against; the table is being
        // built for this one. Site C's fisheyes are 2560x2560 main and 720x720 sub, so this is
        // the difference between aiming at the picture and aiming at empty space.
        var cal = calibration.ScaledTo(sw, sh).Normalized();
        var geometry = FisheyeProjection.For(cal, view, w, h);

        var u = new int[w * h];
        var v = new int[u.Length];

        // Only the fill is parallel: it is the trigonometry, and it is what makes an exact
        // 1600x900 table cost ~90 ms single-threaded on a path that rebuilds on every drag
        // frame. The reduction afterwards is a cheap serial walk over the finished table.
        Parallel.For(0, h, row =>
        {
            int offset = row * w;
            for (int col = 0; col < w; col++)
            {
                var source = geometry.SourceFor(col, row);
                if (source is null ||
                    double.IsNaN(source.Value.X) || double.IsNaN(source.Value.Y))
                {
                    u[offset + col] = Outside;
                    v[offset + col] = Outside;
                    continue;
                }
                u[offset + col] = ToFixed(source.Value.X);
                v[offset + col] = ToFixed(source.Value.Y);
            }
        });

        return Reduce(w, h, u, v, sw, sh);
    }

    private static int ToFixed(double value)
    {
        double scaled = Math.Round(value * One);
        // Clamped short of the sentinel so a wild coordinate can never be mistaken for "outside".
        if (scaled <= Outside + 1) return Outside + 1;
        if (scaled >= int.MaxValue) return int.MaxValue;
        return (int)scaled;
    }
}
