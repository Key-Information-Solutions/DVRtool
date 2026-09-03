namespace DVRTool.Core;

/// <summary>
/// Mouse gestures on a dewarped pane, turned into views: grab-and-drag, and wheel zoom.
/// </summary>
/// <remarks>
/// <para>
/// <b>A drag grabs the picture.</b> Whatever was under the pointer when the button went down
/// should still be under it as the pointer moves — the gesture every map and every photo viewer
/// has taught people. Fixed degrees-per-pixel does not do that: it is twitchy when zoomed in and
/// sluggish when zoomed out, and it drifts on a curved projection. So the source pixel under the
/// pointer at drag-start is the thing grabbed, and the view is re-aimed so that pixel appears at
/// the pointer's new position.
/// </para>
/// <para>
/// <b>The re-aim is solved as a least-squares problem, because it does not always have an exact
/// answer.</b> The view has two degrees of freedom, yaw and pitch; its roll is fixed by the mount
/// rule (<see cref="FisheyeProjection.MountRollRad"/>) so that people stay upright. That rule
/// has a consequence at the centre of a ceiling camera's picture: the nadir can only ever appear
/// on the pane's vertical centre line, whatever the aim, so a pointer that grabbed the exact
/// centre and moved sideways is asking for something no view can show. Near the centre the
/// problem is merely ill-conditioned; at it, one equation has no solution. A plain Newton step
/// in that situation is enormous and wrong — the first attempt flung a 250-pixel drag to a 76°
/// tilt — so the solve is Levenberg–Marquardt: a damped Gauss–Newton step on the residual in pane
/// pixels, with the damping raised whenever a step fails to reduce it. Away from the centre it
/// converges to the exact answer in three or four steps (the tests hold the grabbed source pixel
/// under the pointer to a tenth of a pixel on every mount); at the centre it moves the pitch to
/// put the nadir at the pointer's height and leaves the yaw, which cannot help, where it was.
/// </para>
/// <para>
/// A pointer over the black outside the circle has no source pixel to grab, so it falls back to
/// panning by the change in the direction under the pointer. A panorama has no perspective to
/// solve: its columns <i>are</i> azimuth, so dragging shifts the yaw by exactly the azimuth
/// difference between the two pointer positions, and its rows are fixed by the mode.
/// </para>
/// </remarks>
public static class DewarpDrag
{
    /// <summary>Outer iterations before giving up on a target that cannot be reached exactly.</summary>
    private const int MaxIterations = 30;

    /// <summary>Damping retries within one iteration before declaring the step hopeless.</summary>
    private const int MaxDampingRetries = 10;

    /// <summary>Residual, in pane pixels, at which the solve is considered done.</summary>
    private const double TolerancePixels = 0.005;

    /// <summary>Central-difference half-step for the Jacobian, in radians.</summary>
    private const double JacobianStepRad = 1e-4;

    /// <summary>Largest single step in either angle, in radians, so no step can fling the view.</summary>
    private const double MaxStepRad = 0.35;

    /// <summary>
    /// A Jacobian column this many times shorter than the other is treated as zero: that unknown
    /// cannot move the residual, and solving for it would only amplify finite-difference noise —
    /// the exact-nadir case, where yaw does nothing to where the nadir appears.
    /// </summary>
    private const double NullColumnRatio = 1e-6;

    /// <summary>
    /// Field-of-view multiplier for one notch of the wheel: 15 % per notch, which iVMS and DW
    /// Spectrum both sit close to. Below 1 is in.
    /// </summary>
    public const double WheelZoomStep = 1.15;

