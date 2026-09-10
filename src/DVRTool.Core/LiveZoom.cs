namespace DVRTool.Core;

/// <summary>
/// Digital zoom on a live pane: how much of the picture is showing and where, plus the crop
/// geometry that tells LibVLC to show exactly that.
/// </summary>
/// <remarks>
/// <para>
/// This is a viewer-side crop, not an optical or a PTZ move. Nothing is asked of the
/// recorder and nothing about the stream changes — the decoded picture is simply displayed
/// from a window inside itself, scaled up to fill the pane. So the detail on offer is the
/// detail the stream already carries: zooming a sub-stream tile 4× shows sub-stream pixels
/// four times the size, which is why the grid's maximize goes to the main stream first and
/// zoom is the thing you reach for after that.
/// </para>
/// <para>
/// The state is a factor and a centre in <em>picture</em> coordinates (0–1 across the whole
/// decoded frame), never in pane pixels: a pane is resized, letterboxed and — in fullscreen —
/// a different shape entirely, while the centre of interest is a place on the camera's
/// picture and should survive all of that. The centre is clamped as it is set, so the visible
/// window can never hang off the edge of the frame and a factor of 1 always means the whole
/// picture, dead centre.
/// </para>
/// <para>
/// Pure and tested, because the interesting parts are arithmetic that is easy to get subtly
/// wrong: anchoring a zoom on the point under the cursor, a drag that has to move the picture
/// with the mouse rather than against it, and a crop rectangle LibVLC will accept.
/// </para>
/// </remarks>
public readonly record struct LiveZoom
{
    /// <summary>The whole picture. Below this there is nothing to show: it is not a shrink.</summary>
    public const double MinFactor = 1.0;

    /// <summary>
    /// The most a pane will magnify. Past about 8× a 1080p sub stream is showing 240 px of
    /// picture across a whole monitor and every notch is another mouthful of the same
    /// macroblocks, so the limit is where the detail runs out rather than where the maths does.
    /// </summary>
    public const double MaxFactor = 8.0;

    /// <summary>
    /// One wheel notch. 1.25 takes ten notches to cross the whole range, which feels like a
    /// zoom rather than a jump, and 4× — the useful "read the plate" setting — is six notches
    /// in and lands on 3.81, close enough that nobody counts.
    /// </summary>
    public const double StepPerNotch = 1.25;

    /// <summary>Fit-to-pane: the whole picture, no crop.</summary>
    public static readonly LiveZoom None = new(1.0, 0.5, 0.5);

    private LiveZoom(double factor, double centerX, double centerY)
    {
        Factor = factor;
        CenterX = centerX;
        CenterY = centerY;
    }

    /// <summary>How much the visible part of the picture is magnified; 1 is the whole frame.</summary>
    public double Factor { get; }

    /// <summary>Centre of the visible window, 0–1 across the decoded picture.</summary>
    public double CenterX { get; }

    /// <inheritdoc cref="CenterX"/>
    public double CenterY { get; }

    /// <summary>Whether anything is actually cropped. A hair of tolerance, because 1.0000001× is 1×.</summary>
    public bool IsZoomed => Factor > MinFactor + 1e-6;

    /// <summary>The fraction of the picture's width (and height) on screen: 1 / <see cref="Factor"/>.</summary>
    public double VisibleFraction => 1.0 / Factor;

    /// <summary>
    /// A zoom at a factor and centre, clamped: the factor into range and the centre so far in
    /// that the visible window stays wholly inside the picture.
    /// </summary>
    public static LiveZoom At(double factor, double centerX, double centerY)
    {
        factor = double.IsNaN(factor) ? MinFactor : Math.Clamp(factor, MinFactor, MaxFactor);
        double half = 0.5 / factor;
        return new(factor, Clamp(centerX, half), Clamp(centerY, half));

        static double Clamp(double value, double half) =>
            double.IsNaN(value) ? 0.5 : Math.Clamp(value, half, 1.0 - half);
    }

    /// <summary>
    /// Wheel notches at a point on the visible picture — <paramref name="u"/> and
    /// <paramref name="v"/> being 0–1 across what is currently on screen, as
    /// <see cref="Pick"/> reports it.
    /// </summary>
    /// <remarks>
    /// The point under the cursor stays under the cursor, which is what makes a wheel zoom
    /// feel like a magnifier rather than a slider: you put the pointer on the door handle and
    /// scroll, instead of zooming the middle and then hunting for the handle. Near an edge the
    /// clamp takes over and the picture slides as far as it can, exactly as every map does.
    /// </remarks>
    public LiveZoom StepAt(int notches, double u, double v)
    {
        if (notches == 0)
            return this;
        double factor = Math.Clamp(Factor * Math.Pow(StepPerNotch, notches), MinFactor, MaxFactor);
        u = double.IsNaN(u) ? 0.5 : Math.Clamp(u, 0.0, 1.0);
        v = double.IsNaN(v) ? 0.5 : Math.Clamp(v, 0.0, 1.0);

        // The picture point under the cursor now, and where the centre has to be for that
        // same point to be under the cursor at the new factor.
        double span = VisibleFraction;
        double pictureX = CenterX + (u - 0.5) * span;
        double pictureY = CenterY + (v - 0.5) * span;
        double newSpan = 1.0 / factor;
        return At(factor, pictureX - (u - 0.5) * newSpan, pictureY - (v - 0.5) * newSpan);
    }

    /// <summary>
    /// A drag, in fractions of the visible picture: the picture follows the mouse, so dragging
    /// right moves the window left. Does nothing at 1×, where there is nowhere to pan to.
    /// </summary>
    public LiveZoom PanBy(double du, double dv)
    {
        if (!IsZoomed)
            return this;
        if (double.IsNaN(du) || double.IsNaN(dv))
            return this;
        double span = VisibleFraction;
        return At(Factor, CenterX - du * span, CenterY - dv * span);
    }

    /// <summary>
    /// The crop LibVLC should apply to a picture of this size, or null for "no crop" —
    /// which is how the setting is cleared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four numbers are <c>left+top+right+bottom</c> <em>borders</em>, one of the three
    /// forms VLC's crop parser accepts, and the only one that behaves. The documented way to
    /// name a window — <c>WxH+X+Y</c> — is parsed by libvlc 3.0.21 and then handed to the
    /// display as though it were the border form, so "640x360+320+180" on a 1280×720 picture
    /// shows a 320×180 region at (320,180) instead of a 640×360 one, and any real zoom
    /// (where the offsets exceed the leftover margins) computes a negative size and paints
    /// the pane black. Measured against the shipped LibVLC with <c>--verbose=3</c>: the
    /// vout's own <c>CROPPED … of (320,180), vsz 320x180</c> line is the proof, and
    /// <c>320+180+320+180</c> is what makes it read <c>vsz 640x360</c>. So borders it is.
    /// </para>
    /// <para>
    /// Everything is rounded to an even number of pixels: the crop lands on a
    /// chroma-subsampled plane, so odd sizes and offsets are the vout's problem to round, and
    /// rounding them here is how the geometry that goes out matches the picture that comes
    /// back. The window keeps the picture's aspect ratio, so the pane's letterboxing does not
    /// change as it zooms.
    /// </para>
    /// </remarks>
    public string? CropGeometry(int pictureWidth, int pictureHeight)
    {
        if (!IsZoomed || pictureWidth <= 0 || pictureHeight <= 0)
            return null;
        int width = Even(Math.Round(pictureWidth * VisibleFraction), pictureWidth);
        int height = Even(Math.Round(pictureHeight * VisibleFraction), pictureHeight);
        int left = Offset(CenterX, pictureWidth, width);
        int top = Offset(CenterY, pictureHeight, height);
        return $"{left}+{top}+{pictureWidth - left - width}+{pictureHeight - top - height}";

        static int Even(double value, int max)
        {
            int size = (int)Math.Clamp(value, 2, max);
            return size - (size % 2);
        }

        static int Offset(double center, int picture, int window)
        {
            int offset = (int)Math.Round((center - 0.5) * picture + (picture - window) / 2.0);
            offset = Math.Clamp(offset, 0, picture - window);
            return offset - (offset % 2);
        }
    }

    /// <summary>
    /// The zoom at which one picture pixel is one screen pixel — "1:1", the pixel-peeper's
    /// setting — centred where the zoom is centred now, or null when the pane already shows
    /// the picture at or above its native size and there is nothing to magnify.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pane draws the whole picture at <see cref="Fit"/>'s size, in device-independent
    /// units; multiplied by <paramref name="dpiScale"/> that is how many screen pixels the
    /// picture's width is spread over. A factor of <c>f</c> spreads <c>1/f</c> of the picture
    /// over the same screen pixels, so one picture pixel is one screen pixel when
    /// <c>f = pictureWidth / fittedScreenWidth</c>. Width and height agree because the fit
    /// keeps the picture's aspect ratio.
    /// </para>
    /// <para>
    /// Null rather than 1× for the small-picture case, so the caller can say why nothing
    /// happened: a 704×480 sub stream in a full-screen pane is already bigger than life and a
    /// crop cannot shrink it. The factor is clamped to <see cref="MaxFactor"/> like any other,
    /// and <see cref="IsOneToOne"/> tells whether it made it — a 4K picture in a 16-up tile
    /// wants 11× and does not.
    /// </para>
    /// </remarks>
    public LiveZoom? OneToOne(int pictureWidth, int pictureHeight,
        double paneWidth, double paneHeight, double dpiScale = 1.0)
    {
        if (pictureWidth <= 0 || pictureHeight <= 0 || paneWidth <= 0 || paneHeight <= 0)
            return null;
        if (double.IsNaN(dpiScale) || dpiScale <= 0)
            dpiScale = 1.0;
        var (width, _) = Fit(paneWidth, paneHeight, (double)pictureWidth / pictureHeight);
        double factor = pictureWidth / (width * dpiScale);
        if (factor <= MinFactor + 1e-6)
            return null;
        return At(factor, CenterX, CenterY);
    }

    /// <summary>
    /// Whether this zoom is the one <see cref="OneToOne"/> would compute for these sizes —
    /// within a percent, since a pane's width is a real number and the crop is whole pixels.
    /// </summary>
    public bool IsOneToOne(int pictureWidth, int pictureHeight,
        double paneWidth, double paneHeight, double dpiScale = 1.0)
    {
        if (!IsZoomed || pictureWidth <= 0 || pictureHeight <= 0 || paneWidth <= 0 || paneHeight <= 0)
            return false;
        if (double.IsNaN(dpiScale) || dpiScale <= 0)
            dpiScale = 1.0;
        var (width, _) = Fit(paneWidth, paneHeight, (double)pictureWidth / pictureHeight);
        double wanted = pictureWidth / (width * dpiScale);
        return Math.Abs(Factor - wanted) <= wanted * 0.01;
    }

    /// <summary>"3.8×", or "" when the whole picture is showing.</summary>
    public string Describe() => IsZoomed ? $"{Factor:0.#}×" : "";

    /// <summary>
    /// The size the picture is drawn at inside a pane: as big as fits with its own shape kept,
    /// which is what the video output does and therefore where the black bars come from.
    /// </summary>
    public static (double Width, double Height) Fit(double paneWidth, double paneHeight,
        double pictureAspect)
    {
        if (paneWidth <= 0 || paneHeight <= 0 || pictureAspect <= 0 ||
            double.IsNaN(pictureAspect) || double.IsInfinity(pictureAspect))
            return (paneWidth, paneHeight);
        return paneWidth / paneHeight > pictureAspect
            ? (paneHeight * pictureAspect, paneHeight)   // bars left and right
            : (paneWidth, paneWidth / pictureAspect);    // bars top and bottom
    }

    /// <summary>
    /// Where a point in a video pane lands on the picture: 0–1 across the visible image, or
    /// null when the point is on the black bars beside it.
    /// </summary>
    /// <remarks>
    /// A pane almost never has the picture's shape, and the video output centres the image and
    /// letterboxes the rest. Zooming on a cursor position therefore has to undo that fit
    /// first — treating pane coordinates as picture coordinates puts the anchor in the wrong
    /// place by the width of the bars, which on a 16:9 camera in a 4:3 tile is a sixth of the
    /// pane. Returning null for the bars is deliberate: a wheel notch out there has no anchor,
    /// and the caller zooms about the current centre instead of about a made-up point.
    /// </remarks>
    public static (double U, double V)? Pick(double x, double y,
        double paneWidth, double paneHeight, double pictureAspect)
    {
        if (paneWidth <= 0 || paneHeight <= 0 || pictureAspect <= 0 ||
            double.IsNaN(pictureAspect) || double.IsInfinity(pictureAspect))
            return null;

        var (width, height) = Fit(paneWidth, paneHeight, pictureAspect);
        double u = (x - (paneWidth - width) / 2) / width;
        double v = (y - (paneHeight - height) / 2) / height;
        return u is >= 0 and <= 1 && v is >= 0 and <= 1 ? (u, v) : null;
    }
}
