namespace DVRTool.Core;

/// <summary>What shape of dewarped picture to build out of the circle.</summary>
public enum DewarpViewMode
{
    /// <summary>
    /// A flat, perspective-correct window aimed somewhere in the circle — the pseudo-PTZ view,
    /// and the one the scroll wheel zooms. Straight lines come out straight.
    /// </summary>
    Rectilinear,

    /// <summary>A half-circle unrolled into a strip. The natural view for a wall mount.</summary>
    Panorama180,

    /// <summary>The whole circle unrolled into one strip. Ceiling and floor mounts only.</summary>
    Panorama360,

    /// <summary>
    /// Four <see cref="Rectilinear"/> windows 90° apart around the axis — iVMS's four-way
    /// e-PTZ split. A composition of the primitive, never its own projection.
    /// </summary>
    Quad,
}

/// <summary>
/// Where a dewarped view is aimed, in the lens's own frame.
/// </summary>
/// <remarks>
/// <para>
/// The parameterization is deliberately the fisheye-native one rather than a camera-style
/// yaw/pitch: <see cref="PitchDegrees"/> is the incidence angle <i>out from the optical axis</i>
/// and <see cref="YawDegrees"/> is the azimuth <i>around</i> it. So pitch 0 is the centre of the
/// circle whatever the mount, and pitch is bounded by exactly one number — the lens's half
/// field of view.
/// </para>
/// <para>
/// That choice pays for itself twice. Clamping becomes one comparison against
/// <see cref="FisheyeCalibration.ThetaMaxRad"/> instead of a per-mount rule, and
/// <see cref="DewarpViewMode.Quad"/>'s four panes come out genuinely uniform — at a constant
/// tilt, 90° apart around the axis. Under a conventional yaw-then-pitch frame they would not:
/// yaw 90 with pitch 45 sits 90° off the axis while yaw 0 with pitch 45 sits 45° off it, so the
/// four panes would look at four different elevations.
/// </para>
/// </remarks>
public readonly record struct ViewOrientation(
    double YawDegrees,
    double PitchDegrees,
    double RollDegrees)
{
    /// <summary>Down the optical axis: the centre of the image circle.</summary>
    public static ViewOrientation Center => new(0, 0, 0);

    /// <summary>Azimuth around the optical axis, in radians.</summary>
    public double YawRad => YawDegrees * Math.PI / 180;

    /// <summary>Incidence angle out from the optical axis, in radians.</summary>
    public double PitchRad => PitchDegrees * Math.PI / 180;

    /// <summary>Roll of the virtual camera about its own view direction, in radians.</summary>
    public double RollRad => RollDegrees * Math.PI / 180;

    /// <summary>
    /// Yaw wrapped into [0, 360) and pitch folded to be non-negative, so every direction has
    /// exactly one representation.
    /// </summary>
    /// <remarks>
    /// Folding matters for dragging: a drag that pulls the aim through the centre of the circle
    /// and out the other side arrives as a negative pitch, and turning that into "the same tilt,
    /// 180° around" is what makes the gesture continue smoothly instead of stopping dead at the
    /// axis.
    /// </remarks>
    public ViewOrientation Normalized()
    {
        double yaw = YawDegrees;
        double pitch = PitchDegrees;
        if (!double.IsFinite(yaw)) yaw = 0;
        if (!double.IsFinite(pitch)) pitch = 0;
        if (pitch < 0)
        {
            pitch = -pitch;
            yaw += 180;
        }
        yaw %= 360;
        if (yaw < 0) yaw += 360;
        double roll = double.IsFinite(RollDegrees) ? RollDegrees : 0;
        roll %= 360;
        if (roll <= -180) roll += 360;
        if (roll > 180) roll -= 360;
        return new ViewOrientation(yaw, pitch, roll);
    }
}

