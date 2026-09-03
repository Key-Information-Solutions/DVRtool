namespace DVRTool.Core;

/// <summary>
/// A 3×3 rotation, stored flat so the per-pixel path neither allocates nor indirects.
/// </summary>
internal readonly struct Rot3(
    double m00, double m01, double m02,
    double m10, double m11, double m12,
    double m20, double m21, double m22)
{
    public readonly double M00 = m00, M01 = m01, M02 = m02;
    public readonly double M10 = m10, M11 = m11, M12 = m12;
    public readonly double M20 = m20, M21 = m21, M22 = m22;

    public static Rot3 Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public static Rot3 AboutY(double rad)
    {
        double c = Math.Cos(rad), s = Math.Sin(rad);
        return new Rot3(c, 0, s, 0, 1, 0, -s, 0, c);
    }

    public static Rot3 AboutZ(double rad)
    {
        double c = Math.Cos(rad), s = Math.Sin(rad);
        return new Rot3(c, -s, 0, s, c, 0, 0, 0, 1);
    }

    public static Rot3 operator *(Rot3 a, Rot3 b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
        a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
        a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
        a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
        a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
        a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
        a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

    public (double X, double Y, double Z) Apply(double x, double y, double z) =>
        (M00 * x + M01 * y + M02 * z,
         M10 * x + M11 * y + M12 * z,
         M20 * x + M21 * y + M22 * z);

    /// <summary>The inverse, which for a rotation is the transpose.</summary>
    public (double X, double Y, double Z) ApplyTransposed(double x, double y, double z) =>
        (M00 * x + M10 * y + M20 * z,
         M01 * x + M11 * y + M21 * z,
         M02 * x + M12 * y + M22 * z);

    public Rot3 Transposed() => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);
}