    /// <summary>
    /// The view after dragging the pointer from one pane pixel to another, starting from
    /// <paramref name="start"/>. Call with the view that was current when the button went down,
    /// not the view of the previous mouse-move: the gesture is absolute, so intermediate
    /// rounding never accumulates.
    /// </summary>
    /// <param name="calibration">The lens the pane looks through.</param>
    /// <param name="start">The view when the drag began.</param>
    /// <param name="paneWidth">Pane width in pixels.</param>
    /// <param name="paneHeight">Pane height in pixels.</param>
    /// <param name="x0">Pointer X when the button went down, in pane pixels.</param>
    /// <param name="y0">Pointer Y when the button went down.</param>
    /// <param name="x1">Pointer X now.</param>
    /// <param name="y1">Pointer Y now.</param>
    public static DewarpView Drag(in FisheyeCalibration calibration, in DewarpView start,
        int paneWidth, int paneHeight, double x0, double y0, double x1, double y1)
    {
        if (paneWidth <= 0 || paneHeight <= 0)
            return start.ClampedTo(calibration);
        if (!double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(x1) || !double.IsFinite(y1))
            return start.ClampedTo(calibration);

        var geometry = FisheyeProjection.For(calibration, start, paneWidth, paneHeight);
        var view = geometry.View;

        if (view.Mode is DewarpViewMode.Panorama180 or DewarpViewMode.Panorama360)
        {
            var (_, phi0) = geometry.LookAt(x0, y0);
            var (_, phi1) = geometry.LookAt(x1, y1);
            // Columns are azimuth: the azimuth that was at x0 must now be at x1, so the strip's
            // origin moves by the difference. Wrapped, so a drag across the seam of a 360 does not
            // spin the strip the long way round.
            double delta = Math.IEEERemainder(phi0 - phi1, 2 * Math.PI);
            return view.Pan(delta * 180 / Math.PI, 0, calibration);
        }

        var grabbed = geometry.SourceFor(x0, y0);
        if (grabbed is null)
        {
            // Over the black outside the circle: nothing to hold, so pan by the change in the
            // direction under the pointer.
            var (theta0, phi0) = geometry.LookAt(x0, y0);
            var (theta1, phi1) = geometry.LookAt(x1, y1);
            double dPhi = Math.IEEERemainder(phi0 - phi1, 2 * Math.PI);
            return view.Pan(dPhi * 180 / Math.PI, (theta0 - theta1) * 180 / Math.PI, calibration);
        }

        return Solve(calibration, view, paneWidth, paneHeight,
            grabbed.Value.X, grabbed.Value.Y, x1, y1);
    }

    /// <summary>
    /// The view after one wheel movement. Positive <paramref name="wheelDelta"/> (wheel away from
    /// the user) zooms in, matching every map application; the magnitude is in Windows
    /// <c>WHEEL_DELTA</c> units of 120 per notch.
    /// </summary>
    public static DewarpView Wheel(in FisheyeCalibration calibration, in DewarpView view, int wheelDelta)
    {
        if (wheelDelta == 0 || !view.SupportsZoom)
            return view.ClampedTo(calibration);
        double notches = wheelDelta / 120.0;
        return view.Zoom(Math.Pow(WheelZoomStep, -notches), calibration);
    }