/// <summary>
/// One dewarped view: its shape, where it is aimed, and how wide it is. The aimable state a
/// scroll wheel and a mouse drag move around.
/// </summary>
/// <remarks>
/// Immutable, so the render loop can read a consistent view while the UI thread replaces it.
/// <see cref="Pan"/>, <see cref="AimedAt"/> and <see cref="Zoom"/> all return a new value and
/// all clamp against the lens, because how far a view can be aimed and how wide it can open are
/// properties of the glass, not preferences.
/// </remarks>
public readonly record struct DewarpView(
    DewarpViewMode Mode,
    ViewOrientation Orientation,
    double HorizontalFovDegrees)
{
    /// <summary>
    /// Narrowest the virtual camera opens, in degrees — the far end of the zoom.
    /// </summary>
    /// <remarks>
    /// 5° into Site C's 2560×2560 is roughly a 40-pixel-wide slice of source filling the pane,
    /// which is already well past the point of diminishing returns. Going narrower magnifies
    /// sensor noise, not detail.
    /// </remarks>
    public const double MinHorizontalFovDegrees = 5;

    /// <summary>
    /// Widest a <see cref="DewarpViewMode.Rectilinear"/> view opens, in degrees.
    /// </summary>
    /// <remarks>
    /// Capped well short of 180 because a rectilinear projection is a flat plane: its extent
    /// goes as <c>tan(fov/2)</c>, so 179° is not "slightly wider than 150°", it is 65× wider and
    /// spends every pixel on a smeared periphery. Past about 150° the panorama modes are the
    /// right answer instead, which is why the cap sits there rather than at an arbitrary number.
    /// </remarks>
    public const double MaxHorizontalFovDegrees = 150;

    /// <summary>
    /// A sensible opening view for a mount and a shape: aimed down the optical axis — the centre
    /// of the circle, which is directly below a ceiling camera and straight ahead of a wall one.
    /// </summary>
    /// <remarks>
    /// Panorama modes are self-describing, so their field of view <i>is</i> the arc they unroll.
    /// <see cref="DewarpViewMode.Quad"/> opens tilted 45° off the axis, because four panes all
    /// looking straight down the axis would be four copies of the same picture.
    /// </remarks>
    public static DewarpView DefaultFor(FisheyeMount mount, DewarpViewMode mode) => mode switch
    {
        DewarpViewMode.Panorama180 => new DewarpView(mode, ViewOrientation.Center, 180),
        DewarpViewMode.Panorama360 => new DewarpView(mode, ViewOrientation.Center, 360),
        DewarpViewMode.Quad => new DewarpView(mode, new ViewOrientation(0, 45, 0), 90),
        // A wall mount is looking into a room rather than down at a floor, so it opens wider.
        _ => new DewarpView(mode, ViewOrientation.Center,
            mount == FisheyeMount.Wall ? 110 : 90),
    };

    /// <summary>
    /// True when the scroll wheel means anything here. A panorama unrolls a fixed arc, so its
    /// width is the mode rather than a setting.
    /// </summary>
    public bool SupportsZoom =>
        Mode is DewarpViewMode.Rectilinear or DewarpViewMode.Quad;

    /// <summary>True when this mode needs the whole circle, so a wall mount cannot serve it.</summary>
    public bool NeedsFullCircle => Mode == DewarpViewMode.Panorama360;

    /// <summary>
    /// This view with its aim and width forced into what <paramref name="calibration"/>'s lens
    /// can actually show.
    /// </summary>
    /// <remarks>
    /// Pitch stops a hair inside the rim rather than on it. Landing exactly on the rim would aim
    /// the view centre at the last ring of pixels the lens caught, where a rectilinear window is
    /// more than half outside the circle — technically valid and visibly useless.
    /// </remarks>
    public DewarpView ClampedTo(in FisheyeCalibration calibration)
    {
        var orientation = Orientation.Normalized();
        double maxPitch = calibration.ThetaMaxRad * 180 / Math.PI * 0.98;
        if (!double.IsFinite(maxPitch) || maxPitch <= 0)
            maxPitch = 0;
        orientation = orientation with { PitchDegrees = Math.Min(orientation.PitchDegrees, maxPitch) };

        double fov = HorizontalFovDegrees;
        if (!double.IsFinite(fov))
            fov = DefaultFor(calibration.Mount, Mode).HorizontalFovDegrees;
        fov = Mode switch
        {
            DewarpViewMode.Panorama180 => 180,
            DewarpViewMode.Panorama360 => 360,
            _ => Math.Clamp(fov, MinHorizontalFovDegrees, MaxHorizontalFovDegrees),
        };
        return this with { Orientation = orientation, HorizontalFovDegrees = fov };
    }

    /// <summary>
    /// This view nudged by an azimuth and tilt delta, in degrees. The coarse API — buttons and
    /// arrow keys.
    /// </summary>
    /// <remarks>
    /// A mouse drag should use <see cref="AimedAt"/> instead, driven by the difference between
    /// the rays under the cursor at drag-start and now. Fixed degrees-per-pixel feels wrong at
    /// both ends of the zoom range: unusably twitchy when zoomed in, sluggish when zoomed out.
    /// </remarks>
    public DewarpView Pan(double deltaYawDegrees, double deltaPitchDegrees,
        in FisheyeCalibration calibration)
    {
        if (!double.IsFinite(deltaYawDegrees)) deltaYawDegrees = 0;
        if (!double.IsFinite(deltaPitchDegrees)) deltaPitchDegrees = 0;
        return (this with
        {
            Orientation = Orientation with
            {
                YawDegrees = Orientation.YawDegrees + deltaYawDegrees,
                PitchDegrees = Orientation.PitchDegrees + deltaPitchDegrees,
            },
        }).ClampedTo(calibration);
    }

    /// <summary>
    /// This view aimed at an absolute direction in the lens frame — incidence angle out from the
    /// optical axis and azimuth around it, both in radians. What a drag gesture calls.
    /// </summary>
    public DewarpView AimedAt(double thetaRad, double phiRad, in FisheyeCalibration calibration)
    {
        if (!double.IsFinite(thetaRad)) thetaRad = 0;
        if (!double.IsFinite(phiRad)) phiRad = 0;
        return (this with
        {
            Orientation = Orientation with
            {
                YawDegrees = phiRad * 180 / Math.PI,
                PitchDegrees = thetaRad * 180 / Math.PI,
            },
        }).ClampedTo(calibration);
    }

    /// <summary>
    /// This view zoomed by a multiplier on the field of view — below 1 zooms in, above 1 zooms
    /// out. A no-op in the modes where zoom means nothing.
    /// </summary>
    /// <remarks>
    /// Multiplicative rather than additive so each wheel notch feels the same at every zoom
    /// level; subtracting a fixed number of degrees crawls when wide and jumps when narrow.
    /// </remarks>
    public DewarpView Zoom(double factor, in FisheyeCalibration calibration)
    {
        if (!SupportsZoom || !double.IsFinite(factor) || factor <= 0)
            return ClampedTo(calibration);
        return (this with { HorizontalFovDegrees = HorizontalFovDegrees * factor })
            .ClampedTo(calibration);
    }

    /// <summary>
    /// The panes to render: this view alone, or for <see cref="DewarpViewMode.Quad"/> four
    /// <see cref="DewarpViewMode.Rectilinear"/> views 90° apart around the axis, with the grid
    /// cell each belongs in.
    /// </summary>
    /// <remarks>
    /// Quad is built out of the rectilinear primitive rather than being its own projection, so
    /// there is only ever one piece of perspective arithmetic to get right. The tests assert the
    /// panes are byte-identical to standalone rectilinear views at the same aims, which is what
    /// keeps the composition from drifting away from the primitive it is made of.
    /// </remarks>
    public IReadOnlyList<(DewarpView View, int Column, int Row)> Panes
    {
        get
        {
            if (Mode != DewarpViewMode.Quad)
                return [(this, 0, 0)];
            var panes = new (DewarpView, int, int)[4];
            for (int i = 0; i < 4; i++)
            {
                var view = this with
                {
                    Mode = DewarpViewMode.Rectilinear,
                    Orientation = Orientation with
                    {
                        YawDegrees = Orientation.YawDegrees + i * 90,
                    },
                };
                panes[i] = (view with { Orientation = view.Orientation.Normalized() },
                    i % 2, i / 2);
            }
            return panes;
        }
    }
}