/// <summary>
/// The geometry of one dewarped view: everything needed to turn an output pixel into a source
/// pixel, precomputed once so the per-pixel path is arithmetic only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Frames.</b> The lens frame has <c>+Z</c> along the optical axis pointing into the scene,
/// <c>+X</c> to image right and <c>+Y</c> to image <i>down</i>, matching pixel coordinates so
/// there is no flip hiding between this type and a bitmap. A direction in that frame has an
/// incidence angle from the axis and an azimuth around it; the lens law turns the former into a
/// radius and the calibration turns the pair into a pixel.
/// </para>
/// <para>
/// <b>The view rotation is <c>Rz(yaw) · Ry(pitch) · Rz(mountRoll + viewRoll)</c>.</b> Tilting
/// about Y rather than X is what makes the view's yaw equal the sampled azimuth exactly and its
/// pitch equal the incidence angle exactly, which in turn is what makes Quad's four panes land
/// at one common tilt. The trailing roll is where the mount lives — see
/// <see cref="FisheyeProjection.MountRollRad"/> — and it is a rotation about the axis rather
/// than a look-at with an up vector precisely because a look-at degenerates when the view aims
/// down the axis, which for a ceiling camera is the default.
/// </para>
/// <para>
/// Pixel centres are at <c>+0.5</c>. That convention is pinned by a test because a future GPU
/// path samples texel centres the same way, and a half-pixel disagreement between the two ships
/// as "the accelerated view looks slightly soft".
/// </para>
/// </remarks>
public readonly struct DewarpGeometry
{
    private readonly FisheyeCalibration _cal;
    private readonly DewarpView _view;
    private readonly Rot3 _rotation;
    private readonly double _focal;
    private readonly double _thetaMax;
    private readonly double _tanHalfW;
    private readonly double _tanHalfH;
    private readonly double _cosRoll;
    private readonly double _sinRoll;
    private readonly bool _mirrored;

    /// <summary>Output width in pixels.</summary>
    public int OutputWidth { get; }

    /// <summary>Output height in pixels.</summary>
    public int OutputHeight { get; }

    /// <summary>The calibration this geometry was built from.</summary>
    public FisheyeCalibration Calibration => _cal;

    /// <summary>The view this geometry was built from, already clamped to the lens.</summary>
    public DewarpView View => _view;

    internal DewarpGeometry(in FisheyeCalibration calibration, in DewarpView view,
        int outputWidth, int outputHeight)
    {
        _cal = calibration.Normalized();
        _view = view.ClampedTo(_cal);
        OutputWidth = Math.Max(1, outputWidth);
        OutputHeight = Math.Max(1, outputHeight);

        _focal = _cal.FocalPixels;
        _thetaMax = _cal.ThetaMaxRad;

        double roll = FisheyeProjection.MountRollRad(_cal.Mount, _view.Orientation.YawRad)
            + _view.Orientation.RollRad;
        _rotation = Rot3.AboutZ(_view.Orientation.YawRad)
            * Rot3.AboutY(_view.Orientation.PitchRad)
            * Rot3.AboutZ(roll);
        _mirrored = _cal.Mount == FisheyeMount.Floor;

        // Rectilinear panes are a flat plane at unit distance; the half-extents are tangents, and
        // the vertical one follows the aspect ratio so pixels stay square.
        double halfFov = _view.HorizontalFovDegrees / 2 * Math.PI / 180;
        _tanHalfW = Math.Tan(halfFov);
        _tanHalfH = _tanHalfW * OutputHeight / OutputWidth;

        // The calibration's own roll: the camera rotated about its optical axis turns the whole
        // projected image in sensor coordinates, so it rotates the offsets from the circle centre.
        double calRoll = _cal.RollDegrees * Math.PI / 180;
        _cosRoll = Math.Cos(calRoll);
        _sinRoll = Math.Sin(calRoll);
    }

    /// <summary>
    /// The direction in the lens frame that an output pixel looks along. Never null: every pixel
    /// of a pane looks somewhere, even if the lens cannot see that far.
    /// </summary>
    public (double X, double Y, double Z) RayFor(double px, double py)
    {
        double ndcX = (px + 0.5 - OutputWidth / 2.0) / (OutputWidth / 2.0);
        double ndcY = (py + 0.5 - OutputHeight / 2.0) / (OutputHeight / 2.0);
        if (_mirrored)
            ndcX = -ndcX;

        if (_view.Mode is DewarpViewMode.Panorama180 or DewarpViewMode.Panorama360)
        {
            // An equirectangular unroll: azimuth across, incidence angle down. The top of the
            // strip is the rim of the circle and the bottom is the optical axis, so for a ceiling
            // camera the horizon sits at the top where real-world up belongs.
            double span = (_view.Mode == DewarpViewMode.Panorama360 ? 360 : 180) * Math.PI / 180;
            double phi = _view.Orientation.YawRad + ndcX * span / 2;
            double theta = _thetaMax * (1 - (py + 0.5) / OutputHeight);
            double sinT = Math.Sin(theta);
            return (Math.Cos(phi) * sinT, Math.Sin(phi) * sinT, Math.Cos(theta));
        }

        // A flat perspective window: the pixel on the image plane, rotated into the lens frame.
        double cx = ndcX * _tanHalfW;
        double cy = ndcY * _tanHalfH;
        double len = Math.Sqrt(cx * cx + cy * cy + 1);
        var (x, y, z) = _rotation.Apply(cx / len, cy / len, 1 / len);
        return (x, y, z);
    }

    /// <summary>
    /// The source pixel an output pixel samples, or null when its ray leaves the image circle.
    /// </summary>
    /// <remarks>
    /// Null rather than a clamp, all the way down from
    /// <see cref="LensModel.ThetaFromRadiusOverFocal"/>. Clamping is what produces the artefact
    /// where the edge of a dewarped view smears along the rim, or wraps around to the far side of
    /// the circle, instead of simply going black.
    /// </remarks>
    public (double X, double Y)? SourceFor(double px, double py)
    {
        var (dx, dy, dz) = RayFor(px, py);
        return SourceForRay(dx, dy, dz);
    }

    /// <summary>The source pixel a lens-frame direction lands on, or null if the lens misses it.</summary>
    public (double X, double Y)? SourceForRay(double dx, double dy, double dz)
    {
        // atan2 rather than acos: a lens wider than 180° sees directions with a negative Z, and
        // atan2 reports those as an angle past 90° instead of losing the sign.
        double theta = Math.Atan2(Math.Sqrt(dx * dx + dy * dy), dz);
        if (!(theta <= _thetaMax))
            return null;

        double rOverF = LensModel.RadiusOverFocal(_cal.Projection, theta);
        if (double.IsNaN(rOverF) || !double.IsFinite(_focal))
            return null;
        double r = _focal * rOverF;

        // Azimuth carried as a unit vector: at the exact optical axis both components are zero
        // and atan2 would be arbitrary, but r is zero there too so the product is the centre.
        double hyp = Math.Sqrt(dx * dx + dy * dy);
        double ux = hyp > 0 ? dx / hyp : 0;
        double uy = hyp > 0 ? dy / hyp : 0;

        double u = r * ux;
        double v = r * uy;
        // Calibration roll first, in optical space; then ellipticity, in sensor space.
        double ru = u * _cosRoll - v * _sinRoll;
        double rv = u * _sinRoll + v * _cosRoll;
        return (_cal.CenterX + ru, _cal.CenterY + rv * _cal.Ellipticity);
    }

    /// <summary>
    /// Where a source pixel appears in the pane, or null when the lens never saw it or it falls
    /// behind the virtual camera.
    /// </summary>
    /// <remarks>
    /// The result is not clipped to the pane — a point just off the edge returns a coordinate
    /// just outside it, which is what click-to-centre and the round-trip tests want.
    /// </remarks>
    public (double X, double Y)? OutputFor(double sx, double sy)
    {
        double u = sx - _cal.CenterX;
        double v = (sy - _cal.CenterY) / _cal.Ellipticity;
        // Undo the calibration roll.
        double ru = u * _cosRoll + v * _sinRoll;
        double rv = -u * _sinRoll + v * _cosRoll;

        double r = Math.Sqrt(ru * ru + rv * rv);
        if (!double.IsFinite(_focal) || _focal <= 0)
            return null;
        double theta = LensModel.ThetaFromRadiusOverFocal(_cal.Projection, r / _focal);
        if (double.IsNaN(theta) || theta > _thetaMax)
            return null;

        double sinT = Math.Sin(theta);
        double phi = Math.Atan2(rv, ru);
        double dx = Math.Cos(phi) * sinT;
        double dy = Math.Sin(phi) * sinT;
        double dz = Math.Cos(theta);

        if (_view.Mode is DewarpViewMode.Panorama180 or DewarpViewMode.Panorama360)
        {
            double span = (_view.Mode == DewarpViewMode.Panorama360 ? 360 : 180) * Math.PI / 180;
            double delta = phi - _view.Orientation.YawRad;
            // Bring the azimuth difference into (-pi, pi] before asking whether it is on screen.
            delta = Math.IEEERemainder(delta, 2 * Math.PI);
            double ndcX = delta / (span / 2);
            if (_mirrored)
                ndcX = -ndcX;
            double px = ndcX * (OutputWidth / 2.0) + OutputWidth / 2.0 - 0.5;
            double py = (1 - theta / _thetaMax) * OutputHeight - 0.5;
            return (px, py);
        }

        var (cx, cy, cz) = _rotation.ApplyTransposed(dx, dy, dz);
        if (cz <= 0)
            return null; // behind the virtual camera
        double nx = cx / cz / _tanHalfW;
        double ny = cy / cz / _tanHalfH;
        if (_mirrored)
            nx = -nx;
        return (nx * (OutputWidth / 2.0) + OutputWidth / 2.0 - 0.5,
                ny * (OutputHeight / 2.0) + OutputHeight / 2.0 - 0.5);
    }

    /// <summary>
    /// The direction under an output pixel, as an incidence angle out from the optical axis and
    /// an azimuth around it, both in radians — the pair <see cref="DewarpView.AimedAt"/> takes.
    /// </summary>
    /// <remarks>
    /// This is what makes a drag feel like grabbing the picture: take this at the pointer's
    /// position when the drag started and again where it is now, and aim the view by the
    /// difference. Sensitivity then scales with the zoom for free, because a narrow view spans
    /// fewer degrees across the same pixels.
    /// </remarks>
    public (double ThetaRad, double PhiRad) LookAt(double px, double py)
    {
        var (dx, dy, dz) = RayFor(px, py);
        double hyp = Math.Sqrt(dx * dx + dy * dy);
        return (Math.Atan2(hyp, dz), hyp > 0 ? Math.Atan2(dy, dx) : 0);
    }
}

