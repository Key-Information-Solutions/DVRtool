using System.Windows;
using System.Windows.Threading;
using DVRTool.Core;
using DVRTool.Vendors.HikvisionSdk;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace DVRTool.App;

/// <summary>
/// The footer stats block: codec, resolution, frames per second and received bitrate for
/// whichever camera the operator is looking at — the single view's stream, the tile they
/// clicked in the grid, or the maximized camera.
/// </summary>
/// <remarks>
/// <para>
/// The numbers come from LibVLC's per-media statistics, sampled once a second on the UI
/// thread and turned into rates over the last four seconds by <see cref="LiveStatsWindow"/>
/// (see <see cref="LiveStats"/> for why neither one second nor the raw counters will do).
/// They are therefore what the viewer received and decoded, the same over RTSP and over the
/// SDK port — which is the figure a tech wants when a picture stutters: a bitrate that tracks
/// the camera's configured rate with a full frame rate says the link is fine, a low or lumpy
/// bitrate says it is not, and drops with a healthy bitrate say the viewer is behind.
/// </para>
/// <para>
/// Which player is "selected" is decided afresh on every tick rather than tracked through
/// the grid's state machine: the maximized camera wins, on its main stream once that has a
/// picture and on its sub stream until then; otherwise the clicked tile; otherwise, outside
/// the grid, the single live player while it is playing. Rates reset whenever the source
/// changes or the player was handed a new media, because the counters restart with it.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private readonly DispatcherTimer _liveStatsTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>The native media the last sample came from; a different one resets the rates.</summary>
    private IntPtr _liveStatsMedia;

    private readonly LiveStatsWindow _liveStatsWindow = new();

    /// <summary>The grid tile the operator clicked, if any. Cleared when the grid rebuilds.</summary>
    private LiveTile? _selectedTile;

    /// <summary>What the single (non-grid) live view is playing, for the footer's label.</summary>
    private string _liveLabel = "";

    private void InitializeLiveStats()
    {
        LiveStatsText.Visibility = Visibility.Collapsed;
        _liveStatsTimer.Tick += (_, _) => UpdateLiveStats();
        _liveStatsTimer.Start();
    }

    /// <summary>Highlights one tile as the one the footer describes.</summary>
    private void SelectTile(LiveTile? tile)
    {
        if (ReferenceEquals(_selectedTile, tile))
            return;
        if (_selectedTile is { } old)
            old.SetSelected(false);
        _selectedTile = tile;
        tile?.SetSelected(true);
        UpdateLiveStats();
    }

    /// <summary>
    /// The player the footer should describe right now, or null for none. The view comes with
    /// it where there is one: the black-pane watchdog needs the window the player draws into,
    /// and a tile's is not worth tracking (the footer describes one camera at a time).
    /// </summary>
    private (MediaPlayer Player, string Label, SdkMediaStream? Sdk, VideoView? View)? ResolveLiveStatsSource()
    {
        // The dewarp has its own status line, over its own decoder. Whatever is still running
        // behind it — a maximized camera's tile is still on its sub stream — is not what the
        // operator is looking at, and describing it here would be a second, contradictory
        // reading of the same camera.
        if (_dewarpMode)
            return null;
        if (_maxTile is { } max)
        {
            if (LiveMaxVideo.Visibility == Visibility.Visible && _maxPlayer is { } big)
                return (big, $"{max.DefaultLabel}  ·  main", _maxSdk?.Media, LiveMaxVideo);
            return (max.Player, $"{max.DefaultLabel}  ·  sub", max.Sdk?.Media, null);
        }
        if (_gridMode)
        {
            if (_selectedTile is { } tile && _tiles.Contains(tile))
                return (tile.Player, $"{tile.DefaultLabel}  ·  sub", tile.Sdk?.Media, null);
            return null;
        }
        if (_livePlayer is { } player && _liveLabel.Length > 0)
        {
            // Outside the grid, a stopped player is "nothing selected" rather than a camera
            // with no stream: the label would be whatever played last.
            var state = player.State;
            if (state is VLCState.Stopped or VLCState.NothingSpecial)
                return null;
            return (player, _liveLabel, _sdkLive?.Media, LiveVideo);
        }
        return null;
    }

    private void UpdateLiveStats()
    {
        if (_cleanupStarted)
        {
            _liveStatsTimer.Stop();
            return;
        }

        var source = ResolveLiveStatsSource();
        if (source is null)
        {
            _liveStatsMedia = IntPtr.Zero;
            _liveStatsWindow.Reset();
            ShowLiveStats("");
            UpdateLiveViewLabel();
            return;
        }

        var (player, label, sdk, view) = source.Value;
        string text;
        try
        {
            text = SampleLiveStats(player, sdk, view);
        }
        catch (ObjectDisposedException)
        {
            text = "";
        }
        catch (VLCException)
        {
            text = "";
        }
        ShowLiveStats(text.Length > 0 ? $"{label}{LiveStatsZoomNote(player)}  ·  {text}" : "");
        UpdateLiveViewLabel();
        RetryPendingOneToOne(_liveZoom);
    }

    /// <summary>The picture size the footer last read, per player: the zoom's fallback when its own read is early.</summary>
    private (MediaPlayer Player, int Width, int Height)? _liveStatsSize;

    /// <summary>One reading of the player's counters, worded for the footer.</summary>
    private string SampleLiveStats(MediaPlayer player, SdkMediaStream? sdk, VideoView? view = null)
    {
        switch (player.State)
        {
            case VLCState.Error:
                return "stream failed";
            case VLCState.Ended:
                return sdk is { Stalled: true } ? "no video — the recorder went quiet" : "stream ended";
            case VLCState.Stopped:
            case VLCState.NothingSpecial:
                return "no stream";
        }

        // The wrapper is a fresh managed object over the player's current media each time;
        // the native media is retained for as long as the wrapper lives, so a stop on
        // another thread cannot pull it out from under this read.
        using var media = player.Media;
        if (media is null)
            return "connecting …";

        if (media.NativeReference != _liveStatsMedia)
        {
            _liveStatsMedia = media.NativeReference;
            _liveStatsWindow.Reset();
        }

        // DemuxReadBytes, not ReadBytes: over RTSP live555 is an access-demux and nothing
        // passes through the stream layer, so ReadBytes stays 0 for the whole session.
        var stats = media.Statistics;
        var sample = new LiveStatsSample(Environment.TickCount64,
            stats.DemuxReadBytes, stats.DecodedVideo, stats.DisplayedPictures, stats.LostPictures);
        LiveStatsRates? rates = _liveStatsWindow.Add(sample);

        string codec = "";
        int width = 0, height = 0;
        double configuredFps = 0;
        foreach (var track in media.Tracks)
        {
            if (track.TrackType != TrackType.Video)
                continue;
            codec = LiveStats.CodecName(track.Codec);
            width = (int)track.Data.Video.Width;
            height = (int)track.Data.Video.Height;
            // The encoder's declared rate — on Hikvision, the frame rate the camera is set to
            // (Site C: 90000/4500 on a 20 fps camera, 12000/1000 on a 12 fps one).
            if (track.Data.Video.FrameRateNum > 0 && track.Data.Video.FrameRateDen > 0)
                configuredFps = (double)track.Data.Video.FrameRateNum / track.Data.Video.FrameRateDen;
            break;
        }
        // The track header can lag the picture, or report the SPS size before cropping; the
        // output's own size is what is on screen — except under a digital zoom, where the
        // output is showing a crop and the size worth reporting is still the stream's.
        uint px = 0, py = 0;
        if (!IsLiveZoomed(player) &&
            player.Size(0, ref px, ref py) && px > 0 && py > 0)
        {
            width = (int)px;
            height = (int)py;
        }

        if (width > 0 && height > 0)
            _liveStatsSize = (player, width, height);
        if (rates is null && codec.Length == 0 && width == 0 && stats.DemuxReadBytes == 0)
            return "connecting …";
        // A pane that is black while these numbers look healthy is the one failure the footer
        // would otherwise describe as success.
        string described = LiveStats.Describe(codec, width, height, rates, sdk?.BytesDropped ?? 0, configuredFps);
        string blind = WatchPainting(player, media, stats, view);
        return blind.Length > 0 ? $"{described}  ·  {blind}" : described;
    }

    private void ShowLiveStats(string text)
    {
        LiveStatsText.Text = text;
        LiveStatsText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
