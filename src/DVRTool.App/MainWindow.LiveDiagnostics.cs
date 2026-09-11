using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using DVRTool.Core.Updates;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace DVRTool.App;

/// <summary>
/// The black-pane watchdog: says out loud when video is arriving and nothing is reaching the
/// screen, names which half has failed, and repairs the one cause it can prove.
/// </summary>
/// <remarks>
/// <para>
/// A pane that stays black while the footer counts a healthy bitrate is the worst failure this
/// tab has, because every reading an operator can see says the connection is fine. The three
/// ways it happens are worth telling apart, and LibVLC's own counters do tell them apart:
/// bytes arriving with <b>nothing decoded</b> is a codec or a stream the demuxer cannot read;
/// pictures decoded with <b>nothing displayed</b> is the video output; and neither is a
/// connection problem at all. Note that <c>DecodedVideo</c> counts twice per frame — once per
/// packet in, once per picture out (see <see cref="LiveStats"/>) — so it is a poor witness on
/// its own and <c>DisplayedPictures</c> is the one that says the pane is painting.
/// </para>
/// <para>
/// The repairable cause is a stale window handle. A <c>VideoView</c> hands LibVLC the handle
/// of the <c>HwndHost</c> in its template and never mentions it again; if WPF rebuilds that
/// child window — which it does whenever the element leaves and re-enters the visual tree —
/// the player goes on decoding into a window that no longer exists. Nothing in LibVLCSharp
/// notices, and no error is raised anywhere: the picture simply stops arriving, permanently,
/// and every later Play inherits it. So the handle is compared against the host's own before
/// anything else is blamed, and a mismatch is re-attached and reported rather than described.
/// </para>
/// <para>
/// Everything here is observation. The watchdog never stops a stream, never re-opens one
/// behind the operator's back (a re-open costs a login and one of the recorder's stream
/// slots), and costs one counter read per second on the camera the footer is already
/// describing.
/// </para>
/// <para>
/// <c>DVRTOOL_LOG=1</c> additionally writes LibVLC's own log to
/// <c>%APPDATA%\DVRTool\logs</c>, which is what to ask for when a site reports a black pane
/// this cannot explain: the vout's module selection and its errors are in there and nowhere
/// else.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>How long a stream may deliver bytes with nothing on screen before it is called out.</summary>
    private static readonly TimeSpan PaintGrace = TimeSpan.FromSeconds(8);

    /// <summary>One media's paint history: the same media is judged once, not every tick.</summary>
    private sealed class PaintWatch
    {
        public IntPtr Media;
        public long Started;
        public bool Reported;
        public bool Repaired;
    }

    private readonly PaintWatch _paintWatch = new();

    private StreamWriter? _vlcLog;

    // ----- the watchdog -----

    /// <summary>
    /// Judges one sample of the camera the footer is describing. Returns a note for the footer
    /// when the picture is not reaching the pane, and an empty string while all is well.
    /// </summary>
    private string WatchPainting(MediaPlayer player, Media media, MediaStats stats, VideoView? view)
    {
        if (_cleanupStarted)
            return "";

        // A different media is a different verdict: the counters restart with it.
        if (media.NativeReference != _paintWatch.Media)
        {
            _paintWatch.Media = media.NativeReference;
            _paintWatch.Started = Environment.TickCount64;
            _paintWatch.Reported = false;
            _paintWatch.Repaired = false;
            return "";
        }
        if (stats.DisplayedPictures > 0)
            return "";
        if (stats.DemuxReadBytes <= 0)
            return ""; // nothing has arrived yet — that is "connecting", not "not painting"
        if (Environment.TickCount64 - _paintWatch.Started < PaintGrace.TotalMilliseconds)
            return "";

        // A window that was rebuilt under the player is both the likeliest cause and the only
        // one that can be put right from here, so it is tested before anything is blamed.
        if (!_paintWatch.Repaired && view is not null && ReattachStaleWindow(view, player))
        {
            _paintWatch.Repaired = true;
            _paintWatch.Started = Environment.TickCount64;
            SetStatus("The video pane's window had been rebuilt under the player, so the " +
                "picture was going nowhere. Re-attached — press ▶ Play to restart the stream " +
                "into it.");
            Diagnostic($"stale hwnd re-attached for {player.Hwnd}");
            return "the pane's window was rebuilt — press Play";
        }

        if (_paintWatch.Reported)
            return stats.DecodedVideo > 0 ? "decoded but not painted" : "arriving but not decoding";
        _paintWatch.Reported = true;

        if (stats.DecodedVideo > 0)
        {
            SetStatus("Video is arriving and decoding, but no picture is reaching the pane — " +
                "this is the video output, not the recorder or the link. Run DVRTool with " +
                "DVRTOOL_LOG=1 and send the log in %APPDATA%\\DVRTool\\logs.");
            Diagnostic($"decoded {stats.DecodedVideo} pictures, displayed none, " +
                $"vouts={player.VoutCount}, crop='{SafeCrop(player)}'");
            return "decoded but not painted";
        }

        SetStatus("Video is arriving but nothing is decoding — the stream is reaching us and " +
            "the decoder is making nothing of it. Run DVRTool with DVRTOOL_LOG=1 and send the " +
            "log in %APPDATA%\\DVRTool\\logs.");
        Diagnostic($"read {stats.DemuxReadBytes} bytes, decoded nothing, state={player.State}");
        return "arriving but not decoding";
    }

    private static string SafeCrop(MediaPlayer player)
    {
        try { return player.CropGeometry ?? ""; }
        catch (ObjectDisposedException) { return "?"; }
        catch (VLCException) { return "?"; }
    }

    /// <summary>
    /// Hands the player the pane's current window handle if it is holding an older one.
    /// True when it had to, which is the proof that this was the failure.
    /// </summary>
    private bool ReattachStaleWindow(VideoView view, MediaPlayer player)
    {
        var host = FindHwndHost(view);
        if (host is null)
            return false;
        IntPtr live;
        try { live = host.Handle; }
        catch (InvalidOperationException) { return false; }
        if (live == IntPtr.Zero || live == player.Hwnd)
            return false;
        lock (_playerLock)
        {
            if (_playersDisposed)
                return false;
            try { player.Hwnd = live; }
            catch (ObjectDisposedException) { return false; }
            catch (VLCException) { return false; }
        }
        return true;
    }

    /// <summary>The <c>HwndHost</c> in a VideoView's template — the window LibVLC draws into.</summary>
    private static HwndHost? FindHwndHost(DependencyObject root)
    {
        if (root is HwndHost host)
            return host;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            if (FindHwndHost(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        return null;
    }

    // ----- the log -----

    /// <summary>
    /// Opens LibVLC's log when <c>DVRTOOL_LOG</c> is set. Off by default: libvlc's debug log is
    /// a few megabytes a minute per stream, and it is wanted only when something is wrong.
    /// </summary>
    private void InitializeDiagnostics()
    {
        if (Environment.GetEnvironmentVariable("DVRTOOL_LOG") is not { Length: > 0 } setting ||
            setting is "0" or "false")
            return;
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DVRTool", "logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"dvrtool-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _vlcLog = new StreamWriter(path, append: false) { AutoFlush = true };
            Diagnostic($"DVRTool {ProductVersion.Format(ProductVersion.Current)} logging to {path}");
            if (_libVlc is not null)
                _libVlc.Log += OnVlcLog;
            SetStatus($"Diagnostic log: {path}");
        }
        catch (IOException)
        {
            // A log we cannot open is not a reason to fail to start.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void OnVlcLog(object? sender, LogEventArgs e) =>
        Diagnostic($"{e.Level} {e.Module}: {e.Message}");

    /// <summary>One line into the diagnostic log, from any thread; nothing when it is off.</summary>
    private void Diagnostic(string line)
    {
        var log = _vlcLog;
        if (log is null)
            return;
        try
        {
            lock (log)
                log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void DisposeDiagnostics()
    {
        var log = _vlcLog;
        _vlcLog = null;
        if (log is null)
            return;
        if (_libVlc is not null)
            _libVlc.Log -= OnVlcLog;
        try
        {
            lock (log)
                log.Dispose();
        }
        catch (IOException) { }
    }
}
