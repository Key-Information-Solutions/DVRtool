using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DVRTool.Core;
using DVRTool.Vendors.HikvisionSdk;
using LibVLCSharp.Shared;

namespace DVRTool.App;

/// <summary>
/// The Fisheye tab: one channel, decoded to frames, dewarped on the GPU (or the CPU where the
/// GPU cannot present), aimed with the mouse.
/// </summary>
/// <remarks>
/// <para>
/// The chain is the Live tab's up to the decoder and the dewarp engine's after it:
/// <see cref="VlcFrameSource"/> plays the same SDK or RTSP media the Live tab would, but through
/// LibVLC's video callbacks, so each decoded frame arrives here as a <see cref="DewarpFrame"/>
/// and goes into <see cref="DewarpSurface.Present"/>. Nothing about the transport changed, which
/// is the point: the SDK route that works at fourteen sites is the one feeding the dewarp.
/// </para>
/// <para>
/// <b>Calibration is per frame size until the operator says otherwise.</b> Nothing on the wire
/// says where the image circle is, so the first frame's dimensions seed
/// <see cref="FisheyeCalibration.Default"/> — right for Site C's square stream, close for Casa
/// Maya's 4:3 one — and the toolbar's numbers let the operator refine it. Once edited, a
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
    private HikvisionSdkSession? _dewarpSdkSession;
    private Task? _dewarpStartTask;
    private int _dewarpGen;

    private FisheyeCalibration _dewarpCalibration = FisheyeCalibration.Default(2560, 2560);
    private bool _dewarpCalibrationEdited;
    private DewarpView _dewarpView = DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear);
    private int _dewarpFrameWidth;
    private int _dewarpFrameHeight;
    private string _dewarpSourceLabel = "";

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

    private void InitializeDewarpTab()
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

        _dewarpStatsTimer.Tick += (_, _) => UpdateDewarpStatus();
        _dewarpStatsTimer.Start();
        UpdateDewarpStatus();
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

    // ----- starting and stopping -----

    private async void OnDewarpPlay(object sender, RoutedEventArgs e)
    {
        if (_client is null || _currentDevice is null || _libVlc is null)
        {
            SetStatus("Select a device and channel first.");
            return;
        }
        if (ChannelList.SelectedItem is not ChannelItem item)
        {
            SetStatus("Select a channel first.");
            return;
        }
        if (_dewarpStartTask is { IsCompleted: false })
        {
            SetStatus("Still starting the previous fisheye stream …");
            return;
        }

        var device = _currentDevice;
        var stream = DewarpStreamCombo.SelectedIndex == 1 ? StreamType.Sub : StreamType.Main;
        bool overSdk = DewarpTransportCombo.SelectedIndex == 1;
        int channel = item.Channel.Id;

        // Whatever was playing goes first — a stream slot on the recorder, and a decoder here.
        StopDewarp();
        int gen = _dewarpGen;
        int selection = _selectionGen;
        var source = EnsureDewarpSource();
        if (source is null)
            return;
        DewarpPane.ClearFrame();

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
            SetStatus($"Fisheye: connecting to {device.Name} on SDK port {device.SdkPort} …");
            var startTask = Task.Run(() =>
            {
                var session = HikvisionSdkSession.Open(device.ToConnection(),
                    device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name);
                try
                {
                    return (Session: session, Live: session.StartLive(channel, stream));
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            });
            _dewarpStartTask = startTask;
            try
            {
                var (session, live) = await startTask;
                if (gen != _dewarpGen || selection != _selectionGen || _libVlc is null || _cleanupStarted)
                {
                    await Task.Run(session.Dispose);
                    return;
                }
                _dewarpSdkSession = session;
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

    private void OnDewarpStop(object sender, RoutedEventArgs e)
    {
        StopDewarp();
        SetStatus("Fisheye stopped — the recorder's stream slot is released. The last picture " +
            "stays and can still be aimed.");
    }

    /// <summary>
    /// Ends the stream: the decoder off the UI thread (LibVLC's Stop blocks until its threads
    /// join) and the SDK session with it, releasing the recorder's stream slot.
    /// </summary>
    private void StopDewarp()
    {
        _dewarpGen++;
        var session = _dewarpSdkSession;
        _dewarpSdkSession = null;
        var source = _dewarpSource;
        if (source is not null)
            _ = source.StopAsync();
        if (session is not null)
            _ = Task.Run(session.Dispose);
    }

    /// <summary>Shutdown-time teardown, waited on.</summary>
    private async Task DisposeDewarpAsync()
    {
        _dewarpGen++;
        _dewarpStatsTimer.Stop();
        if (_dewarpStartTask is { } starting)
        {
            try { await starting; }
            catch { /* already reported by OnDewarpPlay */ }
        }
        var source = _dewarpSource;
        _dewarpSource = null;
        var session = _dewarpSdkSession;
        _dewarpSdkSession = null;
        if (source is not null)
            await Task.Run(source.Dispose);
        if (session is not null)
            await Task.Run(session.Dispose);
        DewarpPane.Dispose();
    }

    // ----- frames -----

    private void OnDewarpFrame(DewarpFrame frame)
    {
        if (_cleanupStarted)
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
            if (_dewarpStatsShown == 0 && _dewarpSourceLabel.Length > 0)
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