/// <summary>
/// Turns a <see cref="FisheyeCalibration"/> and a <see cref="DewarpView"/> into source
/// coordinates. Pure, and the only place perspective arithmetic lives.
/// </summary>
public static class FisheyeProjection
{
    /// <summary>
    /// The roll applied about the optical axis for a mount, in radians, which is what decides
    /// which way is up in the dewarped pane.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ceiling: +90°.</b> A ceiling camera's optical axis points at the floor, so the rim of
    /// the circle is the horizon and real-world up runs radially <i>outward</i>. A quarter turn
    /// is what aligns the pane's up with that radial direction, so a person standing at any
    /// azimuth appears upright rather than lying on their side.
    /// </para>
    /// <para>
    /// <b>Floor: −90°, plus a mirror.</b> A floor or table camera looks up, so the circle centre
    /// is the ceiling and up runs radially <i>inward</i> — the opposite quarter turn. It also
    /// sees the scene handed the other way round, which is why
    /// <see cref="FisheyeMount.Floor"/> mirrors the pane. Without that flip a drag to the right
    /// moves the view left, which is the classic "PTZ goes backwards on floor mounts" bug.
    /// </para>
    /// <para>
    /// <b>Wall: −yaw.</b> A wall camera is level and looking into a room, so up is a fixed
    /// direction in the image rather than a radial one. Cancelling the yaw keeps the horizon
    /// level as the view sweeps sideways instead of rolling the picture around the axis.
    /// </para>
    /// </remarks>
    public static double MountRollRad(FisheyeMount mount, double yawRad) => mount switch
    {
        FisheyeMount.Ceiling => Math.PI / 2,
        FisheyeMount.Floor => -Math.PI / 2,
        _ => -yawRad,
    };

