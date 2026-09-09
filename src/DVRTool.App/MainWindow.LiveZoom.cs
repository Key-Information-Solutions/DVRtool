using System.Windows;
using System.Windows.Input;
using DVRTool.Core;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace DVRTool.App;

/// <summary>
/// The live view's wheel zoom: scroll to magnify the camera under the pointer, drag to pan,
/// right-click to fit again.
/// </summary>
/// <remarks>
/// <para>
/// This is a crop on the decoded picture, applied by LibVLC's own vout
/// (<c>libvlc_video_set_crop_geometry</c>, the <c>WxH+X+Y</c> form) and computed by
/// <see cref="LiveZoom"/>. Nothing is asked of the recorder — no second stream, no PTZ, no
/// extra bandwidth — so it works over both transports, on all three vendors, and on a
/// recorded stream as readily as a live one. What it cannot do is invent detail: a sub-stream
/// tile zoomed 4× shows sub-stream pixels four times the size, which is why the useful move
/// is to maximize a camera first (that switches it to the main stream) and zoom that.
/// </para>
/// <para>
/// One camera is zoomed at a time — whichever pane the pointer is over — because the zoom is
/// a thing you do to look at something, not a per-tile setting to keep track of. Pointing at
/// a different pane and scrolling hands the zoom over and fits the old one again, and any
/// change of stream underneath (Play, a page turn, a maximize, a restore) drops it, since the
/// crop is a property of the player rather than of the media and would otherwise be inherited
/// by the next camera to land there.
/// </para>
/// <para>
/// The mouse input arrives through the overlays rather than the video panes: a
/// <c>VideoView</c> is a hosted child window that WPF sees no input over, so the wheel, the
/// drag and the right-click are all handled on the almost-transparent overlay grid that sits
/// in front of each one (the tiles' and the maximized view's already exist to carry their
/// labels and the double-click; the single view got one for this). Pane pixels are turned
/// into picture coordinates by <see cref="LiveZoom.Pick"/>, so a zoom anchors on the point
/// under the cursor rather than on the middle of the letterboxed pane.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>What a pane's mouse handlers zoom: the player, a name for it, its tile if any.</summary>
    private sealed record ZoomTarget(MediaPlayer Player, string Label, LiveTile? Tile);

    private LiveZoom _zoom = LiveZoom.None;
    private MediaPlayer? _zoomPlayer;
    private FrameworkElement? _zoomPane;
    private LiveTile? _zoomTile;

    /// <summary>
    /// The picture's size as it was when this pane took the zoom. Captured once rather than
    /// read per notch: with a crop applied, the player reports the size of what it is
    /// displaying, and computing the next crop from that would compound it.
    /// </summary>
    private int _zoomWidth, _zoomHeight;

    private Point _zoomDragFrom;
    private bool _zoomDragging;

    /// <summary>
    /// Gives a pane the zoom gestures. <paramref name="resolve"/> is asked on every gesture
    /// rather than captured, because a tile's player outlives neither the page nor the grid.
    /// </summary>
    private void WireLiveZoom(FrameworkElement pane, Func<ZoomTarget?> resolve)
    {
        pane.MouseWheel += (_, e) => OnZoomWheel(pane, resolve(), e);
        pane.MouseLeftButtonDown += (_, e) => OnZoomDragStart(pane, resolve(), e);
        pane.MouseMove += (_, e) => OnZoomDragMove(pane, e);
        pane.MouseLeftButtonUp += (_, e) => OnZoomDragEnd(pane, e);
        pane.MouseRightButtonUp += (_, e) => OnZoomFit(resolve(), e);
    }

    private void OnZoomWheel(FrameworkElement pane, ZoomTarget? target, MouseWheelEventArgs e)
    {
        if (target is null || _cleanupStarted)
            return;
        e.Handled = true;
        int notches = (int)Math.Round(e.Delta / (double)Mouse.MouseWheelDeltaForOneLine);
        if (notches == 0)
            notches = Math.Sign(e.Delta);
        if (notches == 0)
            return;

        // The footer follows whatever is being zoomed, the same as a click would.
        if (target.Tile is { } tile && _tiles.Contains(tile))
            SelectTile(tile);
        TakeZoom(pane, target);
        if (_zoomWidth <= 0 || _zoomHeight <= 0)
        {
            // No picture yet, so no size to crop and no aspect to anchor on. Saying so beats
            // a wheel that silently does nothing.
            SetStatus($"{target.Label}: nothing to zoom yet — waiting for the first picture.");
            return;
        }

        // Off the picture and on the black bars there is no point to anchor on, so the zoom
        // works about the middle of what is showing instead of a made-up position.
        var hit = LiveZoom.Pick(e.GetPosition(pane).X, e.GetPosition(pane).Y,
            pane.ActualWidth, pane.ActualHeight, (double)_zoomWidth / _zoomHeight);
        _zoom = _zoom.StepAt(notches, hit?.U ?? 0.5, hit?.V ?? 0.5);
        ApplyZoom();
        SetStatus(_zoom.IsZoomed
            ? $"{target.Label}: zoom {_zoom.Describe()} — drag to pan, right-click to fit, " +
              "wheel down to zoom out."
            : $"{target.Label}: fit to the pane.");
    }

    private void OnZoomDragStart(FrameworkElement pane, ZoomTarget? target, MouseButtonEventArgs e)
    {
        // Not Handled: the tiles' select and double-click live on the same overlay, and a
        // drag that starts on an unzoomed pane is not a pan.
        if (target is null || !ReferenceEquals(_zoomPlayer, target.Player) || !_zoom.IsZoomed)
            return;
        _zoomDragFrom = e.GetPosition(pane);
        _zoomDragging = true;
        pane.CaptureMouse();
    }

    private void OnZoomDragMove(FrameworkElement pane, MouseEventArgs e)
    {
        if (!_zoomDragging)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            OnZoomDragEnd(pane, e);
            return;
        }

        var now = e.GetPosition(pane);
        // In fractions of the displayed picture, not of the pane: a drag across a letterboxed
        // pane must move the picture by the same fraction whatever shape the pane is.
        var (width, height) = LiveZoom.Fit(pane.ActualWidth, pane.ActualHeight,
            _zoomHeight > 0 ? (double)_zoomWidth / _zoomHeight : 0);
        if (width <= 0 || height <= 0)
            return;
        _zoom = _zoom.PanBy((now.X - _zoomDragFrom.X) / width, (now.Y - _zoomDragFrom.Y) / height);
        _zoomDragFrom = now;
        ApplyZoom();
    }

    private void OnZoomDragEnd(FrameworkElement pane, MouseEventArgs e)
    {
        if (!_zoomDragging)
            return;
        _zoomDragging = false;
        pane.ReleaseMouseCapture();
    }

    private void OnZoomFit(ZoomTarget? target, MouseButtonEventArgs e)
    {
        if (target is null || !ReferenceEquals(_zoomPlayer, target.Player) || !_zoom.IsZoomed)
            return;
        e.Handled = true;
        _zoom = LiveZoom.None;
        ApplyZoom();
        SetStatus($"{target.Label}: fit to the pane.");
    }

    /// <summary>
    /// Moves the zoom to this pane, fitting whatever held it before and reading the new
    /// picture's size.
    /// </summary>
    private void TakeZoom(FrameworkElement pane, ZoomTarget target)
    {
        if (ReferenceEquals(_zoomPlayer, target.Player))
        {
            // Same camera, but its size may only now be known (the first notch can land
            // before the first picture).
            if (_zoomWidth <= 0 || _zoomHeight <= 0)
                (_zoomWidth, _zoomHeight) = PictureSize(target.Player);
            return;
        }
        ResetLiveZoom();
        _zoomPlayer = target.Player;
        _zoomPane = pane;
        _zoomTile = target.Tile;
        (_zoomWidth, _zoomHeight) = PictureSize(target.Player);
    }

    /// <summary>Pushes the current zoom to the player and shows the factor on the pane.</summary>
    private void ApplyZoom()
    {
        var player = _zoomPlayer;
        if (player is null)
            return;
        // Empty rather than null clears it — that is what libvlc itself passes down for
        // "no crop", and it keeps this off the marshalling of a null string.
        SetCrop(player, _zoom.CropGeometry(_zoomWidth, _zoomHeight) ?? "");

        if (_zoomPane is { } pane)
            pane.Cursor = _zoom.IsZoomed ? Cursors.SizeAll : null;
        // The maximized view and the single view name the factor on their own labels, which
        // is the only feedback there is in fullscreen. A tile says so too, unless a maximize
        // is using its label to explain itself.
        if (_zoomTile is { } tile && _maxTile is null && _tiles.Contains(tile))
            tile.SetLabelNote(_zoom.Describe());
        UpdateLiveViewLabel();
        UpdateMaxOverlayLabel();
        UpdateLiveStats();
    }

    /// <summary>
    /// Fits whatever is zoomed and forgets it. Called whenever a stream changes underneath —
    /// the crop belongs to the player, so it would otherwise apply to the next camera the
    /// player is handed.
    /// </summary>
    private void ResetLiveZoom()
    {
        var player = _zoomPlayer;
        var pane = _zoomPane;
        var tile = _zoomTile;
        bool wasZoomed = _zoom.IsZoomed;
        _zoom = LiveZoom.None;
        _zoomPlayer = null;
        _zoomPane = null;
        _zoomTile = null;
        _zoomWidth = _zoomHeight = 0;
        _zoomDragging = false;

        if (pane is not null)
        {
            pane.Cursor = null;
            if (pane.IsMouseCaptured)
                pane.ReleaseMouseCapture();
        }
        if (tile is not null && wasZoomed && _maxTile is null && _tiles.Contains(tile))
            tile.SetLabelNote("");
        if (player is null || !wasZoomed)
            return;
        SetCrop(player, "");
    }

    /// <summary>
    /// Hands a crop to a player, or clears it with the empty string. Under the player lock and
    /// behind the shutdown flag, like every other native call on these players: a crop set on
    /// a player that has just been disposed is a dereference of a released handle, not an
    /// exception.
    /// </summary>
    private void SetCrop(MediaPlayer player, string geometry)
    {
        if (_cleanupStarted)
            return;
        lock (_playerLock)
        {
            if (_playersDisposed)
                return;
            try { player.CropGeometry = geometry; }
            catch (ObjectDisposedException) { }
            catch (VLCException) { }
        }
    }

    /// <summary>
    /// The decoded picture's size, from the video output if it has one and from the media's
    /// video track otherwise; (0, 0) when neither knows yet.
    /// </summary>
    private static (int Width, int Height) PictureSize(MediaPlayer player)
    {
        try
        {
            uint w = 0, h = 0;
            if (player.Size(0, ref w, ref h) && w > 0 && h > 0)
                return ((int)w, (int)h);
            using var media = player.Media;
            if (media is null)
                return (0, 0);
            foreach (var track in media.Tracks)
            {
                if (track.TrackType != TrackType.Video)
                    continue;
                if (track.Data.Video.Width > 0 && track.Data.Video.Height > 0)
                    return ((int)track.Data.Video.Width, (int)track.Data.Video.Height);
                break;
            }
        }
        catch (ObjectDisposedException) { }
        catch (VLCException) { }
        return (0, 0);
    }

    /// <summary>The zoom factor for the footer, when the footer is describing the zoomed camera.</summary>
    private string LiveStatsZoomNote(MediaPlayer player) =>
        _zoom.IsZoomed && ReferenceEquals(_zoomPlayer, player) ? $"  ·  zoom {_zoom.Describe()}" : "";

    /// <summary>
    /// The single view's corner label. It exists for fullscreen, where the status bar is gone
    /// and nothing else says which camera this is or that it is zoomed.
    /// </summary>
    private void UpdateLiveViewLabel()
    {
        if (_cleanupStarted)
            return;
        string text = "";
        if (!_gridMode && _liveLabel.Length > 0 && _livePlayer is { } player &&
            player.State is not (VLCState.Stopped or VLCState.NothingSpecial or VLCState.Error))
        {
            text = _liveLabel;
            if (_zoom.IsZoomed && ReferenceEquals(_zoomPlayer, player))
                text += $"  —  zoom {_zoom.Describe()}";
            if (_fullScreen)
                text += "  ·  F11 or Esc to leave fullscreen";
        }
        LiveVideoLabel.Text = text;
        LiveVideoLabel.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
