using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using DVRTool.Core;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace DVRTool.App;

public partial class MainWindow : Window
{
    private LibVLC? _libVlc;
    private MediaPlayer? _livePlayer;
    private MediaPlayer? _playbackPlayer;

    private readonly ObservableCollection<SavedDevice> _devices = [];
    private INvrClient? _client;
    private SavedDevice? _currentDevice;

    // Bumped on every device selection; async continuations compare their captured
    // value against it and bail out if the selection moved on (stale-completion guard).
    private int _selectionGen;
    private CancellationTokenSource? _clientCts;

    private CancellationTokenSource? _downloadCts;
    private Task? _downloadTask;
    private Task? _searchTask;

    // Serializes MediaPlayer Stop against Dispose (a queued Stop racing shutdown
    // disposal would call into a released native handle).
    private readonly object _playerLock = new();
    private bool _playersDisposed;

    private bool _cleanupStarted;
    private bool _closePending;

    private sealed record ChannelItem(Channel Channel)
    {
        public string Display => Channel.Online switch
        {
            true => $"{Channel.Id}  {Channel.Name}",
            false => $"{Channel.Id}  {Channel.Name}  (offline)",
            null => $"{Channel.Id}  {Channel.Name}",
        };
    }

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC();
        _livePlayer = new MediaPlayer(_libVlc);
        _playbackPlayer = new MediaPlayer(_libVlc);
        LiveVideo.MediaPlayer = _livePlayer;
        PlaybackVideo.MediaPlayer = _playbackPlayer;

        foreach (var device in DeviceStore.Load())
            _devices.Add(device);
        DeviceList.ItemsSource = _devices;

        var now = DateTime.Now;
        StartBox.Text = now.Date.ToString("yyyy-MM-dd HH:mm:ss");
        EndBox.Text = now.ToString("yyyy-MM-dd HH:mm:ss");