    /// <summary>
    /// Levenberg–Marquardt on "aim the view so source pixel (sx, sy) appears at pane pixel
    /// (x1, y1)", in the unknowns yaw and pitch.
    /// </summary>
    private static DewarpView Solve(in FisheyeCalibration calibration, DewarpView view,
        int paneWidth, int paneHeight, double sx, double sy, double x1, double y1)
    {
        double yaw = view.Orientation.YawRad;
        double pitch = view.Orientation.PitchRad;
        var residual = Residual(calibration, view, paneWidth, paneHeight, yaw, pitch, sx, sy, x1, y1);
        if (residual is null)
            return view;
        var (rx, ry) = residual.Value;
        double cost = rx * rx + ry * ry;
        double lambda = 1e-3;

        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            if (cost < TolerancePixels * TolerancePixels)
                break;

            // Central differences: the residual is smooth away from the pole, and the one-sided
            // estimate's O(h) error is what a null column turns into a spurious gradient. Pitch
            // is a half-line — a negative pitch is not another view but this one folded to the
            // opposite yaw, and under the mount roll that is a different picture — so zero is a
            // boundary: the pitch difference is one-sided there, and no step goes below it.
            double pitchBelow = Math.Max(0, pitch - JacobianStepRad);
            var yawPlus = Residual(calibration, view, paneWidth, paneHeight, yaw + JacobianStepRad, pitch, sx, sy, x1, y1);
            var yawMinus = Residual(calibration, view, paneWidth, paneHeight, yaw - JacobianStepRad, pitch, sx, sy, x1, y1);
            var pitchPlus = Residual(calibration, view, paneWidth, paneHeight, yaw, pitch + JacobianStepRad, sx, sy, x1, y1);
            var pitchMinus = Residual(calibration, view, paneWidth, paneHeight, yaw, pitchBelow, sx, sy, x1, y1);
            if (yawPlus is null || yawMinus is null || pitchPlus is null || pitchMinus is null)
                break;

            // J = d(residual)/d(yaw, pitch); normal equations A δ = −g.
            double pitchSpan = pitch + JacobianStepRad - pitchBelow;
            double j00 = (yawPlus.Value.X - yawMinus.Value.X) / (2 * JacobianStepRad);
            double j10 = (yawPlus.Value.Y - yawMinus.Value.Y) / (2 * JacobianStepRad);
            double j01 = (pitchPlus.Value.X - pitchMinus.Value.X) / pitchSpan;
            double j11 = (pitchPlus.Value.Y - pitchMinus.Value.Y) / pitchSpan;
            if (!double.IsFinite(j00) || !double.IsFinite(j10) || !double.IsFinite(j01) || !double.IsFinite(j11))
                break;

            // An unknown whose column is (numerically) zero is frozen for this step rather than
            // solved for: dividing a noise gradient by a tiny damped diagonal is what flung the yaw.
            double yawColumn = Math.Sqrt(j00 * j00 + j10 * j10);
            double pitchColumn = Math.Sqrt(j01 * j01 + j11 * j11);
            bool yawFrozen = yawColumn <= NullColumnRatio * pitchColumn;
            bool pitchFrozen = pitchColumn <= NullColumnRatio * yawColumn;
            if (yawFrozen && pitchFrozen)
                break;
            if (yawFrozen) { j00 = 0; j10 = 0; }
            if (pitchFrozen) { j01 = 0; j11 = 0; }

            double a00 = j00 * j00 + j10 * j10;
            double a01 = j00 * j01 + j10 * j11;
            double a11 = j01 * j01 + j11 * j11;
            double g0 = j00 * rx + j10 * ry;
            double g1 = j01 * rx + j11 * ry;
            double trace = a00 + a11;

            bool accepted = false;
            for (int retry = 0; retry < MaxDampingRetries; retry++)
            {
                // Marquardt scaling plus a share of the trace, so a frozen unknown's diagonal is
                // never zero and its step is exactly zero.
                double d00 = a00 + lambda * (a00 + 1e-3 * trace);
                double d11 = a11 + lambda * (a11 + 1e-3 * trace);
                double det = d00 * d11 - a01 * a01;
                if (!double.IsFinite(det) || det <= 0)
                {
                    lambda *= 4;
                    continue;
                }
                double dYaw = yawFrozen ? 0 : (-g0 * d11 + g1 * a01) / det;
                double dPitch = pitchFrozen ? 0 : (-g1 * d00 + g0 * a01) / det;
                dYaw = Math.Clamp(dYaw, -MaxStepRad, MaxStepRad);
                dPitch = Math.Clamp(dPitch, -MaxStepRad, MaxStepRad);
                if (pitch + dPitch < 0)
                    dPitch = -pitch; // stop at the pole rather than folding through it
                if (Math.Abs(dYaw) < 1e-12 && Math.Abs(dPitch) < 1e-12)
                {
                    accepted = false;
                    break;
                }

                var trial = Residual(calibration, view, paneWidth, paneHeight,
                    yaw + dYaw, pitch + dPitch, sx, sy, x1, y1);
                if (trial is not null)
                {
                    double trialCost = trial.Value.X * trial.Value.X + trial.Value.Y * trial.Value.Y;
                    if (trialCost < cost)
                    {
                        yaw += dYaw;
                        pitch += dPitch;
                        (rx, ry) = trial.Value;
                        cost = trialCost;
                        lambda = Math.Max(lambda / 3, 1e-6);
                        accepted = true;
                        break;
                    }
                }
                lambda = Math.Min(lambda * 4, 1e6);
            }
            if (!accepted)
                break;
        }

        return Aimed(calibration, view, yaw, pitch);
    }

    /// <summary>Where the grabbed source pixel lands minus where the pointer is, for an aim; null when it is behind the camera.</summary>
    private static (double X, double Y)? Residual(in FisheyeCalibration calibration, in DewarpView view,
        int paneWidth, int paneHeight, double yawRad, double pitchRad,
        double sx, double sy, double x1, double y1)
    {
        var aimed = Aimed(calibration, view, yawRad, pitchRad);
        var at = FisheyeProjection.For(calibration, aimed, paneWidth, paneHeight).OutputFor(sx, sy);
        if (at is null)
            return null;
        return (at.Value.X - x1, at.Value.Y - y1);
    }

    private static DewarpView Aimed(in FisheyeCalibration calibration, in DewarpView view,
        double yawRad, double pitchRad) =>
        view.AimedAt(pitchRad, yawRad, calibration);
}
