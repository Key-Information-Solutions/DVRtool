using System.Windows;
using DVRTool.Core;
using DVRTool.Vendors.HikvisionSdk;
using LibVLCSharp.Shared;

namespace DVRTool.App;

/// <summary>
/// The Live tab: the same picture over either of two transports.
/// </summary>
/// <remarks>
/// <para>
/// RTSP is the standard route and the one this tab started with. It is also the route most
/// sites do not forward — across our installed base the RTSP port is reachable at a handful
/// of recorders and the SDK port at nearly all of them, because the SDK port is what iVMS-4200
/// needs and so is the one that got opened. Hikvision's private protocol carries the video
/// back over the very session it logged in on, which is why one open port is enough.
/// </para>
/// <para>
/// So this is not a fallback bolted onto the RTSP path — on most of the fleet it is the only
/// transport that works remotely, and the choice is the operator's rather than something
/// guessed at. The SDK route is Hikvision-only and needs <c>HCNetSDK.dll</c> present; both
/// facts are reported when they bite, not assumed away.
/// </para>
/// <para>
/// Either way the picture goes through the same LibVLC player. The SDK's data callback hands
/// over an MPEG program stream, which LibVLC demuxes from a <see cref="StreamMediaInput"/>
/// exactly as it demuxes an RTSP session — so overlays, snapshots and the existing stop/
/// dispose plumbing all keep working.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Live transports, in the order the combo lists them.</summary>
    private enum LiveTransport
    {
        Rtsp = 0,
        Sdk = 1,
    }

    private HikvisionSdkSession? _sdkSession;

    /// <summary>The single view's SDK preview, kept for the footer's dropped-bytes count.</summary>
    private HikvisionLiveStream? _sdkLive;

    /// <summary>Guards the SDK teardown against a Play that is still starting up.</summary>
    private int _sdkGen;

    private Task? _sdkStartTask;

    /// <summary>
    /// Names the transports after the ports this device actually uses. The numbers are
    /// per-device and operators do move them, so a bare "RTSP / SDK" would leave the one
    /// question the row exists to answer — which port has to be open — unanswered.
    /// </summary>
    private void UpdateLiveTransportLabels(SavedDevice? device)
    {
        if (device is null)
        {
            LiveTransportRtsp.Content = "RTSP";
            LiveTransportSdk.Content = "SDK";
            return;
        }
        LiveTransportRtsp.Content = $"RTSP {device.RtspPort}";
        LiveTransportSdk.Content = device.VendorKind == Vendor.Hikvision
            ? $"SDK {device.SdkPort}"
            : "SDK (Hikvision only)";
    }

    private LiveTransport SelectedLiveTransport =>
        LiveTransportCombo.SelectedIndex == (int)LiveTransport.Sdk
            ? LiveTransport.Sdk
            : LiveTransport.Rtsp;

    private async void OnLivePlay(object sender, RoutedEventArgs e)
    {
        if (_client is null || _currentDevice is null || _libVlc is null || _livePlayer is null)
        {
            SetStatus("Select a device and channel first.");
            return;
        }
        if (ChannelList.SelectedItem is not ChannelItem item)
        {
            SetStatus("Select a channel first.");
            return;
        }

        // In grid mode Play means "fill the grid again" — see MainWindow.LiveGrid.cs.
        if (_gridMode)
        {
            await StartLiveGridAsync();
            return;
        }

        // With the dewarp on, Play means the same thing on the other decoder: re-open this
        // channel through the frame source, on whatever the toolbar now says.
        if (_dewarpMode)
        {
            await StartDewarpAsync();
            return;
        }

        var stream = LiveStreamCombo.SelectedIndex == 1 ? StreamType.Sub : StreamType.Main;
        if (SelectedLiveTransport == LiveTransport.Sdk)
        {
            await PlayOverSdkAsync(_currentDevice, item.Channel.Id, stream);
            return;
        }

        Uri uri;
        try
        {
            uri = _client.GetLiveUri(item.Channel.Id, stream);
        }
        catch (NotSupportedException ex)
        {
            // Nx through the DW Cloud relay: HTTPS only, no RTSP — say so instead of handing
            // LibVLC a URL that can only fail to connect.
            SetStatus(ex.Message);
            return;
        }
        using var media = CreateRtspMedia(uri);
        AddLiveDecodeOptions(media);
        StopSdkLive();
        _livePlayer.Play(media);
        _liveLabel = LiveLabel(item.Channel, stream);
        UpdateLiveStats();
        SetStatus($"Live: channel {item.Channel.Id} ({stream}) over RTSP " +
            $"{_currentDevice.RtspPort}.");
    }

    private void OnLiveStop(object sender, RoutedEventArgs e)
    {
        if (_dewarpMode)
        {
            // The dewarp keeps its last picture, which stays aimable — so this is the stream
            // stopping, not the mode ending.
            StopDewarp();
            SetStatus("Fisheye stopped — the recorder's stream slot is released. The last " +
                "picture stays and can still be aimed.");
            return;
        }
        QueuePlayerStop(_livePlayer);
        // The recorder holds a stream slot for as long as the preview runs, so Stop has to
        // release it rather than only blanking the window.
        StopSdkLive();
        StopLiveGrid();
        UpdateGridPageControls();
        if (_gridMode)
            SetStatus("Grid stopped — the recorder's stream slots are released. ▶ Play fills it again.");
    }

    /// <summary>
    /// Logs in over the SDK port, starts a preview, and points the existing player at it.
    /// </summary>
    private async Task PlayOverSdkAsync(SavedDevice device, int channel, StreamType stream)
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
        if (_sdkStartTask is { IsCompleted: false })
        {
            SetStatus("Still starting the previous SDK stream …");
            return;
        }

        // Whatever was playing goes first: the recorder has a finite number of stream slots,
        // and leaving the old preview running to open another is how a site runs out of them.
        QueuePlayerStop(_livePlayer);
        StopSdkLive();

        int gen = ++_sdkGen;
        int selection = _selectionGen;
        SetStatus($"Connecting to {device.Name} on SDK port {device.SdkPort} …");

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
        _sdkStartTask = startTask;

        try
        {
            var (session, live) = await startTask;

            // The operator moved on while the login was in flight. The session is closed
            // rather than kept: it belongs to a device that is no longer selected, and it is
            // holding one of that recorder's stream slots.
            if (gen != _sdkGen || selection != _selectionGen || _libVlc is null ||
                _livePlayer is null)
            {
                await Task.Run(session.Dispose);
                return;
            }

            _sdkSession = session;
            _sdkLive = live;

            // Non-seekable and of unknown length, which is what tells LibVLC this is live.
            using var media = new Media(_libVlc, new StreamMediaInput(live.Media));
            AddLiveDecodeOptions(media);
            _livePlayer.Play(media);
            _liveLabel = LiveLabel(ChannelList.Items.OfType<ChannelItem>()
                .FirstOrDefault(c => c.Channel.Id == channel)?.Channel, stream, channel);
            UpdateLiveStats();

            SetStatus($"Live: channel {channel} (device channel {live.SdkChannel}, {stream}) " +
                $"over the SDK port {device.SdkPort} — no RTSP involved.");

            // A preview the device accepted and then never fed looks exactly like one that is
            // still connecting. Saying so beats leaving the operator watching a black pane.
            _ = ReportSdkFirstFrameAsync(gen, live, channel);
        }
        catch (DeviceIdentityException ex)
        {
            if (gen != _sdkGen)
                return;
            SetStatus($"{device.Name}: WRONG DEVICE on the SDK port — not connected.");
            MessageBox.Show(this,
                ex.Message + "\n\nThe SDK port is a separate forward from the web port, so it " +
                "can point at a different recorder than the rest of this record does. Nothing " +
                "was streamed from it.",
                "DVRTool — wrong device", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            if (gen != _sdkGen)
                return;
            SetStatus($"SDK live view failed: {Shorten(ex.Message)}");
        }
        finally
        {
            if (ReferenceEquals(startTask, _sdkStartTask))
                _sdkStartTask = null;
        }
    }

    private async Task ReportSdkFirstFrameAsync(int gen, HikvisionLiveStream live, int channel)
    {
        bool arrived = await live.Media.WaitForDataAsync(TimeSpan.FromSeconds(15));
        if (gen != _sdkGen || arrived)
            return;
        SetStatus($"Channel {channel}: the recorder accepted the request but sent no video. " +
            "The camera is most likely offline.");
    }

    /// <summary>
    /// Ends the SDK preview and logs out, releasing the recorder's stream slot.
    /// </summary>
    /// <remarks>
    /// Both the disposal and the stream slot are the reason this is not left to the garbage
    /// collector: <c>NET_DVR_StopRealPlay</c> blocks until the SDK's receive thread joins, so
    /// it runs off the UI thread, and a preview nobody released keeps consuming a slot on the
    /// recorder until the session times out.
    /// </remarks>
    private void StopSdkLive()
    {
        _sdkGen++;
        var session = _sdkSession;
        _sdkSession = null;
        _sdkLive = null;
        if (session is null)
            return;
        _ = Task.Run(session.Dispose);
    }

    /// <summary>The footer's name for what the single view is playing: "3  Front Door  ·  main".</summary>
    private static string LiveLabel(Channel? channel, StreamType stream, int channelId = 0)
    {
        string name = channel is not null ? $"{channel.Id}  {channel.Name}" : $"{channelId}";
        return $"{name}  ·  {(stream == StreamType.Sub ? "sub" : "main")}";
    }

    /// <summary>Shutdown-time teardown: the same, but waited on.</summary>
    private async Task DisposeSdkLiveAsync()
    {
        _sdkGen++;
        _sdkLive = null;
        if (_sdkStartTask is { } starting)
        {
            // A login still in flight would otherwise complete into a closing window and
            // leak the session it just opened.
            try { await starting; }
            catch { /* already reported by PlayOverSdkAsync */ }
        }
        var session = _sdkSession;
        _sdkSession = null;
        if (session is not null)
            await Task.Run(session.Dispose);
    }
}