    /// <summary>
    /// True when a mount sees the scene handed the other way round and the pane must be mirrored.
    /// </summary>
    public static bool IsMirrored(FisheyeMount mount) => mount == FisheyeMount.Floor;

    /// <summary>
    /// The precomputed geometry for a view. Build this once per (calibration, view, output size)
    /// and reuse it across every pixel; the per-pixel calls on it are arithmetic only.
    /// </summary>
    public static DewarpGeometry For(in FisheyeCalibration calibration, in DewarpView view,
        int outputWidth, int outputHeight) =>
        new(calibration, view, outputWidth, outputHeight);

    /// <summary>
    /// The source pixel one output pixel samples, or null when its ray leaves the image circle.
    /// Convenience over <see cref="For"/> for one-off lookups and tests; building the geometry
    /// per pixel would be wasteful in a loop.
    /// </summary>
    public static (double X, double Y)? SourceFor(in FisheyeCalibration calibration,
        in DewarpView view, int outputWidth, int outputHeight, double px, double py) =>
        For(calibration, view, outputWidth, outputHeight).SourceFor(px, py);

    /// <summary>Where a source pixel appears in the pane. See <see cref="DewarpGeometry.OutputFor"/>.</summary>
    public static (double X, double Y)? OutputFor(in FisheyeCalibration calibration,
        in DewarpView view, int outputWidth, int outputHeight, double sx, double sy) =>
        For(calibration, view, outputWidth, outputHeight).OutputFor(sx, sy);

    /// <summary>The direction under an output pixel. See <see cref="DewarpGeometry.LookAt"/>.</summary>
    public static (double ThetaRad, double PhiRad) LookAt(in FisheyeCalibration calibration,
        in DewarpView view, int outputWidth, int outputHeight, double px, double py) =>
        For(calibration, view, outputWidth, outputHeight).LookAt(px, py);
}
