namespace DVRTool.Core;

/// <summary>
/// How a fisheye lens maps an incidence angle to a radius on the sensor. Every fisheye is one
/// of these four laws, and which one it is changes where a given part of the room lands in the
/// circle — so guessing wrong bends straight lines by a visible amount even when the circle is
/// calibrated perfectly.
/// </summary>
/// <remarks>
/// The names are DW Spectrum's, deliberately: an operator who has already calibrated a camera
/// in the DW client can carry the same answer across instead of re-deriving it. DW offers
/// Equidistant, Stereographic and Equisolid; <see cref="Orthographic"/> is added because a few
/// older panomorph lenses use it and it costs one line.
/// </remarks>
public enum LensProjection
{
    /// <summary>
    /// <c>r = f·θ</c>. The commonest law and the right default — angle is linear in radius, so
    /// the horizon of a 180° lens lands exactly on the rim of the circle.
    /// </summary>
    Equidistant,

    /// <summary>
    /// <c>r = 2f·tan(θ/2)</c>. Conformal: preserves shapes locally at the cost of stretching
    /// the periphery hardest of the four.
    /// </summary>
    Stereographic,

    /// <summary>
    /// <c>r = 2f·sin(θ/2)</c>. Equal-area — a solid angle covers the same number of pixels
    /// wherever it sits, which is why it is also called equal-solid-angle.
    /// </summary>
    EquisolidAngle,

    /// <summary>
    /// <c>r = f·sin θ</c>. Only monotonic out to 90°, so it cannot describe a lens wider than
    /// 180°; see <see cref="LensModel.MaxTheta"/>.
    /// </summary>
    Orthographic,
}

/// <summary>
/// The radius/angle laws of <see cref="LensProjection"/>, in units of focal length.
/// </summary>
/// <remarks>
/// <para>
/// Everything is expressed as <c>r/f</c> — radius over focal length — rather than in pixels, so
/// this type knows nothing about a particular camera. <see cref="FisheyeCalibration"/> turns a
/// calibrated image circle into a focal length in pixels and scales through these.
/// </para>
/// <para>
/// Pure and unit-tested. The four values of <c>r/f</c> at θ=90° (π/2, 2, √2 and 1) <i>are</i>
/// this model: a typo in any one formula moves exactly one of them, which is what the tests
/// assert against.
/// </para>
/// </remarks>
public static class LensModel
{
    /// <summary>
    /// The largest incidence angle a projection can describe, in radians. Beyond it the law
    /// stops being monotonic and two different directions would map to the same radius, so a
    /// dewarp would fold part of the room back on top of itself.
    /// </summary>
    /// <remarks>
    /// π for the three that stay monotonic across a full hemisphere and beyond; π/2 for
    /// <see cref="LensProjection.Orthographic"/>, whose <c>sin θ</c> turns over at 90°. That
    /// difference is why <see cref="FisheyeCalibration.Validate"/> refuses an orthographic lens
    /// declared wider than 180°.
    /// </remarks>
    public static double MaxTheta(LensProjection projection) =>
        projection == LensProjection.Orthographic ? Math.PI / 2 : Math.PI;

    /// <summary>
    /// Radius over focal length for an incidence angle, or <see cref="double.NaN"/> when the
    /// angle is negative or past <see cref="MaxTheta"/>.
    /// </summary>
    public static double RadiusOverFocal(LensProjection projection, double thetaRad)
    {
        if (double.IsNaN(thetaRad) || thetaRad < 0 || thetaRad > MaxTheta(projection))
            return double.NaN;
        return projection switch
        {
            LensProjection.Equidistant => thetaRad,
            LensProjection.Stereographic => 2 * Math.Tan(thetaRad / 2),
            LensProjection.EquisolidAngle => 2 * Math.Sin(thetaRad / 2),
            LensProjection.Orthographic => Math.Sin(thetaRad),
            _ => double.NaN,
        };
    }

    /// <summary>
    /// The incidence angle that lands at a given radius over focal length, or
    /// <see cref="double.NaN"/> when no angle does.
    /// </summary>
    /// <remarks>
    /// NaN rather than a clamp is deliberate and load-bearing: it is what lets the sample-map
    /// builder mark a pixel as outside the image circle. Clamping instead is what produces the
    /// artefact where the edge of a dewarped view smears, or wraps to the far side of the
    /// circle, instead of going black.
    /// </remarks>
    public static double ThetaFromRadiusOverFocal(LensProjection projection, double rOverF)
    {
        if (double.IsNaN(rOverF) || rOverF < 0)
            return double.NaN;
        double theta = projection switch
        {
            LensProjection.Equidistant => rOverF,
            // atan keeps this finite for every non-negative input: stereographic's radius runs
            // off to infinity as the angle approaches π, so there is no radius it cannot answer.
            LensProjection.Stereographic => 2 * Math.Atan(rOverF / 2),
            LensProjection.EquisolidAngle => rOverF > 2 ? double.NaN : 2 * Math.Asin(rOverF / 2),
            LensProjection.Orthographic => rOverF > 1 ? double.NaN : Math.Asin(rOverF),
            _ => double.NaN,
        };
        return theta > MaxTheta(projection) ? double.NaN : theta;
    }
}
