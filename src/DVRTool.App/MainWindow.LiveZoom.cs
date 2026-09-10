using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DVRTool.Core;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace DVRTool.App;

/// <summary>
/// The video panes' wheel zoom: scroll to magnify the camera under the pointer, drag to pan,
/// right-click to fit again, and a <b>1:1</b> button that magnifies until one camera pixel is
/// one screen pixel. The Live tab and the Playback tab each have one.
/// </summary>
/// <remarks>
/// <para>
/// This is a crop on the decoded picture, applied by LibVLC's own vout
/// (<c>libvlc_video_set_crop_geometry</c>, the four-border form) and computed by
/// <see cref="LiveZoom"/>. Nothing is asked of the recorder — no second stream, no PTZ, no
/// extra bandwidth — so it works over both transports, on all three vendors, and on a
/// recorded stream as readily as a live one. What it cannot do is invent detail: a sub-stream
/// tile zoomed 4× shows sub-stream pixels four times the size, which is why the useful move
/// is to maximize a camera first (that switches it to the main stream) and zoom that.
/// </para>
/// <para>
/// Each tab zooms one camera at a time — whichever pane the pointer is over — because the
/// zoom is a thing you do to look at something, not a per-tile setting to keep track of.
/// Pointing at a different pane and scrolling hands the zoom over and fits the old one again,
/// and any change of stream underneath (Play, a page turn, a maximize, a restore, another
/// channel on the Playback tab) drops it, since the crop is a property of the player rather
/// than of the media and would otherwise be inherited by the next camera to land there. The
/// Live and Playback panes are independent (<see cref="PaneZoom"/> per tab): zooming a
/// recording does not fit the live camera behind the other tab.
/// </para>
/// <para>
/// <b>1:1</b> is a zoom like any other, just computed rather than scrolled to: the factor at
/// which the pane's screen pixels and the picture's pixels line up
/// (<see cref="LiveZoom.OneToOne"/>, DPI-aware). It is a toggle that stays engaged while the
/// pane is resized — going fullscreen re-solves it for the new pane — and is "softly"
/// cancelled by the wheel: a notch in or out leaves the picture where the wheel put it and
/// merely un-presses the button, because the operator asked for a different magnification,
/// not for the whole picture back. A right-click still fits.
/// </para>
/// <para>
/// The mouse input arrives through the overlays rather than the video panes: a
/// <c>VideoView</c> is a hosted child window that WPF sees no input over, so the wheel, the
/// drag and the right-click are all handled on the almost-transparent overlay grid that sits
/// in front of each one. Pane pixels are turned into picture coordinates by
/// <see cref="LiveZoom.PickVisible"/>, so a zoom anchors on the point under the cursor rather
/// than on the middle of the letterboxed pane.
/// </para>
/// <para>
/// The crop window takes the <em>pane's</em> shape rather than the picture's
/// (<see cref="LiveZoom.VisibleSpan"/>), so zooming a camera whose shape does not match the
/// pane fills the black bars in with picture instead of magnifying them along with the rest.
/// A fitted pane is letterboxed exactly as before and the bars close as the zoom comes up. The
/// crop therefore depends on the pane's size, which is why a resize re-applies it even when
/// 1:1 is not holding it.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>What a pane's mouse handlers zoom: the player, a name for it, its tile if any.</summary>
    private sealed record ZoomTarget(MediaPlayer Player, string Label, LiveTile? Tile);

    /// <summary>One tab's zoom: which pane holds it, what it is, and the drag in progress.</summary>
    private sealed class PaneZoom
    {
        public LiveZoom Zoom = LiveZoom.None;
        public MediaPlayer? Player;
        public FrameworkElement? Pane;
        public LiveTile? Tile;
        public string Label = "";

        /// <summary>
        /// The picture's size as it was when this pane took the zoom. Captured once rather than
        /// read per notch: with a crop applied, the player reports the size of what it is
        /// displaying, and computing the next crop from that would compound it.
        /// </summary>
        public int Width, Height;

        /// <summary>The 1:1 button is pressed: the zoom follows the pane's size until the wheel moves it.</summary>
        public bool OneToOne;

        public Point DragFrom;
        public bool Dragging;

        public bool Holds(MediaPlayer player) => ReferenceEquals(Player, player);
        public bool IsZoomed => Zoom.IsZoomed;
    }

    private readonly PaneZoom _liveZoom = new();
    private readonly PaneZoom _playbackZoom = new();

    /// <summary>
    /// Gives a Live pane the zoom gestures. <paramref name="resolve"/> is asked on every gesture
    /// rather than captured, because a tile's player outlives neither the page nor the grid.
    /// </summary>
    private void WireLiveZoom(FrameworkElement pane, Func<ZoomTarget?> resolve) =>
        WireZoom(_liveZoom, pane, resolve);

    private void WireZoom(PaneZoom state, FrameworkElement pane, Func<ZoomTarget?> resolve)
    {
        pane.MouseWheel += (_, e) => OnZoomWheel(state, pane, resolve(), e);
        pane.MouseLeftButtonDown += (_, e) => OnZoomDragStart(state, pane, resolve(), e);
        pane.MouseMove += (_, e) => OnZoomDragMove(state, pane, e);
        pane.MouseLeftButtonUp += (_, e) => OnZoomDragEnd(state, pane, e);
        pane.MouseRightButtonUp += (_, e) => OnZoomFit(state, resolve(), e);
        // 1:1 is a property of the pane's size, so a resize (fullscreen, a splitter, a
        // window drag) re-solves it rather than leaving yesterday's factor pressed.
        pane.SizeChanged += (_, _) =>
        {
            if (_cleanupStarted || !ReferenceEquals(state.Pane, pane))
                return;
            if (state.OneToOne)
                ApplyOneToOne(state, quiet: true);
            else if (state.IsZoomed)
                // The crop follows the pane's shape, so a reshaped pane wants a new one —
                // otherwise the bars the zoom had closed reopen (or the picture stays
                // stretched into a shape the pane no longer has).
                ApplyZoom(state);
        };
    }

    private void OnZoomWheel(PaneZoom state, FrameworkElement pane, ZoomTarget? target, MouseWheelEventArgs e)
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
        TakeZoom(state, pane, target);
        if (state.Width <= 0 || state.Height <= 0)
        {
            // No picture yet, so no size to crop and no aspect to anchor on. Saying so beats
            // a wheel that silently does nothing.
            SetStatus($"{target.Label}: nothing to zoom yet — waiting for the first picture.");
            return;
        }

        // Off the picture and on the black bars there is no point to anchor on, so the zoom
        // works about the middle of what is showing instead of a made-up position.
        var hit = state.Zoom.PickVisible(e.GetPosition(pane).X, e.GetPosition(pane).Y,
            state.Width, state.Height, pane.ActualWidth, pane.ActualHeight);
        state.Zoom = state.Zoom.StepAt(notches, hit?.U ?? 0.5, hit?.V ?? 0.5);
        // The wheel softly cancels 1:1: the picture stays where the wheel put it, the button
        // just stops claiming it is pixel-exact.
        state.OneToOne = false;
        ApplyZoom(state);
        SetStatus(state.IsZoomed
            ? $"{target.Label}: zoom {state.Zoom.Describe()} — drag to pan, right-click to fit, " +
              "wheel down to zoom out."
            : $"{target.Label}: fit to the pane.");
    }

    private void OnZoomDragStart(PaneZoom state, FrameworkElement pane, ZoomTarget? target, MouseButtonEventArgs e)
    {
        // Not Handled: the tiles' select and double-click live on the same overlay, and a
        // drag that starts on an unzoomed pane is not a pan.
        if (target is null || !state.Holds(target.Player) || !state.IsZoomed)
            return;
        state.DragFrom = e.GetPosition(pane);
        state.Dragging = true;
        pane.CaptureMouse();
    }

    private void OnZoomDragMove(PaneZoom state, FrameworkElement pane, MouseEventArgs e)
    {
        if (!state.Dragging)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            OnZoomDragEnd(state, pane, e);
            return;
        }

        var now = e.GetPosition(pane);
        // In fractions of the displayed picture, not of the pane: a drag across a letterboxed
        // pane must move the picture by the same fraction whatever shape the pane is.
        var (width, height) = state.Zoom.DisplayedSize(state.Width, state.Height,
            pane.ActualWidth, pane.ActualHeight);
        if (width <= 0 || height <= 0)
            return;
        state.Zoom = state.Zoom.PanBy((now.X - state.DragFrom.X) / width, (now.Y - state.DragFrom.Y) / height);
        state.DragFrom = now;
        ApplyZoom(state);
    }

    private void OnZoomDragEnd(PaneZoom state, FrameworkElement pane, MouseEventArgs e)
    {
        if (!state.Dragging)
            return;
        state.Dragging = false;
        pane.ReleaseMouseCapture();
    }

    private void OnZoomFit(PaneZoom state, ZoomTarget? target, MouseButtonEventArgs e)
    {
        if (target is null || !state.Holds(target.Player) || !state.IsZoomed)
            return;
        e.Handled = true;
        state.Zoom = LiveZoom.None;
        state.OneToOne = false;
        ApplyZoom(state);
        SetStatus($"{target.Label}: fit to the pane.");
    }

    // ----- 1:1 -----

    private void OnLiveOneToOne(object sender, RoutedEventArgs e) =>
        ToggleOneToOne(_liveZoom, LiveOneToOneButton, ResolveLiveOneToOneTarget());

    private void OnPlaybackOneToOne(object sender, RoutedEventArgs e) =>
        ToggleOneToOne(_playbackZoom, PlaybackOneToOneButton,
            _playbackPlayer is { } player ? (PlaybackVideoOverlay, new ZoomTarget(player, "playback", null)) : null);

    /// <summary>
    /// The Live pane 1:1 applies to: the one already zoomed if there is one, else the single
    /// view, the maximized camera, or the selected tile — whichever is on screen.
    /// </summary>
    private (FrameworkElement Pane, ZoomTarget Target)? ResolveLiveOneToOneTarget()
    {
        if (_liveZoom.Player is { } held && _liveZoom.Pane is { IsVisible: true } heldPane)
            return (heldPane, new ZoomTarget(held, _liveZoom.Label, _liveZoom.Tile));
        if (!_gridMode)
            return _livePlayer is { } player
                ? (LiveVideoOverlay, new ZoomTarget(player, _liveLabel.Length > 0 ? _liveLabel : "live view", null))
                : null;
        if (_maxTile is { } max && _maxPlayer is { } maxPlayer && LiveMaxVideo.Content is FrameworkElement maxPane)
            return (maxPane, new ZoomTarget(maxPlayer, max.DefaultLabel, null));
        if (_selectedTile is { } tile && _tiles.Contains(tile))
            return (tile.Overlay, new ZoomTarget(tile.Player, tile.DefaultLabel, tile));
        return null;
    }

    private void ToggleOneToOne(PaneZoom state, ToggleButton button, (FrameworkElement Pane, ZoomTarget Target)? found)
    {
        if (_cleanupStarted)
            return;
        if (found is null)
        {
            button.IsChecked = false;
            SetStatus("1:1: nothing is playing to zoom.");
            return;
        }
        var (pane, target) = found.Value;
        if (state.OneToOne && state.Holds(target.Player))
        {
            // Pressed again: back to fit.
            state.Zoom = LiveZoom.None;
            state.OneToOne = false;
            ApplyZoom(state);
            SetStatus($"{target.Label}: fit to the pane.");
            return;
        }
        if (target.Tile is { } tile && _tiles.Contains(tile))
            SelectTile(tile);
        TakeZoom(state, pane, target);
        state.OneToOne = true;
        ApplyOneToOne(state, quiet: false);
    }

    /// <summary>
    /// Solves and applies the 1:1 factor for the pane's current size. Quiet for a resize,
    /// which happens dozens of times during a window drag and merits no status line.
    /// </summary>
    private void ApplyOneToOne(PaneZoom state, bool quiet)
    {
        if (state.Pane is not { } pane || state.Player is not { } player)
            return;
        if (state.Width <= 0 || state.Height <= 0)
            (state.Width, state.Height) = PictureSize(player);
        if (state.Width <= 0 || state.Height <= 0)
        {
            // No picture yet. The button stays pressed and the next stats tick tries again
            // (RetryPendingOneToOne): 1:1 on a camera that is still connecting should mean
            // "1:1 as soon as it shows", not "press me again later".
            SyncOneToOneButtons();
            if (!quiet)
                SetStatus($"{state.Label}: waiting for the first picture — 1:1 will apply when it arrives.");
            return;
        }

        double dpi = VisualTreeHelper.GetDpi(pane).DpiScaleX;
        var solved = state.Zoom.OneToOne(state.Width, state.Height, pane.ActualWidth, pane.ActualHeight, dpi);
        if (solved is null)
        {
            // The picture is already at or above life size: there is nothing to magnify, and
            // 1:1 is not a shrink. Fit it and say so.
            state.Zoom = LiveZoom.None;
            state.OneToOne = false;
            ApplyZoom(state);
            if (!quiet)
                SetStatus($"{state.Label}: the {state.Width}×{state.Height} picture already fits at or " +
                    "above one screen pixel per camera pixel — nothing to magnify.");
            return;
        }
        state.Zoom = solved.Value;
        ApplyZoom(state);
        if (quiet)
            return;
        bool exact = state.Zoom.IsOneToOne(state.Width, state.Height, pane.ActualWidth, pane.ActualHeight, dpi);
        SetStatus(exact
            ? $"{state.Label}: 1:1 — {state.Width}×{state.Height} at {state.Zoom.Describe()}; drag to pan, " +
              "wheel to zoom from here, right-click to fit."
            : $"{state.Label}: 1:1 wants more than the {LiveZoom.MaxFactor:0}× limit for a " +
              $"{state.Width}×{state.Height} picture in this pane — showing {state.Zoom.Describe()}. " +
              "Maximize the camera for the real thing.");
    }

    /// <summary>The two toolbars' 1:1 buttons show their pane's state, whichever way it was reached.</summary>
    private void SyncOneToOneButtons()
    {
        // Null during InitializeComponent: a combo's SelectionChanged fires while the XAML is
        // still being built, before the toolbars' buttons exist.
        if (_cleanupStarted || LiveOneToOneButton is null || PlaybackOneToOneButton is null)
            return;
        if (LiveOneToOneButton.IsChecked != _liveZoom.OneToOne)
            LiveOneToOneButton.IsChecked = _liveZoom.OneToOne;
        if (PlaybackOneToOneButton.IsChecked != _playbackZoom.OneToOne)
            PlaybackOneToOneButton.IsChecked = _playbackZoom.OneToOne;
    }

    /// <summary>
    /// A 1:1 that was pressed before the first picture: applies it once the picture's size is
    /// known. Called from the Live footer's and the Playback tab's timers, so it costs one size
    /// read per tick while pending and nothing otherwise.
    /// </summary>
    private void RetryPendingOneToOne(PaneZoom state)
    {
        if (!state.OneToOne || state.IsZoomed || state.Player is null || _cleanupStarted)
            return;
        if (state.Pane is not { IsVisible: true })
        {
            // The pane went away under the pending press (a grid page turn, a tab change): the
            // stream that arrives there is not the one the operator pressed 1:1 for.
            state.OneToOne = false;
            SyncOneToOneButtons();
            return;
        }
        ApplyOneToOne(state, quiet: false);
    }

    // ----- state -----

    /// <summary>
    /// Moves the zoom to this pane, fitting whatever held it before and reading the new
    /// picture's size.
    /// </summary>
    private void TakeZoom(PaneZoom state, FrameworkElement pane, ZoomTarget target)
    {
        if (state.Holds(target.Player))
        {
            // Same camera, but its size may only now be known (the first notch can land
            // before the first picture).
            if (state.Width <= 0 || state.Height <= 0)
                (state.Width, state.Height) = PictureSize(target.Player);
            state.Label = target.Label;
            return;
        }
        ResetZoom(state);
        state.Player = target.Player;
        state.Pane = pane;
        state.Tile = target.Tile;
        state.Label = target.Label;
        (state.Width, state.Height) = PictureSize(target.Player);
    }

    /// <summary>Pushes the current zoom to the player and shows the factor on the pane.</summary>
    private void ApplyZoom(PaneZoom state)
    {
        var player = state.Player;
        if (player is null)
            return;
        // Empty rather than null clears it — that is what libvlc itself passes down for
        // "no crop", and it keeps this off the marshalling of a null string.
        // The pane's size, not just the picture's: the crop window takes the pane's shape so
        // that a zoom fills the letterbox bars in with picture rather than magnifying them.
        var pane0 = state.Pane;
        SetCrop(player, state.Zoom.CropGeometry(state.Width, state.Height,
            pane0?.ActualWidth ?? 0, pane0?.ActualHeight ?? 0) ?? "");

        if (state.Pane is { } pane)
            pane.Cursor = state.IsZoomed ? Cursors.SizeAll : null;
        SyncOneToOneButtons();
        if (ReferenceEquals(state, _playbackZoom))
        {
            UpdatePlaybackVideoLabel();
            return;
        }
        // The maximized view and the single view name the factor on their own labels, which
        // is the only feedback there is in fullscreen. A tile says so too, unless a maximize
        // is using its label to explain itself.
        if (state.Tile is { } tile && _maxTile is null && _tiles.Contains(tile))
            tile.SetLabelNote(ZoomNote(state));
        UpdateLiveViewLabel();
        UpdateMaxOverlayLabel();
        UpdateLiveStats();
    }

    /// <summary>"1:1 (2×)" while the button holds, "2×" otherwise, "" when fitted.</summary>
    private static string ZoomNote(PaneZoom state) =>
        !state.IsZoomed ? "" : state.OneToOne ? $"1:1 ({state.Zoom.Describe()})" : state.Zoom.Describe();

    /// <summary>
    /// Fits whatever the Live tab has zoomed and forgets it. Called whenever a live stream
    /// changes underneath — the crop belongs to the player, so it would otherwise apply to the
    /// next camera the player is handed.
    /// </summary>
    private void ResetLiveZoom() => ResetZoom(_liveZoom);

    /// <summary>The Playback tab's equivalent: another channel, stream or device is not the same picture.</summary>
    private void ResetPlaybackZoom()
    {
        ResetZoom(_playbackZoom);
        if (!_cleanupStarted)
            UpdatePlaybackVideoLabel();
    }

    private void ResetZoom(PaneZoom state)
    {
        var player = state.Player;
        var pane = state.Pane;
        var tile = state.Tile;
        bool wasZoomed = state.IsZoomed;
        state.Zoom = LiveZoom.None;
        state.Player = null;
        state.Pane = null;
        state.Tile = null;
        state.Label = "";
        state.Width = state.Height = 0;
        state.Dragging = false;
        state.OneToOne = false;
        SyncOneToOneButtons();

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
    private (int Width, int Height) PictureSize(MediaPlayer player)
    {
        var read = ReadPictureSize(player);
        if (read.Width > 0 && read.Height > 0)
            return read;
        // The Live footer samples the same player every 250 ms and keeps what it last saw;
        // if it has a size and this read does not, the footer's is the picture on screen.
        if (_liveStatsSize is { } seen && ReferenceEquals(seen.Player, player))
            return (seen.Width, seen.Height);
        return (0, 0);
    }

    private static (int Width, int Height) ReadPictureSize(MediaPlayer player)
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

    // ----- labels -----

    /// <summary>The zoom factor for the footer, when the footer is describing the zoomed camera.</summary>
    private string LiveStatsZoomNote(MediaPlayer player) =>
        _liveZoom.IsZoomed && _liveZoom.Holds(player) ? $"  ·  zoom {ZoomNote(_liveZoom)}" : "";

    /// <summary>Whether the Live tab has this player zoomed (the footer's fps reads differently then).</summary>
    private bool IsLiveZoomed(MediaPlayer player) => _liveZoom.IsZoomed && _liveZoom.Holds(player);

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
            if (IsLiveZoomed(player))
                text += $"  —  zoom {ZoomNote(_liveZoom)}";
            if (_fullScreen)
                text += "  ·  F11 or Esc to leave fullscreen";
        }
        LiveVideoLabel.Text = text;
        LiveVideoLabel.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The Playback pane's corner label: only ever the zoom, since the toolbar names the rest.</summary>
    private void UpdatePlaybackVideoLabel()
    {
        if (PlaybackVideoLabel is null)
            return;
        string text = _playbackZoom.IsZoomed && _playbackPlayer is { } player && _playbackZoom.Holds(player)
            ? $"zoom {ZoomNote(_playbackZoom)}  ·  drag to pan, right-click to fit"
            : "";
        PlaybackVideoLabel.Text = text;
        PlaybackVideoLabel.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
