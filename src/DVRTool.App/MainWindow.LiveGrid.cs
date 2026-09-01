using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DVRTool.Core;
using DVRTool.Vendors.HikvisionSdk;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace DVRTool.App;

/// <summary>
/// The Live tab's grid mode: every camera of the selected system at once, sub streams in a
/// paged 4×4, and a double-click that brings one camera up full-size on its main stream.
/// </summary>
/// <remarks>
/// <para>
/// This was shaped by measurement rather than by taste (<c>docs/hikvision-sdk-live.md</c> §7).
/// One SDK login carries every preview — each <c>NET_DVR_RealPlay_V40</c> is its own TCP
/// connection to the SDK port, exactly as iVMS-4200 does it — and the SDK side of sixteen
/// streams costs nothing measurable. What costs is LibVLC: each tile is an independent
/// player at roughly 85 threads and 35–40 MB, flat to 20 tiles and then a CPU cliff at 21 on
/// a fast workstation. So the grid is paged at <see cref="LiveGridLayout.MaxTilesPerPage"/>,
/// tiles are started a beat apart to spread the keyframe burst, and every tile media carries
/// the single-thread decoder option without which a 20 fps stream drops every frame after
/// the first (<see cref="MainWindow.AddLiveDecodeOptions"/>).
/// </para>
/// <para>
/// Maximizing does not tear the grid down. Starting a main-stream preview on the running
/// session was measured at ~50 ms and played at full rate beside sixteen tiles, so the
/// sixteen keep running underneath and coming back is instant — a tech flipping between
/// cameras never waits for a page of keyframes. The tiles are collapsed rather than removed,
/// which is why their overlays are cleared while maximized: LibVLCSharp's WPF overlay is a
/// separate transparent window that stops tracking a zero-size host and would otherwise sit
/// over the big picture, swallowing the double-click meant to bring the grid back.
/// </para>
/// <para>
/// Tiles come from the ISAPI channel list, never from the SDK's channel count: Site C's login
/// reports 32 IP channels, 21 of which exist, and the other eleven fail with "illegal
/// channel". A channel ISAPI marks offline is shown but not started, saving a stream slot.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private bool _gridMode;
    private int _gridPage;

    /// <summary>Bumped on every grid start/stop; async continuations bail out when it moves.</summary>
    private int _gridGen;

    private HikvisionSdkSession? _gridSession;
    private readonly List<LiveTile> _tiles = [];
    private Task? _gridStopTask;
    private Task? _gridStartTask;

    // The maximized view: one player kept for the window's life, streams per use.
    private MediaPlayer? _maxPlayer;
    private HikvisionLiveStream? _maxSdk;
    private Media? _maxMedia;
    private LiveTile? _maxTile;
    private int _maxGen;

    /// <summary>One camera in the grid: its view, its player, and whatever feeds it.</summary>
    private sealed class LiveTile
    {
        public required Channel Channel { get; init; }
        public required Border Frame { get; init; }
        public required VideoView View { get; init; }
        public required MediaPlayer Player { get; init; }
        public required Grid Overlay { get; init; }
        public required TextBlock Status { get; init; }
        public HikvisionLiveStream? Sdk { get; set; }
        public Media? Media { get; set; }

        public void SetStatus(string text)
        {
            Status.Text = text;
            Status.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// The decoder settings every live media gets, single view or tile, SDK or RTSP.
    /// </summary>
    /// <remarks>
    /// <c>avcodec-threads=1</c> is the fix for "first frame, then nothing". Hikvision stamps
    /// each program-stream pack's clock equal to the frame's own timestamp, and its 0xBD
    /// metadata stream makes VLC's PS demuxer discard the pack clock anyway, so frames reach
    /// the decoder with no lead beyond the 300 ms input cache. avcodec's frame-threading then
    /// holds output back by about one frame per thread — ten threads at 20 fps is 500 ms —
    /// and the video output drops every frame as late. Measured on Site C: 118 late drops in
    /// 15 s by default, zero with one thread, full 20 fps for 30 s; bigger caches and
    /// <c>clock-synchro=0</c> did nothing. Hardware decoding (D3D11VA → NVDEC) is unaffected,
    /// and even software HEVC at 4256×1888 kept up on one thread. Audio is dropped for tiles
    /// because sixteen recorders' worth of it is noise, not information.
    /// </remarks>
    private static void AddLiveDecodeOptions(Media media, bool audio = true)
    {
        media.AddOption(":avcodec-threads=1");
        if (!audio)
            media.AddOption(":no-audio");
    }

    /// <summary>
    /// Removes a view's overlay. Assigning <c>null</c> would do nothing: <see cref="VideoView"/>
    /// moves whatever it is given into its overlay window and resets its own
    /// <c>Content</c> to null as it does so, so null-to-null is not a change. An empty,
    /// background-less element replaces the overlay with one that is fully transparent — and
    /// in a layered window, fully transparent means click-through.
    /// </summary>
    private static void ClearOverlay(VideoView view) => view.Content = new Grid();

    /// <summary>The last grid summary shown on the status line, restored after a maximize.</summary>
    private string _gridStatus = "";

    private IReadOnlyList<Channel> CurrentChannels =>
        ChannelList.ItemsSource is IEnumerable<ChannelItem> items
            ? items.Select(i => i.Channel).ToList()
            : [];

    // ----- mode and paging -----

    private void OnLiveGridToggle(object sender, RoutedEventArgs e)
    {
        bool on = LiveGridToggle.IsChecked == true;
        if (on == _gridMode)
            return;
        _gridMode = on;

        // The grid decides main/sub for itself: sub in the tiles, main when maximized.
        LiveStreamCombo.IsEnabled = !on;
        LiveGridHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (on)
        {
            QueuePlayerStop(_livePlayer);
            StopSdkLive();
            LiveVideo.Visibility = Visibility.Collapsed;
            LiveGridPanel.Visibility = Visibility.Visible;
            _gridPage = 0;
            _ = StartLiveGridAsync();
        }
        else
        {
            StopLiveGrid();
            LiveGridPanel.Visibility = Visibility.Collapsed;
            LiveVideo.Visibility = Visibility.Visible;
            UpdateGridPageControls();
        }
    }

    private void OnLiveGridPrev(object sender, RoutedEventArgs e) => TurnGridPage(-1);

    private void OnLiveGridNext(object sender, RoutedEventArgs e) => TurnGridPage(+1);

    private void TurnGridPage(int delta)
    {
        int count = CurrentChannels.Count;
        int page = LiveGridLayout.ClampPage(_gridPage + delta, count);
        if (page == _gridPage)
            return;
        _gridPage = page;
        _ = StartLiveGridAsync();
    }

    private void UpdateGridPageControls()
    {
        int count = _gridMode ? CurrentChannels.Count : 0;
        int pages = LiveGridLayout.PageCount(count);
        LiveGridPrev.IsEnabled = _gridMode && _gridPage > 0;
        LiveGridNext.IsEnabled = _gridMode && _gridPage < pages - 1;
        LiveGridPageLabel.Text = _gridMode ? LiveGridLayout.Describe(_gridPage, count) : "";
    }

    /// <summary>Called when a device's channel list has loaded: a grid follows the selection.</summary>
    private void OnChannelsLoadedForLiveGrid()
    {
        if (!_gridMode)
            return;
        _gridPage = 0;
        _ = StartLiveGridAsync();
    }

    private void OnLivePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _maxTile is not null)
        {
            RestoreGrid();
            e.Handled = true;
        }
    }

    // ----- starting and stopping -----

    /// <summary>
    /// Builds the tiles for the current page and starts a sub-stream preview in each.
    /// </summary>
    private async Task StartLiveGridAsync()
    {
        StopLiveGrid();
        if (!_gridMode || _libVlc is null || _cleanupStarted)
            return;

        var device = _currentDevice;
        var client = _client;
        var channels = CurrentChannels;
        _gridPage = LiveGridLayout.ClampPage(_gridPage, channels.Count);
        UpdateGridPageControls();

        if (device is null || client is null)
        {
            SetStatus("Select a device to fill the grid.");
            return;
        }
        if (channels.Count == 0)
        {
            SetStatus($"{device.Name}: no channels to show.");
            return;
        }

        var transport = SelectedLiveTransport;
        if (transport == LiveTransport.Sdk && device.VendorKind != Vendor.Hikvision)
        {
            SetStatus("The SDK transport is Hikvision-only. Dahua's private protocol " +
                "(DHNetSDK on 37777) is not implemented — use RTSP.");
            return;
        }

        int gen = ++_gridGen;
        int selection = _selectionGen;
        var page = LiveGridLayout.Page(channels, _gridPage);
        BuildTiles(page);

        var startTask = RunGridStartAsync(gen, selection, device, client, transport, page);
        _gridStartTask = startTask;
        try
        {
            await startTask;
        }
        finally
        {
            if (ReferenceEquals(_gridStartTask, startTask))
                _gridStartTask = null;
        }
    }

    private async Task RunGridStartAsync(int gen, int selection, SavedDevice device,
        INvrClient client, LiveTransport transport, IReadOnlyList<Channel> page)
    {
        bool Stale() => gen != _gridGen || selection != _selectionGen || _libVlc is null;

        HikvisionSdkSession? session = null;
        if (transport == LiveTransport.Sdk)
        {
            SetStatus($"Connecting to {device.Name} on SDK port {device.SdkPort} …");
            try
            {
                session = await Task.Run(() => HikvisionSdkSession.Open(device.ToConnection(),
                    device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name));
            }
            catch (DeviceIdentityException ex)
            {
                if (Stale())
                    return;
                SetStatus($"{device.Name}: WRONG DEVICE on the SDK port — not connected.");
                MessageBox.Show(this,
                    ex.Message + "\n\nThe SDK port is a separate forward from the web port, so " +
                    "it can point at a different recorder than the rest of this record does. " +
                    "Nothing was streamed from it.",
                    "DVRTool — wrong device", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            catch (Exception ex)
            {
                if (Stale())
                    return;
                SetStatus($"SDK live view failed: {Shorten(ex.Message)}");
                foreach (var t in _tiles)
                    t.SetStatus("not connected");
                return;
            }

            if (Stale())
            {
                await Task.Run(session.Dispose);
                return;
            }
            _gridSession = session;
        }

        int started = 0, skipped = 0, failed = 0;
        foreach (var tile in _tiles.ToArray())
        {
            if (Stale())
                return;

            if (tile.Channel.Online == false)
            {
                // ISAPI already says so; asking the SDK would burn a slot to hear "error 11".
                tile.SetStatus("offline");
                skipped++;
                continue;
            }

            try
            {
                Media media;
                if (session is not null)
                {
                    var live = await Task.Run(() => session.StartLive(tile.Channel.Id, StreamType.Sub));
                    if (Stale())
                    {
                        live.Dispose();
                        return;
                    }
                    tile.Sdk = live;
                    media = new Media(_libVlc!, new StreamMediaInput(live.Media));
                    _ = WatchTileAsync(gen, tile, live);
                }
                else
                {
                    media = CreateRtspMedia(client.GetLiveUri(tile.Channel.Id, StreamType.Sub));
                }
                AddLiveDecodeOptions(media, audio: false);
                tile.Media = media;
                tile.Player.Play(media);
                tile.SetStatus("");
                started++;
            }
            catch (Exception ex)
            {
                if (Stale())
                    return;
                tile.SetStatus(Shorten(ex.Message));
                failed++;
            }

            // Sixteen keyframes in one instant is a 25–34 Mbps burst on a 7 Mbps stream.
            await Task.Delay(LiveGridLayout.StartStagger);
        }

        if (Stale())
            return;
        string route = session is not null
            ? $"over the SDK port {device.SdkPort}"
            : $"over RTSP {device.RtspPort}";
        _gridStatus = $"Grid: {started} camera(s) live {route}" +
            (skipped > 0 ? $", {skipped} offline" : "") +
            (failed > 0 ? $", {failed} failed" : "") +
            " — sub streams; double-click one for the main stream.";
        SetStatus(_gridStatus);
    }

    /// <summary>A preview the recorder accepted and then never fed gets named as such.</summary>
    private async Task WatchTileAsync(int gen, LiveTile tile, HikvisionLiveStream live)
    {
        bool arrived = await live.Media.WaitForDataAsync(TimeSpan.FromSeconds(15));
        if (gen != _gridGen || arrived)
            return;
        tile.SetStatus("no video — camera offline?");
    }

    private void BuildTiles(IReadOnlyList<Channel> page)
    {
        LiveGridPanel.Children.Clear();
        LiveGridPanel.Columns = LiveGridLayout.Columns(page.Count);
        LiveGridPanel.Rows = LiveGridLayout.Rows(page.Count);

        foreach (var channel in page)
        {
            var player = new MediaPlayer(_libVlc!);
            var view = new VideoView { MediaPlayer = player };
            var status = new TextBlock
            {
                Foreground = Brushes.Gainsboro,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8),
            };
            var overlay = new Grid
            {
                // Almost-transparent rather than transparent: the overlay lives in a
                // layered window, and fully transparent pixels are not hit-tested at all.
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            };
            overlay.Children.Add(new TextBlock
            {
                Text = $"{channel.Id}  {channel.Name}",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)),
                Padding = new Thickness(5, 1, 5, 2),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            });
            overlay.Children.Add(status);

            var frame = new Border
            {
                Background = Brushes.Black,
                BorderBrush = Brushes.DimGray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(1),
                Child = view,
            };
            var tile = new LiveTile
            {
                Channel = channel, Frame = frame, View = view, Player = player,
                Overlay = overlay, Status = status,
            };
            overlay.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    e.Handled = true;
                    _ = MaximizeTileAsync(tile);
                }
            };
            // LibVLC reports a stream it could not open on its own thread and otherwise
            // leaves the pane black. Over RTSP that is the common case — the port most
            // sites do not forward — so the tile says so instead of looking like a
            // camera that is merely slow.
            player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (_tiles.Contains(tile) && tile.Sdk is null)
                    tile.SetStatus("stream failed — is the RTSP port reachable? " +
                        "The SDK transport needs only the SDK port.");
            });
            player.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (_tiles.Contains(tile))
                    tile.SetStatus(tile.Sdk is { Media.Stalled: true }
                        ? "no video — the recorder went quiet"
                        : "stream ended");
            });
            view.Content = overlay;
            tile.SetStatus("connecting …");
            _tiles.Add(tile);
            LiveGridPanel.Children.Add(frame);
        }
    }

    /// <summary>
    /// Tears the grid down: players stopped off the UI thread, previews and the session
    /// released so the recorder gets its stream slots back.
    /// </summary>
    private void StopLiveGrid()
    {
        _gridGen++;
        RestoreGrid();

        var tiles = _tiles.ToArray();
        _tiles.Clear();
        var session = _gridSession;
        _gridSession = null;
        LiveGridPanel.Children.Clear();

        if (tiles.Length == 0 && session is null)
            return;

        // Detach on the UI thread so no VideoView renders against a disposed player, and
        // release the hwnd hosts now that they are out of the tree.
        foreach (var t in tiles)
        {
            t.View.MediaPlayer = null;
            ClearOverlay(t.View);
            t.View.Dispose();
        }

        _gridStopTask = Task.Run(() =>
        {
            lock (_playerLock)
            {
                if (!_playersDisposed)
                {
                    foreach (var t in tiles)
                    {
                        try { t.Player.Stop(); } catch (ObjectDisposedException) { }
                        try { t.Player.Dispose(); } catch (ObjectDisposedException) { }
                    }
                }
            }
            foreach (var t in tiles)
                t.Media?.Dispose();
            // Streams first, then logout — the session does that ordering itself.
            session?.Dispose();
        });
    }

    /// <summary>Shutdown-time teardown, waited on.</summary>
    private async Task DisposeLiveGridAsync()
    {
        _gridGen++;
        if (_gridStartTask is { } starting)
        {
            try { await starting; }
            catch { /* reported by RunGridStartAsync */ }
        }
        StopLiveGrid();
        if (_gridStopTask is { } stopping)
            await stopping;

        LiveMaxVideo.MediaPlayer = null;
        var max = _maxPlayer;
        _maxPlayer = null;
        if (max is not null)
            await Task.Run(() =>
            {
                lock (_playerLock)
                {
                    if (_playersDisposed)
                        return;
                    try { max.Stop(); } catch (ObjectDisposedException) { }
                    try { max.Dispose(); } catch (ObjectDisposedException) { }
                }
            });
    }

    // ----- maximize / restore -----

    /// <summary>
    /// Brings one tile's camera up full-size on its main stream, leaving the grid running
    /// underneath so the way back is instant.
    /// </summary>
    private async Task MaximizeTileAsync(LiveTile tile)
    {
        if (_maxTile is not null || _libVlc is null || _cleanupStarted)
            return;
        var device = _currentDevice;
        var client = _client;
        if (device is null || client is null)
            return;

        int gen = _gridGen;
        int maxGen = ++_maxGen;
        _maxTile = tile;
        _maxPlayer ??= new MediaPlayer(_libVlc);
        LiveMaxVideo.MediaPlayer = _maxPlayer;

        // Collapsed tiles keep their overlay windows where they were; emptied, those are
        // transparent and click-through, so the big view's own overlay gets the double-click.
        foreach (var t in _tiles)
            ClearOverlay(t.View);
        LiveGridPanel.Visibility = Visibility.Collapsed;

        var status = new TextBlock
        {
            Text = "starting main stream …",
            Foreground = Brushes.Gainsboro,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        overlay.Children.Add(new TextBlock
        {
            Text = $"{tile.Channel.Id}  {tile.Channel.Name}  —  main stream  ·  double-click or Esc to return to the grid",
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)),
            Padding = new Thickness(6, 2, 6, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        });
        overlay.Children.Add(status);
        overlay.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                RestoreGrid();
            }
        };
        LiveMaxVideo.Content = overlay;
        LiveMaxVideo.Visibility = Visibility.Visible;

        bool Stale() => gen != _gridGen || maxGen != _maxGen || _libVlc is null;
        try
        {
            Media media;
            if (_gridSession is { } session)
            {
                var live = await Task.Run(() => session.StartLive(tile.Channel.Id, StreamType.Main));
                if (Stale())
                {
                    live.Dispose();
                    return;
                }
                _maxSdk = live;
                media = new Media(_libVlc!, new StreamMediaInput(live.Media));
            }
            else
            {
                media = CreateRtspMedia(client.GetLiveUri(tile.Channel.Id, StreamType.Main));
            }
            AddLiveDecodeOptions(media);
            _maxMedia = media;
            _maxPlayer.Play(media);
            status.Visibility = Visibility.Collapsed;
            SetStatus($"{tile.Channel.Name}: main stream — double-click or Esc to return to the grid.");
        }
        catch (Exception ex)
        {
            if (Stale())
                return;
            status.Text = Shorten(ex.Message);
            SetStatus($"Main stream failed: {Shorten(ex.Message)}");
        }
    }

    /// <summary>Back to the grid: the main-stream preview released, tiles uncovered.</summary>
    private void RestoreGrid()
    {
        _maxGen++;
        var wasMax = _maxTile;
        _maxTile = null;

        var sdk = _maxSdk;
        var media = _maxMedia;
        _maxSdk = null;
        _maxMedia = null;
        if (wasMax is not null || sdk is not null)
        {
            QueuePlayerStop(_maxPlayer);
            if (sdk is not null || media is not null)
                _ = Task.Run(() =>
                {
                    sdk?.Dispose();
                    media?.Dispose();
                });
        }

        ClearOverlay(LiveMaxVideo);
        LiveMaxVideo.Visibility = Visibility.Collapsed;
        foreach (var t in _tiles)
            t.View.Content = t.Overlay;
        if (_gridMode)
        {
            LiveGridPanel.Visibility = Visibility.Visible;
            if (wasMax is not null && _gridStatus.Length > 0)
                SetStatus(_gridStatus);
        }
    }
}
