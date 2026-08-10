namespace DVRTool.Core;

/// <summary>Container to remux an export into.</summary>
public enum RemuxContainer
{
    /// <summary>Plays out of the box on Windows — the right default for handover copies.</summary>
    Mp4,
    /// <summary>Carries codec/audio combinations MP4 cannot.</summary>
    Matroska,
}

/// <summary>
/// Where a download is written and, when remuxing, where ffmpeg writes the result.
/// <see cref="DownloadPath"/> and <see cref="RemuxPath"/> are never the same path —
/// ffmpeg refuses to read and write one file ("same as Input #0 - exiting").
/// </summary>
public sealed record DownloadPlan(string DownloadPath, string? RemuxPath, RemuxContainer? Container)
{
    public bool NeedsRemux => RemuxPath is not null;

    /// <summary>The file the operator ends up with.</summary>
    public string FinalPath => RemuxPath ?? DownloadPath;
}

public static class DownloadPaths
{
    /// <summary>
    /// Resolves the remux container from an explicit <c>--remux &lt;value&gt;</c>, falling
    /// back to what the requested output name implies, and finally to MP4.
    /// </summary>
    public static RemuxContainer ResolveContainer(string? requested, string? requestedOut)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested.Trim().ToLowerInvariant() switch
            {
                "mp4" => RemuxContainer.Mp4,
                "mkv" or "matroska" => RemuxContainer.Matroska,
                var v => throw new ArgumentException(
                    $"unknown --remux container '{v}' (use mp4 or mkv)"),
            };
        return requestedOut is not null &&
               ContainerSniffer.ContainerForExtension(requestedOut) == MediaContainer.Matroska
            ? RemuxContainer.Matroska
            : RemuxContainer.Mp4;
    }

    /// <summary>
    /// Builds the download/remux paths. <paramref name="container"/> null means no remux,
    /// in which case an auto-named target is left extensionless until the bytes are
    /// sniffed — the download's real container is not knowable in advance.
    /// </summary>
    public static DownloadPlan Plan(string? requestedOut, string autoBaseName,
        RemuxContainer? container)
    {
        if (container is not RemuxContainer target)
            return new DownloadPlan(requestedOut ?? autoBaseName, null, null);

        string extension = target == RemuxContainer.Mp4 ? ".mp4" : ".mkv";
        string finalPath = requestedOut switch
        {
            null => autoBaseName + extension,
            var p when !Path.HasExtension(p) => p + extension,
            var p => p,
        };
        return new DownloadPlan(RawPathFor(finalPath), finalPath, target);
    }

    /// <summary>
    /// Path the raw vendor stream is downloaded to before remuxing. A sibling of the
    /// destination (so the atomic rename stays on one volume) that can never equal it,
    /// whatever extension the operator asked for.
    /// </summary>
    public static string RawPathFor(string finalPath) => finalPath + ".raw";

    /// <summary>
    /// Path ffmpeg writes a remux to before it is promoted to <paramref name="finalPath"/>.
    /// A sibling of the destination — the promotion is a rename, which is only atomic
    /// within one volume — and distinct from both the destination and
    /// <see cref="RawPathFor"/>, which exist at the same time.
    /// </summary>
    public static string StagingPathFor(string finalPath) => finalPath + ".remux.part";

    /// <summary>Sniffable container a remux target produces.</summary>
    public static MediaContainer MediaContainerFor(RemuxContainer container) =>
        container == RemuxContainer.Mp4 ? MediaContainer.Mp4 : MediaContainer.Matroska;

    /// <summary>
    /// True when an explicit <c>--remux &lt;value&gt;</c> writes a container the operator's
    /// <c>--out</c> name contradicts — the same "extension lies about the bytes" defect
    /// remuxing exists to fix, only self-inflicted.
    /// <para>
    /// Bare <c>--remux</c> can never conflict: with no value the <c>--out</c> extension
    /// chooses the container, so the two agree by construction.
    /// </para>
    /// </summary>
    public static bool ExplicitContainerContradictsName(string? requestedRemux,
        string? requestedOut, RemuxContainer resolved) =>
        !string.IsNullOrWhiteSpace(requestedRemux) && requestedOut is not null &&
        ContainerSniffer.ExtensionContradicts(requestedOut, MediaContainerFor(resolved));

    public static string OverwriteRefusalMessage(string path) =>
        $"{Path.GetFullPath(path)} already exists — refusing to overwrite an export that " +
        "may already be evidence. Choose another --out, or pass --force to replace it.";

    /// <summary>
    /// Guards a real destination — never the internal <see cref="RawPathFor"/> or
    /// <see cref="StagingPathFor"/> scratch files, which overwrite themselves by design.
    /// </summary>
    public static void EnsureNotOverwriting(string path, bool force)
    {
        if (!force && File.Exists(path))
            throw new ArgumentException(OverwriteRefusalMessage(path));
    }
}
