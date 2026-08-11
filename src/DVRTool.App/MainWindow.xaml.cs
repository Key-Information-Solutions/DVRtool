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

    // Same stale-completion guard for the Users tab, which reads devices other than
    // the selected one and so cannot ride on _selectionGen. Its own CTS/task, because
    // the ephemeral clients it opens are not the selected device's.
    private int _usersGen;
    private CancellationTokenSource? _usersCts;
    private Task? _usersTask;

    private CancellationTokenSource? _downloadCts;
    private Task? _downloadTask;
    private Task? _searchTask;

    // Serializes MediaPlayer Stop against Dispose (a queued Stop racing shutdown
    // disposal would call into a released native handle).
    private readonly object _playerLock = new();
    private bool _playersDisposed;

    private bool _cleanupStarted;
    private bool _closePending;

    private sealed record UserRow(string User, string LevelA, string LevelB, string Status);

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
        UsersDeviceA.ItemsSource = _devices;
        UsersDeviceB.ItemsSource = _devices;

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
        _usersCts?.Cancel();
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
        // A user read holds its own ephemeral clients — wait for its finally to
        // release them, and so its continuation never runs against a closed window.
        if (_usersTask is { } users)
        {
            try { await users; }
            catch { /* canceled/failed; OnLoadUsers already reported it */ }
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
        _usersCts?.Dispose();
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
        await RunDownloadAsync($"ch{segment.Channel}_{segment.Start:yyyyMMdd_HHmmss}",
            client.Vendor, segment.SizeBytes, (path, progress, ct) =>
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
        await RunDownloadAsync($"ch{item.Channel.Id}_{start:yyyyMMdd_HHmmss}-{end:HHmmss}",
            client.Vendor, null, (path, progress, ct) =>
                client.DownloadAsync(item.Channel.Id, start, end, path, progress, ct));
    }

    /// <summary>
    /// How an export ended. A failed remux is not an exception: the footage was
    /// downloaded and is still on disk, and where it is matters more to the operator
    /// than what ffmpeg said. <see cref="Detail"/> is raised in a dialog — a status
    /// bar is the wrong place for something that decides whether footage is usable.
    /// </summary>
    private sealed record ExportOutcome(bool Success, string Status, string? Detail = null);

    private async Task RunDownloadAsync(string baseName, Vendor vendor, long? expectedBytes,
        Func<string, IProgress<long>, CancellationToken, Task> download)
    {
        int gen = _selectionGen;
        // Read before the dialog opens, and used for both the suggested name and the
        // plan: the dialog pumps the dispatcher, so a later read could disagree with
        // the extension the operator was just shown.
        bool remux = RemuxCheck.IsChecked == true;
        var dialog = new SaveFileDialog
        {
            FileName = ExportNaming.SuggestedFileName(baseName, vendor, remux),
            Filter = ExportNaming.SaveFilter(vendor, remux),
        };
        if (dialog.ShowDialog(this) != true)
            return;
        // ShowDialog pumps the dispatcher — the device may have changed meanwhile.
        if (gen != _selectionGen)
        {
            SetStatus("Device changed while choosing a file — download not started.");
            return;
        }

        RemuxContainer? container = remux
            ? DownloadPaths.ResolveContainer(null, dialog.FileName)
            : null;
        var plan = DownloadPaths.Plan(dialog.FileName, baseName, container);

        // The dialog does not hand back an existing path without asking first, so a file
        // sitting at the name the operator was shown is one they chose to replace.
        //
        // That permission covers only the name they were shown. Leave the extension off
        // and the container supplies one — "Smith-v-Acme" is what the dialog asked about,
        // "Smith-v-Acme.mp4" is where the export lands — and a "replace?" yes spent on the
        // first must not authorize destroying the second, which may be a delivered export.
        bool force = File.Exists(plan.FinalPath) &&
            string.Equals(plan.FinalPath, dialog.FileName, StringComparison.OrdinalIgnoreCase);
        if (!force && File.Exists(plan.FinalPath))
        {
            SetStatus($"Not started — {plan.FinalPath} already exists.");
            MessageBox.Show(this,
                $"{DownloadPaths.OverwriteRefusalMessage(plan.FinalPath)}\n\n" +
                $"The remux would write it, but you were only asked about\n{dialog.FileName}\n\n" +
                "Export again under a name that is not already taken.",
                "Export refused", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Remuxing into a name whose extension contradicts the container ffmpeg is about
        // to write reproduces the exact defect remuxing exists to fix — an MP4 called
        // ".dav" is refused by everything but VLC. The operator named the file, so this
        // asks rather than overrides, and it asks before minutes of transfer rather than
        // after (and before an existing export is replaced by a mislabeled one).
        if (container is RemuxContainer named &&
            DownloadPaths.ContainerContradictsName(plan.FinalPath, named))
        {
            var written = DownloadPaths.MediaContainerFor(named);
            var answer = MessageBox.Show(this,
                $"Remuxing writes {ContainerSniffer.DisplayName(written)} data, but you named " +
                $"this file {Path.GetExtension(plan.FinalPath)}.\n\nMost players trust the " +
                "extension and will refuse it — the problem remuxing exists to fix.\n\n" +
                "Export anyway under the name you chose?\n\nChoose No to name it " +
                $"{ContainerSniffer.ExtensionFor(written)} or .mkv instead.",
                "That name contradicts the remux", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                SetStatus("Export canceled — that name contradicts what the remux writes.");
                return;
            }
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

        // One task spans the download *and* the remux, so a second export waits for the
        // whole pipeline instead of starting while ffmpeg still holds this one's files.
        var task = ExportAsync(plan, force, download, progress, cts);
        _downloadTask = task;
        try
        {
            var outcome = await task;
            if (ReferenceEquals(cts, _downloadCts))
            {
                SetStatus(outcome.Status);
                if (outcome.Detail is { } detail)
                    MessageBox.Show(this, detail,
                        outcome.Success ? "Export warning" : "Export problem",
                        MessageBoxButton.OK,
                        outcome.Success ? MessageBoxImage.Warning : MessageBoxImage.Error);
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(cts, _downloadCts))
                // A canceled download leaves nothing behind; a cancel during the remux
                // leaves the raw stream, which is minutes of transfer and is the footage.
                SetStatus(plan.NeedsRemux && File.Exists(plan.DownloadPath)
                    ? $"Canceled — the raw download is kept at {plan.DownloadPath}."
                    : "Download canceled — no file was saved.");
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

    /// <summary>
    /// Downloads the export and, when the plan calls for one, remuxes it into a
    /// container players other than VLC will open.
    /// </summary>
    private async Task<ExportOutcome> ExportAsync(DownloadPlan plan, bool force,
        Func<string, IProgress<long>, CancellationToken, Task> download,
        IProgress<long> progress, CancellationTokenSource cts)
    {
        SetStatus($"Downloading → {plan.FinalPath} …");
        await download(plan.DownloadPath, progress, cts.Token);

        // An empty response creates no file at all, so there is nothing truncated to
        // mistake for footage — and nothing to sniff or remux either.
        if (!File.Exists(plan.DownloadPath))
            return new ExportOutcome(false,
                "The NVR sent no data for that range — no file was saved.");

        if (plan.Container is not RemuxContainer target)
        {
            // A raw export keeps the name the operator typed. If the bytes contradict
            // that name they are told, rather than handed a file that will not open on
            // the machine it is going to.
            var sniffed = ContainerSniffer.SniffFile(plan.FinalPath);
            string saved = $"Saved {plan.FinalPath} ({MegabytesOf(plan.FinalPath)}).";
            if (ContainerSniffer.ExtensionContradicts(plan.FinalPath, sniffed))
                return new ExportOutcome(true, saved,
                    $"{plan.FinalPath}\n\nholds {ContainerSniffer.DisplayName(sniffed)} data, " +
                    $"not {Path.GetExtension(plan.FinalPath)}. Most players trust the extension " +
                    "and will refuse it.\n\nTick “Remux to a playable file” and export again to " +
                    "get a real container, or rename this one to " +
                    $"{ContainerSniffer.ExtensionFor(sniffed)}.");
            return new ExportOutcome(true, saved);
        }

        if (ReferenceEquals(cts, _downloadCts))
        {
            // The byte counter is done and ffmpeg reports no progress of its own, so
            // the bar stops implying it knows how far along this is.
            DownloadProgress.IsIndeterminate = true;
            DownloadLabel.Text = "remuxing";
            SetStatus($"Remuxing → {plan.FinalPath} …");
        }

        var result = await Remux.RemuxAsync(
            plan.DownloadPath, plan.FinalPath, target, force, cts.Token);

        if (result.RefusedOverwrite)
            // ffmpeg did its job; only the promotion was refused. Reporting that as an
            // ffmpeg failure would send the operator after the wrong problem.
            return new ExportOutcome(false,
                $"Not saved — {plan.FinalPath} appeared while this export was downloading.",
                $"{DownloadPaths.OverwriteRefusalMessage(plan.FinalPath)}\n\n" +
                "It appeared while this export was downloading, so the remux — which " +
                "succeeded — was discarded instead of replacing it.\n\n" +
                $"The raw download is kept at\n{plan.DownloadPath}\n\nVLC plays it as-is.");

        if (!result.Success)
            return new ExportOutcome(false,
                $"Remux failed — the raw download is kept at {plan.DownloadPath}.",
                $"ffmpeg could not remux this export.\n\n{Shorten(result.Output)}\n\n" +
                $"The raw download is kept at\n{plan.DownloadPath}\n\nVLC plays it as-is. " +
                "If ffmpeg is not installed, put it on PATH and export again.");

        TryDelete(plan.DownloadPath);
        return new ExportOutcome(true, $"Saved {plan.FinalPath} ({MegabytesOf(plan.FinalPath)}).");
    }

    // ----- users -----

    private async void OnLoadUsers(object sender, RoutedEventArgs e)
    {
        if (_cleanupStarted)
            return; // window is closing; don't open clients OnClosing will not see

        // A superseded read must stop its request, not run to NvrHttp's 30s timeout
        // with its clients still open; the task is tracked so shutdown can wait for it.
        _usersCts?.Cancel();
        _usersCts?.Dispose();
        _usersCts = new CancellationTokenSource();
        var task = LoadUsersAsync(_usersCts.Token);
        _usersTask = task;
        try { await task; }
        catch { /* already reported by LoadUsersAsync */ }
        finally
        {
            if (ReferenceEquals(task, _usersTask))
                _usersTask = null;
        }
    }

    private async Task LoadUsersAsync(CancellationToken ct)
    {
        int gen = ++_usersGen;
        if (UsersDeviceA.SelectedItem is not SavedDevice deviceA)
        {
            SetStatus("Choose a device to read users from.");
            return;
        }
        // Picking the same NVR in both boxes is how an operator drops the comparison —
        // a ComboBox bound to the device list has no way back to no selection.
        var deviceB = ReferenceEquals(UsersDeviceB.SelectedItem, deviceA)
            ? null
            : UsersDeviceB.SelectedItem as SavedDevice;

        // Ephemeral clients: reading users must not disturb the session the Live and
        // Playback tabs hold on the selected device, which may be neither of these.
        INvrClient? clientA = null;
        INvrClient? clientB = null;
        try
        {
            clientA = deviceA.CreateClient();
            if (clientA is not IUserManagementClient usersA)
            {
                SetStatus($"{deviceA.Name} does not expose a user list.");
                return;
            }

            IUserManagementClient? usersB = null;
            if (deviceB is not null)
            {
                clientB = deviceB.CreateClient();
                if (clientB is not IUserManagementClient other)
                {
                    SetStatus($"{deviceB.Name} does not expose a user list.");
                    return;
                }
                usersB = other;
            }

            SetStatus(deviceB is null
                ? $"Reading users from {deviceA.Name} …"
                : $"Reading users from {deviceA.Name} and {deviceB.Name} …");

            var taskA = usersA.GetUsersAsync(ct);
            var taskB = usersB is null
                ? Task.FromResult<IReadOnlyList<NvrUser>>([])
                : usersB.GetUsersAsync(ct);
            try
            {
                await Task.WhenAll(taskA, taskB);
            }
            catch (OperationCanceledException)
            {
                _ = taskB.Exception;
                return;
            }
            catch (Exception ex)
            {
                // WhenAll rethrows the first fault only; observe the other so a second
                // failure never surfaces as an unobserved task exception.
                _ = taskB.Exception;
                if (gen != _usersGen)
                    return;
                var failed = taskA.IsFaulted ? deviceA : deviceB!;
                SetStatus($"Failed to read users from {failed.Name}: {Shorten(ex.Message)}");
                return;
            }

            if (gen != _usersGen)
                return;

            if (deviceB is null)
            {
                var listed = taskA.Result
                    .Select(u => new UserRow(u.Name, u.NativeLevel, "", ""))
                    .ToList();
                SetUserLevelHeaders(deviceA.Name, "Device B");
                UsersGrid.ItemsSource = listed;
                SetStatus($"{deviceA.Name}: {listed.Count} user(s).");
                return;
            }

            var comparison = CompareUsers(taskA.Result, taskB.Result, deviceA.Name, deviceB.Name);
            SetUserLevelHeaders(deviceA.Name, deviceB.Name);
            UsersGrid.ItemsSource = comparison.Rows;

            var parts = new List<string> { $"{comparison.Match} match" };
            if (comparison.Differ > 0)
                parts.Add($"{comparison.Differ} differ");
            if (comparison.OnlyA > 0)
                parts.Add($"{comparison.OnlyA} only on {deviceA.Name}");
            if (comparison.OnlyB > 0)
                parts.Add($"{comparison.OnlyB} only on {deviceB.Name}");
            SetStatus($"{comparison.Rows.Count} user(s) — {string.Join(", ", parts)}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (gen != _usersGen)
                return;
            SetStatus($"Failed to read users: {Shorten(ex.Message)}");
        }
        finally
        {
            clientA?.Dispose();
            clientB?.Dispose();
        }
    }

    private sealed record UserComparison(List<UserRow> Rows, int Match, int Differ, int OnlyA, int OnlyB);

    private static UserComparison CompareUsers(IReadOnlyList<NvrUser> a, IReadOnlyList<NvrUser> b,
        string nameA, string nameB)
    {
        // Accounts are paired by name, not by Id: the vendor-native ids are numeric on
        // Hikvision and the login name on Dahua, so only the name compares across vendors.
        var byName = new Dictionary<string, NvrUser>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in b)
            byName.TryAdd(user.Name, user);

        var paired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<UserRow>();
        int match = 0, differ = 0, onlyA = 0, onlyB = 0;
        foreach (var user in a)
        {
            if (!byName.TryGetValue(user.Name, out var counterpart))
            {
                rows.Add(new UserRow(user.Name, user.NativeLevel, "", $"Only on {nameA}"));
                onlyA++;
                continue;
            }
            paired.Add(user.Name);
            bool same = string.Equals(user.NativeLevel, counterpart.NativeLevel,
                StringComparison.OrdinalIgnoreCase);
            rows.Add(new UserRow(user.Name, user.NativeLevel, counterpart.NativeLevel,
                same ? "Match" : "Level differs"));
            if (same)
                match++;
            else
                differ++;
        }
        foreach (var user in b)
        {
            if (paired.Contains(user.Name))
                continue;
            rows.Add(new UserRow(user.Name, "", user.NativeLevel, $"Only on {nameB}"));
            onlyB++;
        }
        return new UserComparison(rows, match, differ, onlyA, onlyB);
    }

    private void SetUserLevelHeaders(string headerA, string headerB)
    {
        UsersGrid.Columns[1].Header = headerA;
        UsersGrid.Columns[2].Header = headerB;
    }

    // ----- helpers -----

    private static string MegabytesOf(string path) =>
        $"{new FileInfo(path).Length / 1048576.0:F1} MB";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

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
