using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DVRTool.Core;

/// <summary>
/// Outcome of a remux. <see cref="RefusedOverwrite"/> is a failure the operator has to be
/// able to tell apart from an ffmpeg failure: the media was produced correctly and only
/// the promotion was refused, so the advice ("re-run", "install ffmpeg") is different.
/// </summary>
public sealed record RemuxResult(bool Success, string Output)
{
    public bool RefusedOverwrite { get; init; }
}

/// <summary>
/// Lossless remux via ffmpeg on PATH. Always "-c copy": this is evidence footage, so
/// the bitstream is never re-encoded — only the container around it is rebuilt.
/// <para>
/// Needed for Dahua .dav (DHAV), and just as much for Hikvision, whose ISAPI downloads
/// are MPEG program streams regardless of the ".mp4" name the device implies.
/// </para>
/// </summary>
public static partial class Remux
{
    /// <param name="force">
    /// Permission to replace an existing <paramref name="outputPath"/>. Defaults to the
    /// protective answer, so a caller that forgets it cannot destroy an export.
    /// </param>
    public static async Task<RemuxResult> RemuxAsync(
        string inputPath, string outputPath, RemuxContainer container, bool force = false,
        CancellationToken ct = default)
    {
        // ffmpeg truncates its output the moment the muxer opens it, so it is never
        // pointed at the destination: a crash mid-write would otherwise leave a
        // truncated file under the exact name the operator treats as the finished export.
        string stagingPath = DownloadPaths.StagingPathFor(outputPath);
        string input = Path.GetFullPath(inputPath);
        if (string.Equals(input, Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, Path.GetFullPath(stagingPath), StringComparison.OrdinalIgnoreCase))
            return new RemuxResult(false, $"refusing to remux {inputPath} onto itself");

        string? videoCodec = await ProbeVideoCodecAsync(inputPath, ct);
        var args = BuildArguments(inputPath, stagingPath, container, videoCodec);
        var (ok, output) = await RunAsync(args, stagingPath, ct);
        if (!ok && args.Contains("hvc1") && IsCodecTagRejection(output))
        {
            // The probe said HEVC but the muxer disagreed. A tag mismatch leaves an
            // unreadable stub behind; drop it and retry untagged rather than failing on a
            // detail the operator can do nothing about.
            TryDelete(stagingPath);
            var untagged = BuildArguments(inputPath, stagingPath, container, videoCodec: null);
            (ok, output) = await RunAsync(untagged, stagingPath, ct);
        }
        if (!ok)
            return new RemuxResult(false, output);

        // Re-checked here, not only by the caller before the download: minutes of transfer
        // and remux sit between the two, and a file can land at the destination in that
        // window — a second operator on the same case name, a backup job, a colleague's
        // script. Promoting over it would destroy an export that may already be evidence.
        if (!force && File.Exists(outputPath))
        {
            TryDelete(stagingPath);
            return RefuseOverwrite(outputPath);
        }

        try
        {
            // overwrite: force, so the sliver between that check and this move cannot
            // reopen the hole — without it the move itself refuses an existing destination.
            File.Move(stagingPath, outputPath, overwrite: force);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(stagingPath);
            return !force && File.Exists(outputPath)
                ? RefuseOverwrite(outputPath) // lost that sliver: a refusal, not an I/O fault
                : new RemuxResult(false,
                    $"remuxed, but could not move {stagingPath} to {outputPath}: {ex.Message}");
        }
        return new RemuxResult(true, output);
    }

    private static RemuxResult RefuseOverwrite(string outputPath) =>
        new(false, DownloadPaths.OverwriteRefusalMessage(outputPath)) { RefusedOverwrite = true };

    /// <summary>
    /// ffmpeg command line for a stream copy of <paramref name="inputPath"/> into
    /// <paramref name="container"/>.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string inputPath, string outputPath,
        RemuxContainer container, string? videoCodec)
    {
        List<string> args = ["-y", "-loglevel", "error", "-i", inputPath, "-c", "copy"];
        if (container == RemuxContainer.Mp4)
        {
            // ffmpeg tags HEVC in MP4 as "hev1", which QuickTime, iOS and Windows
            // Photos refuse to play; "hvc1" is the tag they accept. Applying it to any
            // other codec is a hard mux failure, so it is gated on the probe.
            if (string.Equals(videoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
                args.AddRange(["-tag:v", "hvc1"]);
            // moov at the front, so the file opens without reading it end-to-end.
            args.AddRange(["-movflags", "+faststart"]);
        }
        args.AddRange(["-f", container == RemuxContainer.Mp4 ? "mp4" : "matroska", outputPath]);
        return args;
    }

    /// <summary>
    /// Video codec name reported by ffmpeg ("hevc", "h264", …), or null if it could not
    /// be determined. ffprobe is not assumed to exist; "ffmpeg -i" with no output file
    /// prints the same stream table to stderr and exits non-zero by design.
    /// </summary>
    public static async Task<string?> ProbeVideoCodecAsync(string inputPath,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception)
        {
            return null; // Missing ffmpeg is reported by the remux itself, once.
        }
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return ParseVideoCodec(stderr);
    }

    public static string? ParseVideoCodec(string ffmpegOutput)
    {
        var match = VideoStreamPattern().Match(ffmpegOutput);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>True when ffmpeg rejected the codec tag rather than the media itself.</summary>
    public static bool IsCodecTagRejection(string ffmpegOutput) =>
        ffmpegOutput.Contains("incompatible with output codec", StringComparison.OrdinalIgnoreCase);

    private static async Task<(bool Success, string Output)> RunAsync(
        IReadOnlyList<string> args, string outputPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (false, $"ffmpeg not found on PATH: {ex.Message}");
        }

        try
        {
            string stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode == 0)
                return (true, stderr);
            // A failed mux still creates the output file, and it is unplayable —
            // leaving it behind would look like a successful export.
            TryDelete(outputPath);
            return (false, stderr);
        }
        catch (OperationCanceledException)
        {
            // Canceling the waits does not stop ffmpeg — it would keep running
            // detached, holding locks on the output file. Kill it and wait for the
            // handles to release before the caller observes the cancellation.
            bool killed = false;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                    killed = true;
                }
            }
            catch (InvalidOperationException) { } // exited between the check and Kill
            if (killed)
            {
                // A killed "-c copy" remux has no trailer and is unplayable.
                TryDelete(outputPath);
            }
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"Stream #\d+:\d+.*?: Video: ([A-Za-z0-9_]+)")]
    private static partial Regex VideoStreamPattern();
}
