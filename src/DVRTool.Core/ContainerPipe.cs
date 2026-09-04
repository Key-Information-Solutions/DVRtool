using System.Diagnostics;

namespace DVRTool.Core;

/// <summary>
/// Rewraps a media body from one container to another as it streams, through ffmpeg on
/// PATH — a stream copy, never a re-encode.
/// </summary>
/// <remarks>
/// <para>
/// Exists for Dahua. Its recorders stream footage as DHAV (.dav), and the LibVLC build the
/// GUI ships (VideoLAN.LibVLC.Windows 3.0.x) carries no avformat demuxer at all — the
/// <c>plugins/demux</c> folder has VLC's own PS, TS, MKV and MP4 demuxers and nothing that
/// reads DHAV. ffmpeg does (<c>-f dhav</c>, since 4.2), and the export path already
/// depends on it (<see cref="Remux"/>), so the footage goes through ffmpeg into MPEG-TS on
/// its way to the decoder. Audio is dropped: Dahua records G.711, which MPEG-TS cannot
/// carry without private tagging that VLC then ignores anyway.
/// </para>
/// <para>
/// Back-pressure runs the whole way: the decoder reads ffmpeg's stdout as it needs bytes,
/// ffmpeg reads its stdin as its output drains, and the pump feeding stdin blocks on the
/// recorder's socket in turn. Pausing the decoder therefore pauses the download.
/// </para>
/// </remarks>
public sealed class ContainerPipe : IDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly Task<string> _stderr;

    private ContainerPipe(Process process, Stream source)
    {
        _process = process;
        _stderr = process.StandardError.ReadToEndAsync();
        _pump = PumpAsync(source, process.StandardInput.BaseStream, _cts.Token);
    }

    /// <summary>ffmpeg's stdout: the rewrapped media. Forward-only.</summary>
    public Stream Output => _process.StandardOutput.BaseStream;

    /// <summary>What ffmpeg said on stderr once it has exited; empty while it runs.</summary>
    public string Diagnostics => _stderr.IsCompletedSuccessfully ? _stderr.Result : "";

    public bool HasExited => _process.HasExited;

    /// <summary>The ffmpeg command line for one rewrap, exposed for tests.</summary>
    public static IReadOnlyList<string> BuildArguments(string inputFormat, string outputFormat) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin",
        "-f", inputFormat, "-i", "pipe:0",
        "-c:v", "copy", "-an",
        "-f", outputFormat, "pipe:1",
    ];

    /// <summary>
    /// Starts ffmpeg reading <paramref name="source"/> as <paramref name="inputFormat"/> and
    /// writing <paramref name="outputFormat"/> to <see cref="Output"/>. Throws
    /// <see cref="NvrException"/> when ffmpeg is not on PATH.
    /// </summary>
    public static ContainerPipe Start(Stream source, string inputFormat, string outputFormat)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in BuildArguments(inputFormat, outputFormat))
            psi.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new NvrException("ffmpeg did not start");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new NvrException(
                "Dahua footage is DHAV, which needs ffmpeg on PATH to play — ffmpeg was not " +
                $"found: {ex.Message}", inner: ex);
        }
        return new ContainerPipe(process, source);
    }

    private static async Task PumpAsync(Stream source, Stream stdin, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int n = await source.ReadAsync(buffer, ct);
                if (n == 0)
                    break;
                await stdin.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }
        catch (Exception)
        {
            // ffmpeg exited (its stdin closed under us), the source failed, or we were
            // disposed — all of which end the pump the same way.
        }
        finally
        {
            try { stdin.Close(); }
            catch (Exception) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }
        try { _process.StandardOutput.BaseStream.Dispose(); }
        catch (Exception) { }
        _process.Dispose();
        _cts.Dispose();
    }
}
