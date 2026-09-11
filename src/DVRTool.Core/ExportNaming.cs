namespace DVRTool.Core;

/// <summary>
/// Naming policy for a front end that has to propose a file name <em>before</em> the
/// download starts — a Save-As dialog cannot wait for the bytes the way the CLI's
/// auto-naming does.
/// <para>
/// The name it suggests is therefore a prediction, and the prediction is checked:
/// once the file exists, <see cref="ContainerSniffer"/> decides what it really holds
/// and the operator is warned if the two disagree.
/// </para>
/// </summary>
public static class ExportNaming
{
    /// <summary>
    /// Container a vendor's raw export is expected to arrive in.
    /// <para>
    /// A well-founded guess, not a promise: Hikvision's ISAPI download is an MPEG
    /// program stream however the device labels it, and Dahua sends DHAV — but that is
    /// firmware behavior, and only the bytes settle it. What matters is that the guess
    /// is honest. Naming a Hikvision export ".mp4" — which is what the device implies
    /// and almost never what it sends — produces a file Windows refuses to open.
    /// </para>
    /// </summary>
    public static MediaContainer RawContainerFor(Vendor vendor) => vendor switch
    {
        Vendor.Hikvision => MediaContainer.MpegProgramStream,
        Vendor.Dahua => MediaContainer.Dhav,
        // Nx's /media/ export is asked for as Matroska: the one container it muxes without
        // seeking back to finish an index, so a cut-short transfer still plays.
        Vendor.NxWitness => MediaContainer.Matroska,
        _ => MediaContainer.Unknown,
    };

    /// <summary>
    /// Longest camera-name prefix an export carries. A name is an operator's label, not a
    /// field with a length limit — some sites write a sentence — and MAX_PATH is spent by
    /// the export directory as much as by the file, so the prefix is capped well short of
    /// what the filesystem would take.
    /// </summary>
    public const int MaxCameraPrefix = 48;

    /// <summary>
    /// Base name for an export: the camera's name in front of the channel/time stamp, so a
    /// folder of clips reads as cameras rather than as channel numbers
    /// ("S Service Drive ch9_20260911_115000-120000").
    /// <para>
    /// The name is the recorder's, so it can hold anything an operator typed — path
    /// separators, a trailing dot, nothing at all. It is scrubbed to something a file can
    /// be called, and when nothing survives the scrub the stamp stands on its own: an
    /// export never fails, and never lands somewhere unexpected, because of a camera's name.
    /// </para>
    /// </summary>
    public static string BaseName(string? cameraName, string stamp)
    {
        string prefix = CameraPrefix(cameraName);
        return prefix.Length == 0 ? stamp : $"{prefix} {stamp}";
    }

    /// <summary>
    /// The camera-name half of <see cref="BaseName"/>, scrubbed: invalid characters and
    /// runs of whitespace become single spaces, the result is capped, and Windows' refusal
    /// to end a name in a dot or a space is honoured. Empty when nothing usable is left.
    /// </summary>
    public static string CameraPrefix(string? cameraName)
    {
        if (string.IsNullOrWhiteSpace(cameraName))
            return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(cameraName.Length);
        foreach (char c in cameraName)
        {
            // Control characters are legal in some of these names and legible in none.
            bool bad = char.IsControl(c) || Array.IndexOf(invalid, c) >= 0;
            char next = bad || char.IsWhiteSpace(c) ? ' ' : c;
            if (next == ' ' && (sb.Length == 0 || sb[^1] == ' '))
                continue;
            sb.Append(next);
        }
        string cleaned = sb.ToString().TrimEnd();
        if (cleaned.Length > MaxCameraPrefix)
            cleaned = cleaned[..MaxCameraPrefix].TrimEnd();
        // A name may not end in a dot on Windows, and the stamp that follows would not
        // save it: "Lot 3. ch9_…" is fine, "Lot 3." as the whole name is not, and trimming
        // unconditionally keeps the two cases from having to be told apart.
        return cleaned.TrimEnd('.', ' ');
    }

    /// <summary>
    /// Save-As suggestion for an export of <paramref name="baseName"/>. A remuxed export
    /// is named for the container ffmpeg is about to write; a raw one for what the device
    /// is expected to send.
    /// </summary>
    public static string SuggestedFileName(string baseName, Vendor vendor, bool remux) =>
        baseName + (remux
            ? ".mp4" // The copy most likely to play on an unprepared Windows machine.
            : ContainerSniffer.ExtensionFor(RawContainerFor(vendor)) ?? ".bin");

    /// <summary>
    /// Save-As filter. It offers what the export is actually going to be, so the
    /// dialog cannot quietly append an extension that contradicts the bytes — and
    /// always keeps "All files", because the operator may know better.
    /// </summary>
    public static string SaveFilter(Vendor vendor, bool remux)
    {
        if (remux)
            return "MP4 video|*.mp4|Matroska video|*.mkv|All files|*.*";
        var raw = RawContainerFor(vendor);
        string extension = ContainerSniffer.ExtensionFor(raw) ?? ".bin";
        return $"{ContainerSniffer.DisplayName(raw)}|*{extension}|All files|*.*";
    }
}
