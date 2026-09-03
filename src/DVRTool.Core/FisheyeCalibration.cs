namespace DVRTool.Core;

/// <summary>Where a fisheye camera is mounted, which decides which way is up in the circle.</summary>
public enum FisheyeMount
{
    /// <summary>Looking straight down. The commonest case and the default; allows a full 360°.</summary>
    Ceiling,

    /// <summary>Looking horizontally off a wall. Limits the useful sweep to 180°.</summary>
    Wall,

    /// <summary>
    /// Looking straight up off a floor or table. Geometrically a ceiling mount mirrored, which
    /// is why it needs its own handedness flip rather than just a different default pitch.
    /// </summary>
    Floor,
}

/// <summary>
/// Everything about one fisheye channel's lens and framing that DVRTool cannot work out for
/// itself: where the image circle sits in the frame, how big it is, how round it is, which way
/// up the camera is, and which radius/angle law the glass follows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is read from the device, and that is the point.</b> Nothing on the wire says
/// "this channel is a fisheye" — not the resolution, not the codec, not the channel name. iVMS
/// gates its Dome mode on the camera advertising fisheye capability (or on a square frame), and
/// the consequence is visible on our own fleet: <b>Site H's 2592×1944 fisheye is not detected
/// as one by iVMS and therefore cannot be dewarped there at all.</b> The workaround people reach
/// for — crop a centre square and pretend — throws away the left and right of the circle and
/// still guesses at the centre.
/// </para>
/// <para>
/// Describing the circle in <i>source pixels</i> instead removes the whole problem. A circle
/// inscribed in the height of a 4:3 frame, cropped left and right, is an ordinary calibration
/// rather than a special case, so Site H is dewarpable here by the same code path that
/// handles Site C's square 2560×2560 — a camera iVMS refuses outright needs no hack. This is
/// DW Spectrum's approach and it is the reason to copy it.
/// </para>
/// <para>
/// Lengths are in the pixels of the frame the operator calibrated against, recorded in
/// <see cref="SourceWidth"/>/<see cref="SourceHeight"/> so the numbers survive a main-to-sub
/// stream switch — Site C's fisheyes are 2560×2560 on the main stream and 720×720 on the sub,
/// and a circle measured on one is off by 3.6× on the other until <see cref="ScaledTo"/>
/// rescales it. Angles are in degrees, because that is what an operator and a datasheet both
/// speak.
/// </para>
/// </remarks>
public readonly record struct FisheyeCalibration(
    LensProjection Projection,
    FisheyeMount Mount,
    double CenterX,
    double CenterY,
    double RadiusX,
    double Ellipticity,
    double RollDegrees,
    double FieldOfViewDegrees,
    int SourceWidth,
    int SourceHeight)
{
    /// <summary>
    /// Largest mounting-angle correction offered, in degrees either way. Matches DW Spectrum's
    /// ±30 range: past that the camera is not "slightly off level", it is mounted differently,
    /// and <see cref="Mount"/> is the field that should change.
    /// </summary>
    public const double MaxRollDegrees = 30;

    /// <summary>Narrowest lens field of view accepted, in degrees.</summary>
    public const double MinFieldOfViewDegrees = 1;

    /// <summary>Widest lens field of view accepted, in degrees.</summary>
    public const double MaxFieldOfViewDegrees = 360;

    /// <summary>
    /// A usable starting point for a frame of this size: a round, centred, 180° equidistant
    /// ceiling fisheye whose circle is inscribed in the shorter side.
    /// </summary>
    /// <remarks>
    /// This is right, or close enough to be worth showing, for the large majority of ceiling
    /// fisheyes, and it generalizes to both cameras on our fleet without a special case —
    /// radius 1280 centred at (1280, 1280) on Site C's 2560×2560, radius 972 centred at
    /// (1296, 972) on Site H's 2592×1944. The operator refines it; they should not have to
    /// start from nothing.
    /// </remarks>
    public static FisheyeCalibration Default(int sourceWidth, int sourceHeight)
    {
        int w = Math.Max(1, sourceWidth);
        int h = Math.Max(1, sourceHeight);
        return new FisheyeCalibration(
            LensProjection.Equidistant, FisheyeMount.Ceiling,
            CenterX: w / 2.0, CenterY: h / 2.0,
            RadiusX: Math.Min(w, h) / 2.0,
            Ellipticity: 1.0, RollDegrees: 0, FieldOfViewDegrees: 180,
            SourceWidth: w, SourceHeight: h);
    }

    /// <summary>Vertical radius of the image circle: <see cref="RadiusX"/> times the ellipticity.</summary>
    public double RadiusY => RadiusX * Ellipticity;

    /// <summary>Half the lens field of view, in radians — the incidence angle at the rim.</summary>
    public double ThetaMaxRad => FieldOfViewDegrees / 2 * Math.PI / 180;

    /// <summary>
    /// Focal length in source pixels, derived from the circle rather than configured.
    /// </summary>
    /// <remarks>
    /// An operator can see and drag a circle and can read a field of view off a datasheet;
    /// nobody knows their lens's focal length in pixels. So the circle is the input and this is
    /// the consequence: the rim of the circle is by definition where the incidence angle reaches
    /// <see cref="ThetaMaxRad"/>. For a 180° equidistant lens that makes this exactly
    /// <c>RadiusX / (pi/2)</c>, and the horizon lands precisely on the rim — the identity the
    /// tests pin, because confusing the full field of view with half of it is the single most
    /// common bug in this arithmetic.
    /// </remarks>
    public double FocalPixels
    {
        get
        {
            double rOverF = LensModel.RadiusOverFocal(Projection, ThetaMaxRad);
            return rOverF > 0 ? RadiusX / rOverF : double.NaN;
        }
    }

    /// <summary>Every field forced into its accepted range. Idempotent.</summary>
    public FisheyeCalibration Normalized()
    {
        int w = Math.Max(1, SourceWidth);
        int h = Math.Max(1, SourceHeight);
        // A circle can legitimately be larger than the frame — a full-frame fisheye whose circle
        // overflows the sensor — but never larger than a few frames, and never degenerate.
        double radius = double.IsFinite(RadiusX)
            ? Math.Clamp(RadiusX, 1, Math.Max(w, h) * 4.0)
            : Math.Min(w, h) / 2.0;
        double ellipticity = double.IsFinite(Ellipticity) ? Math.Clamp(Ellipticity, 0.1, 10) : 1.0;
        double fov = double.IsFinite(FieldOfViewDegrees)
            ? Math.Clamp(FieldOfViewDegrees, MinFieldOfViewDegrees, MaxFieldOfViewDegrees)
            : 180;
        // Orthographic cannot describe a lens wider than 180° at all (sin turns over at 90°), so
        // normalizing has to narrow the field rather than leave behind a value Validate refuses.
        if (Projection == LensProjection.Orthographic)
            fov = Math.Min(fov, 180);
        return this with
        {
            CenterX = double.IsFinite(CenterX) ? Math.Clamp(CenterX, -radius, w + radius) : w / 2.0,
            CenterY = double.IsFinite(CenterY) ? Math.Clamp(CenterY, -radius, h + radius) : h / 2.0,
            RadiusX = radius,
            Ellipticity = ellipticity,
            RollDegrees = double.IsFinite(RollDegrees)
                ? Math.Clamp(RollDegrees, -MaxRollDegrees, MaxRollDegrees)
                : 0,
            FieldOfViewDegrees = fov,
            SourceWidth = w,
            SourceHeight = h,
        };
    }

    /// <summary>
    /// Null when this calibration can be dewarped, otherwise the reason, worded for an operator.
    /// </summary>
    public string? Validate()
    {
        if (SourceWidth <= 0 || SourceHeight <= 0)
            return "The frame size is unknown, so the image circle cannot be placed.";
        if (!double.IsFinite(RadiusX) || RadiusX <= 0)
            return "The image circle has no radius — drag it over the camera's picture first.";
        if (!double.IsFinite(Ellipticity) || Ellipticity <= 0)
            return "Ellipticity must be greater than zero (1.0 is a round circle).";
        if (!double.IsFinite(RollDegrees) || Math.Abs(RollDegrees) > MaxRollDegrees)
            return $"The angle correction must be within +/-{MaxRollDegrees:0}°. Past that, " +
                   "change the mount instead.";
        if (!double.IsFinite(FieldOfViewDegrees) ||
            FieldOfViewDegrees < MinFieldOfViewDegrees ||
            FieldOfViewDegrees > MaxFieldOfViewDegrees)
            return $"The field of view must be between {MinFieldOfViewDegrees:0}° and " +
                   $"{MaxFieldOfViewDegrees:0}°.";
        if (Projection == LensProjection.Orthographic && FieldOfViewDegrees > 180)
            return "An orthographic lens cannot be wider than 180° — beyond that its radius " +
                   "stops growing with angle and the view would fold back on itself. Pick " +
                   "another lens projection.";
        if (!double.IsFinite(CenterX) || !double.IsFinite(CenterY))
            return "The image circle's centre is not a number.";
        // A circle entirely off the frame is a mis-calibration that would dewarp to solid black,
        // which reads as a broken stream rather than as a bad setting.
        if (CenterX + RadiusX <= 0 || CenterY + RadiusY <= 0 ||
            CenterX - RadiusX >= SourceWidth || CenterY - RadiusY >= SourceHeight)
            return "The image circle lies entirely outside the picture.";
        return null;
    }

    /// <summary>
    /// The same calibration expressed against a different frame size, scaling the circle with
    /// it. Angles, mount and lens law describe the installation rather than the encoding, so
    /// they are carried across untouched.
    /// </summary>
    /// <remarks>
    /// This exists because one camera serves two resolutions. Site C's fisheyes encode
    /// 2560×2560 on the main stream and 720×720 on the sub; a circle calibrated on the main
    /// stream describes the sub stream only after being scaled by 720/2560. Getting it wrong is
    /// not subtle — the dewarp aims at empty space — but it is easy to forget, because the
    /// calibration looks correct right up until someone switches streams.
    /// </remarks>
    public FisheyeCalibration ScaledTo(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || SourceWidth <= 0 || SourceHeight <= 0)
            return this;
        if (sourceWidth == SourceWidth && sourceHeight == SourceHeight)
            return this;
        double sx = (double)sourceWidth / SourceWidth;
        double sy = (double)sourceHeight / SourceHeight;
        // Ellipticity is a ratio of radii, so a non-uniform rescale changes it: a round circle in
        // a 2560×2560 frame becomes an ellipse if the sub stream is not square.
        return this with
        {
            CenterX = CenterX * sx,
            CenterY = CenterY * sy,
            RadiusX = RadiusX * sx,
            Ellipticity = Ellipticity * sy / sx,
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
        };
    }
}
