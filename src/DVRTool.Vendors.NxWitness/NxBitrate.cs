namespace DVRTool.Vendors.NxWitness;

/// <summary>
/// Nx's own quality → bitrate rule: the number the media server asks a camera for when a
/// schedule task names a quality (lowest … highest) rather than a preset bitrate. Pure math,
/// lifted from the open-source <c>CameraBitrateCalculator</c> so DVRTool's worst-case
/// retention math uses the same figure Nx pushes to the camera.
/// </summary>
/// <remarks>
/// <c>kbps = (0.1 + 0.9 · q/4) · 0.009 · (w·h)^0.7 · fps · codec</c>, floored at
/// <see cref="MinKbps"/>, where <c>q</c> is the quality level 0–4 and the codec factor is
/// 1.0 for H.264, 0.8 for H.265 and 2.0 for MJPEG. 1920×1080 at 15 fps "high" comes to
/// ≈2.8 Mbps; 2688×1520 at 15 fps "highest" ≈5.8 Mbps — the values the Nx client shows in
/// its recording-schedule estimate.
/// </remarks>
internal static class NxBitrate
{
    /// <summary>Nx never asks a camera for less than this.</summary>
    public const int MinKbps = 192;

    /// <summary>
    /// The widest bitrate a schedule task accepts, for the planner's clamp: Nx has no
    /// per-camera range API, and a 4K camera on this fleet records at 69 Mbps, so the
    /// ISAPI-typical 16 Mbps ceiling would be wrong here.
    /// </summary>
    public const int MaxKbps = 65_536;

    /// <summary>Quality level as Nx orders them — lowest 0 … highest 4; null for a preset or unknown.</summary>
    public static int? QualityLevel(string? streamQuality) => NormalizeQuality(streamQuality) switch
    {
        "lowest" => 0,
        "low" => 1,
        "normal" => 2,
        "high" => 3,
        "highest" => 4,
        _ => null,
    };

    /// <summary>
    /// Both spellings Nx has used — REST v3's <c>low</c> and the legacy <c>QualityLow</c> —
    /// reduced to the v3 word (<c>QualityPreSet</c> → <c>preset</c>).
    /// </summary>
    public static string NormalizeQuality(string? streamQuality)
    {
        string q = (streamQuality ?? "").Trim().ToLowerInvariant();
        if (q.StartsWith("quality", StringComparison.Ordinal))
            q = q["quality".Length..];
        return q == "preset" || q == "pre_set" ? "preset" : q;
    }

    /// <summary>
    /// The bitrate Nx requests for <paramref name="qualityLevel"/> at this resolution, frame
    /// rate and codec. 0 when the resolution or frame rate is unknown — there is no honest
    /// number without them.
    /// </summary>
    public static int SuggestKbps(int qualityLevel, int width, int height, double fps, string? codec)
    {
        if (width <= 0 || height <= 0 || fps <= 0)
            return 0;
        double quality = 0.1 + 0.9 * (Math.Clamp(qualityLevel, 0, 4) / 4.0);
        double resolution = 0.009 * Math.Pow((double)width * height, 0.7);
        double kbps = quality * resolution * fps * CodecFactor(codec);
        return (int)Math.Round(Math.Max(MinKbps, kbps));
    }

    /// <summary>H.265 encodes the same picture in ~80 % of the bits; MJPEG needs about twice as many.</summary>
    public static double CodecFactor(string? codec) =>
        (codec ?? "").Replace(".", "", StringComparison.Ordinal).ToUpperInvariant() switch
        {
            "H265" or "HEVC" => 0.8,
            "MJPEG" or "JPEG" => 2.0,
            _ => 1.0,
        };
}
