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
        _ => MediaContainer.Unknown,
    };

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
