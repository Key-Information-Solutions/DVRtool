using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DVRTool.Core;
using DVRTool.Vendors.HikvisionSdk;
using LibVLCSharp.Shared;

namespace DVRTool.App;

/// <summary>
/// Fisheye dewarping, as a mode of the Live tab: the camera already on screen, decoded to
/// frames, dewarped on the GPU (or the CPU where the GPU cannot present), aimed with the mouse.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a way of looking at a live camera, not a separate destination.</b> A tech watching
/// a fisheye has already picked the device, the channel, the stream and the transport in the Live
/// tab's toolbar; making them pick all four again on another tab to see the same camera undistorted
/// is the whole of the friction. So the ◎ Fisheye button toggles the dewarp over whichever
/// <i>single</i> camera is on screen — the single view, or a maximized grid camera — and inherits
/// everything else from the Live tab.
/// </para>
/// <para>
/// <b>It is one camera or none, and that is a property of the renderer's input, not a policy.</b>
/// A dewarp resamples the full-resolution picture, so it wants the main stream; a grid page is
/// sixteen sub streams. Hence the toggle is disabled in grid mode until a camera is maximized,
/// which is exactly when the grid has a main stream of its own.
/// </para>
/// <para>
/// <b>Turning it on restarts the picture, because the two paths are different decoders.</b> The
/// plain view is LibVLC rendering into a <c>VideoView</c>'s window; the dewarp needs the decoded
/// planes in memory, which is LibVLC's <c>vmem</c> video callbacks through
/// <see cref="VlcFrameSource"/>. One media cannot feed both, so the toggle stops the one and
/// starts the other on the same channel — releasing the recorder's stream slot before taking
/// another, never holding two.
/// </para>
/// <para>
/// <b>Calibration is per frame size until the operator says otherwise.</b> Nothing on the wire
/// says where the image circle is, so the first frame's dimensions seed
/// <see cref="FisheyeCalibration.Default"/> — right for Site C's square stream, close for Site
/// H's 4:3 one — and the toolbar's numbers let the operator refine it. Once edited, a
/// calibration is kept across stream restarts and main/sub switches (the renderer rescales it to
/// the frame). It is not yet saved with the device; that is the calibration store still to come.
/// </para>
/// <para>
/// <b>Drag and wheel go through <see cref="DewarpDrag"/></b>, which solves for the view rather
/// than scaling pixels to degrees, and the pane redraws from the frame it already has — so the
/// picture follows the mouse at the display's rate, not the camera's.
/// </para>
/// <para>
/// <b>The test pattern needs no camera.</b> It presents a synthetic floor seen through the current
/// calibration, and is how the whole on-screen path — swap chain, child window, shader, colour —
/// was first verified on this machine, with no recorder involved.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private VlcFrameSource? _dewarpSource;

    /// <summary>The dewarp's own SDK login, when it opened one. Null when it borrowed the grid's.</summary>
    private HikvisionSdkSession? _dewarpSdkSession;

    /// <summary>A preview started on the grid's session: this side stops it, the grid logs out.</summary>
    private HikvisionLiveStream? _dewarpBorrowedLive;

    private Task? _dewarpStartTask;

    /// <summary>
    /// The previous stream's stop, still running. <c>MediaPlayer.Stop</c> blocks until LibVLC's
    /// threads join, so it goes to a worker — and a <c>Play</c> issued before that worker has
    /// finished is stopped by it a moment later, which is a live stream that silently never
    /// arrives. Every start waits on this first.
    /// </summary>
    private Task? _dewarpStopTask;

    private int _dewarpGen;

    /// <summary>True between the toggle going down and coming back up — the mode, not the stream.</summary>
    private bool _dewarpMode;

    /// <summary>Guards the toggle's own handler against the code that sets IsChecked.</summary>
    private bool _dewarpToggling;

    /// <summary>The maximized tile the dewarp took over from, so turning it off can put it back.</summary>
    private LiveTile? _dewarpFromTile;

    private FisheyeCalibration _dewarpCalibration = FisheyeCalibration.Default(2560, 2560);
    private bool _dewarpCalibrationEdited;
    private DewarpView _dewarpView = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear);
    private int _dewarpFrameWidth;
    private int _dewarpFrameHeight;
    private string _dewarpSourceLabel = "";

    /// <summary>
    /// This stream has not put a frame on screen yet. Its own flag rather than a zero in the
    /// frame counters: the frame source outlives any one stream, so its counts are cumulative
    /// and never come back to zero for the second camera of a session.
    /// </summary>
    private bool _dewarpAwaitingFirstFrame;

    private bool _dewarpDragging;
    private double _dewarpDragX;
    private double _dewarpDragY;
    private DewarpView _dewarpDragStart;

    private readonly DispatcherTimer _dewarpStatsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _dewarpInitializing;
    private long _dewarpStatsReceived;
    private long _dewarpStatsShown;
    private long _dewarpStatsSkipped;

    private static readonly DewarpViewMode[] DewarpModes =
    [
        DewarpViewMode.Rectilinear,
        DewarpViewMode.Panorama180,
        DewarpViewMode.Panorama360,
    ];

    private void InitializeDewarpMode()
    {
        // Filling the combos raises their SelectionChanged, which would otherwise count as the
        // operator having edited the calibration before they have touched anything.
        _dewarpInitializing = true;
        try
        {
            DewarpModeCombo.ItemsSource = DewarpModes.Select(DescribeMode).ToList();
            DewarpModeCombo.SelectedIndex = 0;
            DewarpMountCombo.ItemsSource = Enum.GetNames<FisheyeMount>();
            DewarpMountCombo.SelectedIndex = 0;
            DewarpLensCombo.ItemsSource = Enum.GetValues<LensProjection>().Select(DescribeLens).ToList();
            DewarpLensCombo.SelectedIndex = (int)LensProjection.Equidistant;
        }
        finally
        {
            _dewarpInitializing = false;
        }
        FillCalibrationBoxes();

        DewarpPane.PointerPressed += OnDewarpPointerPressed;
        DewarpPane.PointerMoved += OnDewarpPointerMoved;
        DewarpPane.PointerReleased += OnDewarpPointerReleased;
        DewarpPane.Wheel += OnDewarpWheel;
        DewarpPane.BackendChanged += (_, _) => UpdateDewarpStatus();
        DewarpPane.SizeChanged += (_, _) =>
            // After layout has settled, so the swap chain is resized to the size WPF actually gave.
            Dispatcher.BeginInvoke(DispatcherPriority.Render, DewarpPane.Redraw);

        _dewarpStatsTimer.Tick += (_, _) =>
        {
            if (_dewarpMode)
                UpdateDewarpStatus();
        };
        _dewarpStatsTimer.Start();
        UpdateDewarpAvailability();
    }

    private static string DescribeMode(DewarpViewMode mode) => mode switch
    {
        DewarpViewMode.Rectilinear => "PTZ (flat)",
        DewarpViewMode.Panorama180 => "Panorama 180°",
        DewarpViewMode.Panorama360 => "Panorama 360°",
        DewarpViewMode.Quad => "Quad",
        _ => mode.ToString(),
    };

    private static string DescribeLens(LensProjection projection) => projection switch
    {
        LensProjection.Equidistant => "Equidistant (f·θ)",
        LensProjection.Stereographic => "Stereographic",
        LensProjection.EquisolidAngle => "Equisolid angle",
        LensProjection.Orthographic => "Orthographic",
        _ => projection.ToString(),
    };

    private DewarpViewMode SelectedDewarpMode =>
        DewarpModes[Math.Clamp(DewarpModeCombo.SelectedIndex, 0, DewarpModes.Length - 1)];

    // ----- the toggle -----

    /// <summary>
    /// Where the dewarp can stand in for the plain view: over the single camera, or over a
    /// maximized grid camera. A grid page is sixteen sub streams and nothing to dewarp.
    /// </summary>
    private bool DewarpAvailable => !_gridMode || _maxTile is not null;

    private void UpdateDewarpAvailability()
    {
        LiveDewarpToggle.IsEnabled = DewarpAvailable || _dewarpMode;
    }

    private void OnLiveDewarpToggle(object sender, RoutedEventArgs e)
    {
        if (_dewarpToggling)
            return;
        bool on = LiveDewarpToggle.IsChecked == true;
        if (on == _dewarpMode)
            return;
        if (on)
            EnterDewarpMode();
        else
            LeaveDewarpMode(replay: true);
    }

    /// <summary>Puts the toggle back up without running the handler's teardown twice.</summary>
    private void SetDewarpToggle(bool on)
    {
        _dewarpToggling = true;
        try { LiveDewarpToggle.IsChecked = on; }
        finally { _dewarpToggling = false; }
    }

    /// <summary>
    /// Turns the dewarp mode off from somewhere other than the button — leaving the grid, picking
    /// another device, closing down. <paramref name="replay"/> says whether the plain view should
    /// be started again, which it should not be when the thing it would show is going away.
    /// </summary>
    private void ExitDewarpMode(bool replay = false)
    {
        if (!_dewarpMode)
            return;
        SetDewarpToggle(false);
        LeaveDewarpMode(replay);
    }

    private void EnterDewarpMode()
    {
        _dewarpMode = true;
        _dewarpFromTile = _maxTile;
        ShowDewarpChrome(true);

        // The plain view of this camera goes first: the recorder has a finite number of stream
        // slots, and the dewarp is about to ask for one for the same picture.
        if (_dewarpFromTile is not null)
        {
            // The maximized camera's main stream, and the grid underneath it, both give way —
            // the tiles keep running, they are just not what is on screen.
            ReleaseMainStream();
            LiveGridPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            QueuePlayerStop(_livePlayer);
            StopSdkLive();
            LiveVideo.Visibility = Visibility.Collapsed;
            _liveLabel = "";
        }
        DewarpPane.Visibility = Visibility.Visible;
        UpdateDewarpAvailability();
        _ = StartDewarpAsync();
    }

    private void LeaveDewarpMode(bool replay)
    {
        var tile = TeardownDewarpMode();

        if (tile is not null && _tiles.Contains(tile) && _gridMode)
        {
            LiveGridPanel.Visibility = Visibility.Visible;
            if (replay)
            {
                // Back through the ordinary maximize: the tile's sub stream is still running, so
                // it fills the panel again while the main stream warms up, exactly as it did the
                // first time. Its overlay went when the main stream took over the screen, and
                // MaximizeTileAsync needs it back — that is where the label and the double-click
                // that returns to the grid live. MaximizeTileAsync refuses to run twice, so let
                // go of the claim as well.
                tile.View.Content = tile.Overlay;
                _maxTile = null;
                _ = MaximizeTileAsync(tile);
            }
            else
            {
                RestoreGrid();
            }
            UpdateDewarpAvailability();
            return;
        }

        if (!_gridMode)
        {
            LiveVideo.Visibility = Visibility.Visible;
            if (replay && _client is not null && _currentDevice is not null &&
                ChannelList.SelectedItem is ChannelItem)
                OnLivePlay(this, new RoutedEventArgs());
        }
        UpdateDewarpAvailability();
    }

    /// <summary>
    /// Everything the mode owns, released: the stream, the pane, the toolbars, the toggle. What
    /// goes back on screen afterwards is the caller's business, because the two callers differ —
    /// the button puts the plain view back, <see cref="RestoreGrid"/> is already doing that.
    /// Returns the maximized tile the dewarp had taken over, if it was over one.
    /// </summary>
    private LiveTile? TeardownDewarpMode()
    {
        if (!_dewarpMode)
            return null;
        _dewarpMode = false;
        SetDewarpToggle(false);
        var tile = _dewarpFromTile;
        _dewarpFromTile = null;
        StopDewarp();
        DewarpPane.Visibility = Visibility.Collapsed;
        DewarpPane.ClearFrame();
        ShowDewarpChrome(false);
        _dewarpSourceLabel = "";
        _dewarpFrameWidth = _dewarpFrameHeight = 0;
        return tile;
    }

    /// <summary>The dewarp's own toolbars and status line, shown only while the mode is on.</summary>
    private void ShowDewarpChrome(bool on)
    {
        var visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DewarpToolBars.Visibility = visibility;
        DewarpStatusText.Visibility = visibility;
        if (on)
            UpdateDewarpStatus();
    }

    // ----- starting and stopping -----

    /// <summary>
    /// Opens the camera the Live tab is pointed at through the frame source. Which camera, which
    /// stream and which transport are all the Live tab's answers, never asked again here.
    /// </summary>
    private async Task StartDewarpAsync()
    {
        if (_currentDevice is null || _libVlc is null || _cleanupStarted)
        {
            SetStatus("Select a device and channel first.");
            return;
        }
        if (_dewarpStartTask is { IsCompleted: false })
        {
            SetStatus("Still starting the previous fisheye stream …");
            return;
        }

        var device = _currentDevice;
        int channel;
        StreamType stream;
        bool overSdk;
        HikvisionSdkSession? borrowed = null;

        if (_dewarpFromTile is { } tile)
        {
            // The maximized camera, on the main stream the maximize would have used — and on the
            // grid's own login, so dewarping a grid camera costs a stream slot, not a session.
            channel = tile.Channel.Id;
            stream = StreamType.Main;
            borrowed = _gridSession;
            overSdk = borrowed is not null;
        }
        else
        {
            if (ChannelList.SelectedItem is not ChannelItem item)
            {
                SetStatus("Select a channel first — or press Test pattern to check the dewarp " +
                    "with no camera.");
                return;
            }
            channel = item.Channel.Id;
            stream = LiveStreamCombo.SelectedIndex == 1 ? StreamType.Sub : StreamType.Main;
            overSdk = SelectedLiveTransport == LiveTransport.Sdk;
        }

        // Whatever the dewarp itself was playing goes first — a stream slot on the recorder, and
        // a decoder here.
        StopDewarp();
        _dewarpAwaitingFirstFrame = true;
        int gen = _dewarpGen;
        int selection = _selectionGen;
        var source = EnsureDewarpSource();
        if (source is null)
            return;
        DewarpPane.ClearFrame();

        // The old stream's Stop is on a worker and would otherwise stop the new one.
        if (_dewarpStopTask is { } stopping)
        {
            await stopping;
            if (gen != _dewarpGen || selection != _selectionGen || _cleanupStarted)
                return;
        }

        if (overSdk)
        {
            if (device.VendorKind != Vendor.Hikvision)
            {
                SetStatus(device.VendorKind == Vendor.Dahua
                    ? "The SDK transport is Hikvision-only. Dahua's private protocol " +
                      "(DHNetSDK on 37777) is not implemented — use RTSP."
                    : $"The SDK transport is Hikvision-only. {VendorNames.Display(device.VendorKind)} " +
                      "has no SDK port; its live video is RTSP on the server port — use RTSP.");
                return;
            }
            if (borrowed is null)
                SetStatus($"Fisheye: connecting to {device.Name} on SDK port {device.SdkPort} …");
            var startTask = Task.Run(() =>
            {
                // A session the grid already holds is reused; otherwise this is the dewarp's own
                // login, and it owns the disposal.
                var session = borrowed ?? HikvisionSdkSession.Open(device.ToConnection(),
                    device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name);
                try
                {
                    return (Session: session, Live: session.StartLive(channel, stream), Owned: borrowed is null);
                }
                catch
                {
                    if (borrowed is null)
                        session.Dispose();
                    throw;
                }
            });
            _dewarpStartTask = startTask;
            try
            {
                var (session, live, owned) = await startTask;
                if (gen != _dewarpGen || selection != _selectionGen || _libVlc is null || _cleanupStarted)
                {
                    live.Dispose();
                    if (owned)
                        await Task.Run(session.Dispose);
                    return;
                }
                if (owned)
                    _dewarpSdkSession = session;
                else
                    _dewarpBorrowedLive = live;
                using var media = new Media(_libVlc, new StreamMediaInput(live.Media));
                AddDewarpDecodeOptions(media);
                source.Play(media);
                _dewarpSourceLabel = $"channel {channel} (device channel {live.SdkChannel}, {stream}) over SDK {device.SdkPort}";
                SetStatus($"Fisheye: {_dewarpSourceLabel} — waiting for the first frame …");
                _ = ReportDewarpFirstFrameAsync(gen, live, channel);
            }
            catch (DeviceIdentityException ex)
            {
                if (gen != _dewarpGen)
                    return;
                SetStatus($"{device.Name}: WRONG DEVICE on the SDK port — not connected.");
                MessageBox.Show(this, ex.Message, "DVRTool — wrong device",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                if (gen != _dewarpGen)
                    return;
                SetStatus($"Fisheye over SDK failed: {Shorten(ex.Message)}");
            }
            finally
            {
                if (ReferenceEquals(startTask, _dewarpStartTask))
                    _dewarpStartTask = null;
            }
            return;
        }

        if (_client is null)
        {
            SetStatus("Select a device first.");
            return;
        }
        Uri uri;
        try
        {
            uri = _client.GetLiveUri(channel, stream);
        }
        catch (NotSupportedException ex)
        {
            SetStatus(ex.Message);
            return;
        }
        using (var media = CreateRtspMedia(uri))
        {
            AddDewarpDecodeOptions(media);
            source.Play(media);
        }
        _dewarpSourceLabel = $"channel {channel} ({stream}) over RTSP {device.RtspPort}";
        SetStatus($"Fisheye: {_dewarpSourceLabel} — waiting for the first frame …");
    }

    /// <summary>
    /// The Live tab's decode options plus no audio: a dewarp has no speaker, and an audio track
    /// would only open an output device for nothing.
    /// </summary>
    private static void AddDewarpDecodeOptions(Media media) => AddLiveDecodeOptions(media, audio: false);

    private VlcFrameSource? EnsureDewarpSource()
    {
        if (_dewarpSource is not null)
            return _dewarpSource;
        if (_libVlc is null)
            return null;
        _dewarpSource = new VlcFrameSource(_libVlc, Dispatcher, OnDewarpFrame);
        return _dewarpSource;
    }

    private async Task ReportDewarpFirstFrameAsync(int gen, HikvisionLiveStream live, int channel)
    {
        bool arrived = await live.Media.WaitForDataAsync(TimeSpan.FromSeconds(15));
        if (gen != _dewarpGen || arrived)
            return;
        SetStatus($"Channel {channel}: the recorder accepted the request but sent no video. " +
            "The camera is most likely offline.");
    }

    /// <summary>
    /// Ends the stream: the decoder off the UI thread (LibVLC's Stop blocks until its threads
    /// join) and the SDK preview with it, releasing the recorder's stream slot. A session this
    /// side opened is closed; one borrowed from the grid is left to the grid.
    /// </summary>
    private void StopDewarp()
    {
        _dewarpGen++;
        _dewarpAwaitingFirstFrame = false;
        var session = _dewarpSdkSession;
        _dewarpSdkSession = null;
        var borrowed = _dewarpBorrowedLive;
        _dewarpBorrowedLive = null;
        var source = _dewarpSource;
        if (source is not null)
            _dewarpStopTask = source.StopAsync();
        if (session is not null)
            _ = Task.Run(session.Dispose);
        else if (borrowed is not null)
            _ = Task.Run(borrowed.Dispose);
    }

    /// <summary>Shutdown-time teardown, waited on.</summary>
    private async Task DisposeDewarpAsync()
    {
        _dewarpGen++;
        _dewarpMode = false;
        _dewarpStatsTimer.Stop();
        if (_dewarpStartTask is { } starting)
        {
            try { await starting; }
            catch { /* already reported by StartDewarpAsync */ }
        }
        if (_dewarpStopTask is { } stopping)
        {
            try { await stopping; }
            catch { /* Stop swallows its own disposal race */ }
            _dewarpStopTask = null;
        }
        var source = _dewarpSource;
        _dewarpSource = null;
        var session = _dewarpSdkSession;
        _dewarpSdkSession = null;
        var borrowed = _dewarpBorrowedLive;
        _dewarpBorrowedLive = null;
        if (source is not null)
            await Task.Run(source.Dispose);
        if (session is not null)
            await Task.Run(session.Dispose);
        else if (borrowed is not null)
            await Task.Run(borrowed.Dispose);
        DewarpPane.Dispose();
    }

    // ----- frames -----

    private void OnDewarpFrame(DewarpFrame frame)
    {
        if (_cleanupStarted || !_dewarpMode)
            return;
        if (frame.Width != _dewarpFrameWidth || frame.Height != _dewarpFrameHeight)
        {
            _dewarpFrameWidth = frame.Width;
            _dewarpFrameHeight = frame.Height;
            if (!_dewarpCalibrationEdited)
            {
                // The first frame says how big the picture is, and until the operator says
                // otherwise the circle is assumed inscribed and centred. Mount and lens are the
                // operator's choices already, so they carry over.
                _dewarpCalibration = FisheyeCalibration.Default(frame.Width, frame.Height) with
                {
                    Mount = _dewarpCalibration.Mount,
                    Projection = _dewarpCalibration.Projection,
                };
                FillCalibrationBoxes();
            }
        }
        if (_dewarpAwaitingFirstFrame)
        {
            _dewarpAwaitingFirstFrame = false;
            if (_dewarpSourceLabel.Length > 0)
                SetStatus($"Fisheye: {_dewarpSourceLabel}, {frame.Width}×{frame.Height}.");
        }
        PresentDewarp(frame);
    }

    private void PresentDewarp(in DewarpFrame frame)
    {
        DewarpPane.Calibration = _dewarpCalibration;
        DewarpPane.View = _dewarpView;
        DewarpPane.Present(frame);
    }

    private void RedrawDewarp()
    {
        DewarpPane.Calibration = _dewarpCalibration;
        DewarpPane.View = _dewarpView;
        DewarpPane.Redraw();
    }

    private void OnDewarpTestPattern(object sender, RoutedEventArgs e)
    {
        StopDewarp();
        var frame = FisheyeTestPattern.Frame(_dewarpCalibration, maxSide: 1600);
        _dewarpFrameWidth = frame.Width;
        _dewarpFrameHeight = frame.Height;
        _dewarpSourceLabel = "";
        PresentDewarp(frame);
        SetStatus($"Test pattern: a tiled floor {frame.Width}×{frame.Height} through the current " +
            "calibration. Straight grout lines in the flat view mean the dewarp is right; one half " +
            "of the floor is tinted red and the other blue. Drag to aim, wheel to zoom.");
        UpdateDewarpStatus();
    }

    // ----- view and calibration controls -----

    private void OnDewarpViewSettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _dewarpInitializing)
            return;
        _dewarpView = DewarpView.DefaultFor(_dewarpCalibration.Mount, SelectedDewarpMode);
        RedrawDewarp();
    }

    private void OnDewarpCalibrationSettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _dewarpInitializing)
            return;
        var mount = (FisheyeMount)Math.Max(0, DewarpMountCombo.SelectedIndex);
        var projection = (LensProjection)Math.Max(0, DewarpLensCombo.SelectedIndex);
        var candidate = _dewarpCalibration with { Mount = mount, Projection = projection };
        if (candidate.Validate() is { } problem)
        {
            SetStatus($"Calibration: {problem}");
            return;
        }
        _dewarpCalibration = candidate;
        _dewarpCalibrationEdited = true;
        _dewarpView = DewarpView.DefaultFor(mount, SelectedDewarpMode);
        RedrawDewarp();
    }

    private void OnDewarpResetView(object sender, RoutedEventArgs e)
    {
        _dewarpView = DewarpView.DefaultFor(_dewarpCalibration.Mount, SelectedDewarpMode);
        RedrawDewarp();
    }

    private void OnDewarpRendererChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _dewarpInitializing)
            return;
        DewarpPane.Preference = DewarpRendererCombo.SelectedIndex switch
        {
            1 => DewarpBackendPreference.Gpu,
            2 => DewarpBackendPreference.Cpu,
            _ => DewarpBackendPreference.Auto,
        };
        RedrawDewarp();
        UpdateDewarpStatus();
    }

    private void OnDewarpApplyCalibration(object sender, RoutedEventArgs e)
    {
        if (!TryParseBox(DewarpCenterXBox, out double cx) || !TryParseBox(DewarpCenterYBox, out double cy) ||
            !TryParseBox(DewarpRadiusBox, out double radius) || !TryParseBox(DewarpFovBox, out double fov))
        {
            SetStatus("Calibration: centre, radius and field of view must be numbers.");
            return;
        }
        var candidate = _dewarpCalibration with
        {
            CenterX = cx,
            CenterY = cy,
            RadiusX = radius,
            FieldOfViewDegrees = fov,
            SourceWidth = _dewarpFrameWidth > 0 ? _dewarpFrameWidth : _dewarpCalibration.SourceWidth,
            SourceHeight = _dewarpFrameHeight > 0 ? _dewarpFrameHeight : _dewarpCalibration.SourceHeight,
        };
        if (candidate.Validate() is { } problem)
        {
            SetStatus($"Calibration: {problem}");
            return;
        }
        _dewarpCalibration = candidate;
        _dewarpCalibrationEdited = true;
        _dewarpView = _dewarpView.ClampedTo(_dewarpCalibration);
        RedrawDewarp();
        SetStatus($"Calibration applied: circle centre ({cx:0.#}, {cy:0.#}), radius {radius:0.#}, " +
            $"{fov:0.#}° {DescribeLens(_dewarpCalibration.Projection)}, {_dewarpCalibration.Mount} mount.");
    }

    private void OnDewarpDefaultCalibration(object sender, RoutedEventArgs e)
    {
        int w = _dewarpFrameWidth > 0 ? _dewarpFrameWidth : 2560;
        int h = _dewarpFrameHeight > 0 ? _dewarpFrameHeight : 2560;
        _dewarpCalibration = FisheyeCalibration.Default(w, h) with
        {
            Mount = _dewarpCalibration.Mount,
            Projection = _dewarpCalibration.Projection,
        };
        _dewarpCalibrationEdited = false;
        FillCalibrationBoxes();
        _dewarpView = _dewarpView.ClampedTo(_dewarpCalibration);
        RedrawDewarp();
        SetStatus($"Calibration reset to the default for {w}×{h}: circle inscribed and centred, 180°.");
    }

    private void FillCalibrationBoxes()
    {
        var cal = _dewarpCalibration;
        DewarpCenterXBox.Text = cal.CenterX.ToString("0.#", CultureInfo.InvariantCulture);
        DewarpCenterYBox.Text = cal.CenterY.ToString("0.#", CultureInfo.InvariantCulture);
        DewarpRadiusBox.Text = cal.RadiusX.ToString("0.#", CultureInfo.InvariantCulture);
        DewarpFovBox.Text = cal.FieldOfViewDegrees.ToString("0.#", CultureInfo.InvariantCulture);
        DewarpCalibrationNote.Text = _dewarpCalibrationEdited
            ? $"in {cal.SourceWidth}×{cal.SourceHeight} pixels"
            : $"default for {cal.SourceWidth}×{cal.SourceHeight}";
    }

    private static bool TryParseBox(TextBox box, out double value) =>
        double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value);

    // ----- mouse -----

    private void OnDewarpPointerPressed(object? sender, DewarpPointerEventArgs e)
    {
        if (!DewarpPane.HasFrame)
            return;
        _dewarpDragging = true;
        _dewarpDragX = e.X;
        _dewarpDragY = e.Y;
        _dewarpDragStart = _dewarpView;
    }

    private void OnDewarpPointerMoved(object? sender, DewarpPointerEventArgs e)
    {
        if (!_dewarpDragging)
            return;
        var (w, h) = DewarpPane.PanePixelSize;
        _dewarpView = DewarpDrag.Drag(_dewarpCalibration, _dewarpDragStart, w, h,
            _dewarpDragX, _dewarpDragY, e.X, e.Y);
        RedrawDewarp();
    }

    private void OnDewarpPointerReleased(object? sender, DewarpPointerEventArgs e)
    {
        _dewarpDragging = false;
    }

    private void OnDewarpWheel(object? sender, DewarpPointerEventArgs e)
    {
        if (!DewarpPane.HasFrame)
            return;
        _dewarpView = DewarpDrag.Wheel(_dewarpCalibration, _dewarpView, e.WheelDelta);
        RedrawDewarp();
    }

    // ----- status line -----

    private void UpdateDewarpStatus()
    {
        var source = _dewarpSource;
        string picture = _dewarpFrameWidth > 0 ? $"{_dewarpFrameWidth}×{_dewarpFrameHeight}" : "no picture yet";
        string rate = "";
        if (source is not null)
        {
            long received = source.FramesReceived;
            long shown = source.FramesShown;
            long skipped = source.FramesSkipped;
            long dReceived = received - _dewarpStatsReceived;
            long dShown = shown - _dewarpStatsShown;
            long dSkipped = skipped - _dewarpStatsSkipped;
            _dewarpStatsReceived = received;
            _dewarpStatsShown = shown;
            _dewarpStatsSkipped = skipped;
            if (received > 0)
                rate = $" · {dReceived} fps decoded, {dShown} shown" + (dSkipped > 0 ? $", {dSkipped} skipped" : "");
        }
        string view = _dewarpView.Mode == DewarpViewMode.Rectilinear
            ? $"aim {_dewarpView.Orientation.YawDegrees:0}° / {_dewarpView.Orientation.PitchDegrees:0}°, {_dewarpView.HorizontalFovDegrees:0}° wide"
            : $"{DescribeMode(_dewarpView.Mode)} from {_dewarpView.Orientation.YawDegrees:0}°";
        string backend = DewarpPane.Backend switch
        {
            DewarpBackend.Gpu => $"GPU ({DewarpPane.AdapterDescription})",
            DewarpBackend.Cpu => "CPU",
            _ => "renderer not chosen yet",
        };
        DewarpStatusText.Text = $"{picture}{rate} · {view} · {backend}";
        DewarpStatusText.ToolTip = DewarpPane.BackendReason;
    }
}