        if (_devices.Count > 0)
            DeviceList.SelectedIndex = 0;
    }

    private async void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closePending)
            return; // cleanup finished — let the window close
        e.Cancel = true;
        if (_cleanupStarted)
            return; // cleanup already running; ignore repeated close clicks
        _cleanupStarted = true;

        _downloadCts?.Cancel();
        _clientCts?.Cancel();
        if (_downloadTask is { } task)
        {
            try { await task; }
            catch { /* already reported by RunDownloadAsync */ }
        }
        // A canceled Dahua search still needs its client alive to close/destroy
        // the finder object on the NVR — wait for that cleanup too.
        if (_searchTask is { } search)
        {
            try { await search; }
            catch { /* canceled/failed; OnSearch already reported it */ }
        }

        // Detach the views first so VideoView never renders against a disposed
        // player, then run the blocking Stop/Dispose chain off the UI thread —
        // libvlc 3.x Stop blocks (and can hang while a connect is pending).
        LiveVideo.MediaPlayer = null;
        PlaybackVideo.MediaPlayer = null;
        var live = _livePlayer;
        var playback = _playbackPlayer;
        var vlc = _libVlc;
        _livePlayer = _playbackPlayer = null;
        _libVlc = null;
        await Task.Run(() =>
        {
            lock (_playerLock)
            {
                live?.Stop();
                playback?.Stop();
                live?.Dispose();
                playback?.Dispose();
                vlc?.Dispose();
                _playersDisposed = true;
            }
        });

        _client?.Dispose();
        _downloadCts?.Dispose();
        _clientCts?.Dispose();
        _closePending = true;
        Close();
    }

    // ----- device management -----

    private void OnAddDevice(object sender, RoutedEventArgs e)
    {
        var dialog = new AddDeviceWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            _devices.Add(dialog.Result);
            DeviceStore.Save(_devices);
            DeviceList.SelectedItem = dialog.Result;
        }
    }

    private void OnRemoveDevice(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not SavedDevice device)
            return;
        if (MessageBox.Show(this, $"Remove '{device.Name}'?", "DVRTool",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _devices.Remove(device);
        DeviceStore.Save(_devices);
    }

    private async void OnDeviceSelected(object sender, RoutedEventArgs e)
    {
        int gen = ++_selectionGen;
        ChannelList.ItemsSource = null;
        ResultsGrid.ItemsSource = null;

        _clientCts?.Cancel();
        _clientCts?.Dispose();
        _clientCts = null;

        // Never dispose a client that still has a download or search running
        // through it (a canceled Dahua search still uses the client to release
        // its finder object) — defer disposal until those tasks finish.
        var old = _client;
        _client = null;
        _currentDevice = null;
        if (old is not null)
        {
            var pending = new[] { _downloadTask, _searchTask }
                .Where(t => t is { IsCompleted: false })
                .Cast<Task>()
                .ToArray();
            if (pending.Length > 0)
                _ = Task.WhenAll(pending).ContinueWith(t =>
                {
                    _ = t.Exception; // observe, then dispose
                    old.Dispose();
                }, TaskScheduler.Default);
            else
                old.Dispose();
        }

        if (DeviceList.SelectedItem is not SavedDevice device)
            return;

        try
        {
            _client = device.CreateClient();
            _currentDevice = device;
            _clientCts = new CancellationTokenSource();
            SetStatus($"Connecting to {device.Name} …");
            var channels = await _client.GetChannelsAsync(_clientCts.Token);
            if (gen != _selectionGen)
                return;
            ChannelList.ItemsSource = channels.Select(c => new ChannelItem(c)).ToList();
            if (channels.Count > 0)
                ChannelList.SelectedIndex = 0;
            SetStatus($"{device.Name}: {channels.Count} channel(s).");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection or shutdown.
        }
        catch (Exception ex)
        {
            if (gen != _selectionGen)
                return;
            SetStatus($"Failed to connect to {device.Name}: {Shorten(ex.Message)}");
        }
    }

    // ----- live -----

    private void OnLivePlay(object sender, RoutedEventArgs e)
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

        var stream = LiveStreamCombo.SelectedIndex == 1 ? StreamType.Sub : StreamType.Main;
        var uri = _client.GetLiveUri(item.Channel.Id, stream);
        using var media = CreateRtspMedia(uri);
        _livePlayer.Play(media);
        SetStatus($"Live: channel {item.Channel.Id} ({stream}).");
    }

    private void OnLiveStop(object sender, RoutedEventArgs e) => QueuePlayerStop(_livePlayer);

    // ----- playback / export -----

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        if (_client is null || _clientCts is null || ChannelList.SelectedItem is not ChannelItem item)
        {
            SetStatus("Select a device and channel first.");
            return;
        }
        if (!TryGetWindow(out var start, out var end))
            return;

        int gen = _selectionGen;
        var client = _client;
        var ct = _clientCts.Token;
        var searchTask = client.SearchAsync(item.Channel.Id, start, end, ct);
        _searchTask = searchTask; // tracked so device-switch/close defer client disposal
        try
        {
            SetStatus($"Searching channel {item.Channel.Id} …");
            var segments = await searchTask;
            if (gen != _selectionGen)
                return;
            ResultsGrid.ItemsSource = segments;
            var total = TimeSpan.FromSeconds(segments.Sum(s => s.Duration.TotalSeconds));
            SetStatus($"{segments.Count} segment(s), {(long)total.TotalHours}:{total.Minutes:D2}:{total.Seconds:D2} of footage.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (gen != _selectionGen)
                return;
            SetStatus($"Search failed: {Shorten(ex.Message)}");
        }
        finally
        {
            if (ReferenceEquals(searchTask, _searchTask))
                _searchTask = null;
        }
    }

    private void OnPlaySegment(object sender, RoutedEventArgs e)
    {
        if (_client is null || _currentDevice is null || _libVlc is null || _playbackPlayer is null ||
            ResultsGrid.SelectedItem is not RecordingSegment segment)
        {
            SetStatus("Select a search result first.");
            return;
        }

        var uri = _client.GetPlaybackUri(segment.Channel, segment.Start, segment.End);
        using var media = CreateRtspMedia(uri);
        _playbackPlayer.Play(media);
        SetStatus($"Playing {segment.Start:HH:mm:ss} → {segment.End:HH:mm:ss} (ch {segment.Channel}).");
    }

    private void OnPlaybackStop(object sender, RoutedEventArgs e) => QueuePlayerStop(_playbackPlayer);

    private void QueuePlayerStop(MediaPlayer? player)
    {
        // libvlc 3.x Stop blocks until its threads join — keep it off the UI
        // thread, and serialize against shutdown disposal via _playerLock.
        if (player is null || _cleanupStarted)
            return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_playerLock)
            {
                if (_playersDisposed)
                    return;
                try { player.Stop(); }
                catch (ObjectDisposedException) { }
            }
        });
    }

    private async void OnDownloadSegment(object sender, RoutedEventArgs e)
    {
        if (_client is null || ResultsGrid.SelectedItem is not RecordingSegment segment)
        {
            SetStatus("Select a search result first.");
            return;
        }
        var client = _client;
        string extension = client.Vendor == Vendor.Dahua ? ".dav" : ".mp4";
        string suggested = $"ch{segment.Channel}_{segment.Start:yyyyMMdd_HHmmss}{extension}";
        await RunDownloadAsync(suggested, segment.SizeBytes, (path, progress, ct) =>
            client.DownloadSegmentAsync(segment, path, progress, ct));
    }

    private async void OnExportRange(object sender, RoutedEventArgs e)
    {
        if (_client is null || ChannelList.SelectedItem is not ChannelItem item)
        {
            SetStatus("Select a device and channel first.");
            return;
        }
        if (!TryGetWindow(out var start, out var end))
            return;

        var client = _client;
        string extension = client.Vendor == Vendor.Dahua ? ".dav" : ".mp4";
        string suggested = $"ch{item.Channel.Id}_{start:yyyyMMdd_HHmmss}-{end:HHmmss}{extension}";
        await RunDownloadAsync(suggested, null, (path, progress, ct) =>
            client.DownloadAsync(item.Channel.Id, start, end, path, progress, ct));
    }

    private async Task RunDownloadAsync(string suggestedName, long? expectedBytes,
        Func<string, IProgress<long>, CancellationToken, Task> download)
    {
        int gen = _selectionGen;
        var dialog = new SaveFileDialog
        {
            FileName = suggestedName,
            Filter = "Video files|*.mp4;*.dav;*.mkv|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        // ShowDialog pumps the dispatcher — the device may have changed meanwhile.
        if (gen != _selectionGen)
        {
            SetStatus("Device changed while choosing a file — download not started.");
            return;
        }

        // Serialize with any previous download so two tasks never drive the shared
        // progress UI (or the same destination file) at once.
        var oldCts = _downloadCts;
        oldCts?.Cancel();
        if (_downloadTask is { } oldTask)
        {
            try { await oldTask; }
            catch { /* its own catch already reported it */ }
        }
        oldCts?.Dispose();
        if (gen != _selectionGen)
        {
            SetStatus("Device changed — download not started.");
            return;
        }

        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        DownloadProgress.IsIndeterminate = expectedBytes is null;
        DownloadProgress.Value = 0;

        var progress = new Progress<long>(bytes =>
        {
            if (!ReferenceEquals(cts, _downloadCts))
                return; // stale reports from a superseded download
            DownloadLabel.Text = $"{bytes / 1048576.0:F1} MB";
            if (expectedBytes is > 0)
                DownloadProgress.Value = Math.Min(100.0, 100.0 * bytes / expectedBytes.Value);
        });

        var task = download(dialog.FileName, progress, cts.Token);
        _downloadTask = task;
        try
        {
            SetStatus($"Downloading → {dialog.FileName} …");
            await task;
            long size = new FileInfo(dialog.FileName).Length;
            SetStatus($"Saved {dialog.FileName} ({size / 1048576.0:F1} MB).");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(cts, _downloadCts))
                SetStatus("Download canceled — no file was saved.");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(cts, _downloadCts))
                SetStatus($"Download failed: {Shorten(ex.Message)}");
        }
        finally
        {
            if (ReferenceEquals(cts, _downloadCts))
            {
                DownloadProgress.IsIndeterminate = false;
                DownloadLabel.Text = "";
            }
            if (ReferenceEquals(task, _downloadTask))
                _downloadTask = null;
        }
    }

    // ----- helpers -----

    private Media CreateRtspMedia(Uri uri)
    {
        // Credentials go in as live555 options, not in the MRL, so the password
        // never appears in Media.Mrl, libVLC logs, or diagnostic dumps.
        var media = new Media(_libVlc!, uri);
        media.AddOption(":rtsp-tcp");
        media.AddOption($":rtsp-user={_currentDevice!.Username}");
        media.AddOption($":rtsp-pwd={_currentDevice.Password}");
        return media;
    }

    private bool TryGetWindow(out DateTime start, out DateTime end)
    {
        start = end = default;
        string[] formats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"];
        if (!DateTime.TryParseExact(StartBox.Text.Trim(), formats, null,
                System.Globalization.DateTimeStyles.None, out start) ||
            !DateTime.TryParseExact(EndBox.Text.Trim(), formats, null,
                System.Globalization.DateTimeStyles.None, out end) ||
            end <= start)
        {
            SetStatus("Enter a valid time window (yyyy-MM-dd HH:mm:ss), end after start.");
            return false;
        }
        return true;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private static string Shorten(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
