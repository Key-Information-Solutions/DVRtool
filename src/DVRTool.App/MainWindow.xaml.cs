using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
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

    /// <summary>
    /// One Users-tab grid row, either mode: an account (or fob) with one prepared display
    /// cell per selected device. Cells are strings the grid shows verbatim — "—" for absent,
    /// "?" under a device that could not be read — because the columns are built at runtime
    /// and bind by index.
    /// </summary>
    private sealed record FleetRow(string Key, string Name, string[] Cells, string Status);

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
        InitializeUsersTab();

        InitializeAccessTab();
        InitializeStorageTab();
        InitializeConfigTab();
        InitializeLiveStats();
        InitializePlaybackTab();
        InitializeDewarpMode();

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
        _accessCts?.Cancel();
        _storageCts?.Cancel();
        _configCts?.Cancel();
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
        // Likewise for the Access tab: its panel clients hold an SDK login each, and the
        // shared HCNetSDK runtime is only torn down once the last one is disposed.
        if (_accessTask is { } access)
        {
            try { await access; }
            catch { /* canceled/failed; the Access tab already reported it */ }
        }
        // And the Storage tab's ephemeral client — a canceled apply must still finish
        // its current read-back before the process goes away under it.
        if (_storageTask is { } storageWork)
        {
            try { await storageWork; }
            catch { /* canceled/failed; the Storage tab already reported it */ }
        }

        // And the Config tab, which holds the client that did its read for the life of the
        // view — a config write is that client's own document with fields replaced.
        if (_configTask is { } configWork)
        {
            try { await configWork; }
            catch { /* canceled/failed; the Config tab already reported it */ }
        }
        _configClient?.Dispose();
        _configClient = null;

        // Before the players: an SDK preview is the source feeding one of them, and its
        // teardown blocks on the SDK's own receive thread. The grid first — it owns up to
        // sixteen of them on one session.
        // The playback body first: it is an HTTP response (and maybe an ffmpeg) feeding
        // the playback player, and closing it makes that player's stop immediate.
        await DisposePlaybackAsync();
        await DisposeLiveGridAsync();
        await DisposeSdkLiveAsync();
        // The fisheye tab's decoder is a player of its own, fed by an SDK session of its own.
        await DisposeDewarpAsync();

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
        _accessCts?.Dispose();
        _storageCts?.Dispose();
        _configCts?.Dispose();
        _closePending = true;
        Close();
    }

    // ----- device management -----

    private void OnAddDevice(object sender, RoutedEventArgs e)
    {
        var dialog = new AddDeviceWindow(_devices) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            _devices.Add(dialog.Result);
            DeviceStore.Save(_devices);
            DeviceList.SelectedItem = dialog.Result;
        }
    }

    private void OnEditDevice(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not SavedDevice device)
            return;
        int index = _devices.IndexOf(device);
        if (index < 0)
            return;

        var dialog = new AddDeviceWindow(device, _devices) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null)
            return;

        // Replace the item rather than mutate it: SavedDevice raises no change
        // notification, so DeviceList (and the other tabs' device pickers) would keep
        // showing the stale name until something else refreshed them.
        _devices[index] = dialog.Result;
        DeviceStore.Save(_devices);
        DeviceList.SelectedItem = dialog.Result;
    }

    private void OnDeviceListDoubleClick(object sender, MouseButtonEventArgs e) =>
        OnEditDevice(sender, e);

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

        // An SDK preview holds one of the old recorder's stream slots and a login on it.
        // Neither belongs to the device the operator just picked.
        QueuePlayerStop(_livePlayer);
        StopSdkLive();
        StopLiveGrid();
        // Another recorder's picture is not this one's: the mode ends, and nothing is replayed
        // because there is no channel selected yet to replay.
        ExitDewarpMode();
        ResetPlaybackTab();
        UpdateGridPageControls();
        UpdateLiveTransportLabels(DeviceList.SelectedItem as SavedDevice);

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

        // A panel has no channels, no streams and no NVR client — the video tabs have
        // nothing to show for it. Its content lives in the Access tab and the Users tab's
        // Access control mode, which read it on their own connections.
        if (device.IsPanel)
        {
            SetStatus($"{device.Name} is a door panel — read it from the Access tab, or the " +
                "Users tab in Access control mode.");
            return;
        }

        try
        {
            _client = device.CreateClient();
            _currentDevice = device;
            _clientCts = new CancellationTokenSource();
            SetStatus($"Connecting to {device.Name} …");

            // Identity before content. Logging in proves the credentials, not the hardware:
            // several systems behind one address are told apart by forwarded port alone, and
            // one shared account across the fleet means a wrong port yields a healthy-looking
            // channel list, a playable stream and an export filed under this device's name
            // holding another site's footage.
            if (!await VerifyDeviceAsync(device, _client, gen, _clientCts.Token))
                return;

            var channels = await _client.GetChannelsAsync(_clientCts.Token);
            if (gen != _selectionGen)
                return;
            ChannelList.ItemsSource = channels.Select(c => new ChannelItem(c)).ToList();
            if (channels.Count > 0)
                ChannelList.SelectedIndex = 0;
            SetStatus($"{device.Name}: {channels.Count} channel(s).");
            OnChannelsLoadedForLiveGrid();
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

    /// <summary>
    /// Confirms the system that answered is the one this record is bound to, and binds the
    /// record on its first successful connection.
    /// </summary>
    /// <remarks>
    /// On a mismatch the client is dropped, not merely reported: every tab downstream — live,
    /// playback, export, users — would otherwise be pointed at the wrong hardware while the
    /// left-hand list still shows the name the operator picked.
    /// </remarks>
    private async Task<bool> VerifyDeviceAsync(
        SavedDevice device, INvrClient client, int gen, CancellationToken ct)
    {
        IdentityCheck check;
        try
        {
            check = await DeviceIdentityGuard.CheckAsync(client,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name,
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unreachable, refused, bad password: the ordinary connection failures, which the
            // caller's own handler already words for the status bar.
            if (gen == _selectionGen)
                SetStatus($"Failed to connect to {device.Name}: {Shorten(ex.Message)}");
            return false;
        }

        if (gen != _selectionGen)
            return false;

        if (check.Verdict == IdentityVerdict.Mismatch)
        {
            DropClient();
            SetStatus($"{device.Name}: WRONG DEVICE — not connected.");
            MessageBox.Show(this,
                check.Message + "\n\nNothing was read from it. Fix the host or port on this " +
                "record — or, if the recorder itself was replaced, open Edit… and press " +
                "Unbind.",
                "DVRTool — wrong device", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        // First sight of this hardware through this record: bind the two, so the record is
        // pinned even if its address later moves.
        if (device.ExpectedSerial.Length == 0 && check.Seen.IsUsable)
        {
            device.ExpectedSerial = check.Seen.Serial.Trim();
            DeviceStore.Save(_devices);
        }

        if (check.Verdict == IdentityVerdict.Unverifiable)
            SetStatus($"{device.Name}: connected, but it reports no serial — DVRTool cannot " +
                "confirm which system this is.");
        return true;
    }

    /// <summary>
    /// Abandons the client just built for a selection. Safe only here: nothing has had the
    /// chance to start a search or a download through it yet.
    /// </summary>
    private void DropClient()
    {
        _client?.Dispose();
        _client = null;
        _currentDevice = null;
        _clientCts?.Cancel();
        _clientCts?.Dispose();
        _clientCts = null;
        ChannelList.ItemsSource = null;
        ResultsGrid.ItemsSource = null;
    }

    // ----- live: see MainWindow.Live.cs -----

    // ----- playback / export: see MainWindow.Playback.cs -----

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

    /// <summary>Which fleet the Users tab is on: recorders' login accounts, or panels' fobs.</summary>
    private bool UsersAccessMode => UsersModeCombo.SelectedIndex == 1;

    private void InitializeUsersTab()
    {
        // The picker re-queries this every time it opens, so a device added, renamed or
        // removed mid-session shows up without refresh plumbing. IsPanel doubles as the
        // mode filter: the two modes read different hardware, never a mixture.
        UsersDevices.ChoicesProvider = () =>
            _devices.Where(d => d.IsPanel == UsersAccessMode).ToList();
        ApplyUsersMode();
    }

    private void OnUsersModeChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Fires while XAML is still applying SelectedIndex="0", before the picker exists.
        if (UsersDevices is null)
            return;
        ApplyUsersMode();
    }

    private void ApplyUsersMode()
    {
        UsersDevices.Prompt = UsersAccessMode ? "Choose panels…" : "Choose recorders…";
        UsersDevices.EmptyHint = UsersAccessMode
            ? "No door panels saved yet — Add Device… and pick “Door access panel”."
            : "No recorders saved yet — Add Device….";
        UsersDevices.Refresh();

        // A grid still wearing the other mode's columns describes hardware that is no
        // longer what the picker offers.
        UsersGrid.Columns.Clear();
        UsersGrid.ItemsSource = null;
        UsersWarning.Text = "";
        UsersWarning.Visibility = Visibility.Collapsed;
    }

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
        var picked = UsersDevices.Selected;
        if (picked.Count == 0)
        {
            SetStatus(UsersAccessMode
                ? "Pick at least one panel to read."
                : "Pick at least one recorder to read.");
            return;
        }

        try
        {
            if (UsersAccessMode)
                await LoadCardholderMatrixAsync(picked, gen, ct);
            else
                await LoadAccountMatrixAsync(picked, gen, ct);
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
    }

    private async Task LoadAccountMatrixAsync(
        IReadOnlyList<SavedDevice> devices, int gen, CancellationToken ct)
    {
        SetStatus($"Reading accounts from {devices.Count} recorder(s) …");

        // Ephemeral clients, all devices at once: reading users must not disturb the
        // session the Live and Playback tabs hold, and one slow site must not serialize
        // the rest. Each read carries its own failure instead of faulting the batch —
        // the matrix states which columns are unknown rather than dropping them.
        var results = await Task.WhenAll(devices.Select(d => ReadDeviceUsersAsync(d, ct)));
        if (gen != _usersGen)
            return;

        var matrix = UserMatrix.Build(results);
        var readable = matrix.Devices.Select(d => d.Ok).ToList();
        ShowFleetMatrix(
            keyHeader: "User",
            nameHeader: null,
            deviceHeaders: matrix.Devices
                .Select(d => d.Ok ? d.DeviceName : $"{d.DeviceName} ⚠").ToList(),
            rows: matrix.Rows
                .Select(r => new FleetRow(r.User, "", PrepareCells(r.Cells, readable), r.Status))
                .ToList(),
            failures: matrix.FailedDevices.Select(d => (d.DeviceName, d.Error!)).ToList(),
            summary: $"{matrix.Rows.Count} account(s) across " +
                     $"{matrix.Devices.Count(d => d.Ok)} recorder(s)",
            partialCaveat: "An account on an unreadable recorder is unknown, not absent — " +
                "its column shows “?” and it is left out of every row's status.");
    }

    private async Task<DeviceUsersResult> ReadDeviceUsersAsync(
        SavedDevice device, CancellationToken ct)
    {
        INvrClient? client = null;
        try
        {
            client = device.CreateClient();
            if (client is not IUserManagementClient users)
                return DeviceUsersResult.Failed(device.Name,
                    "this device does not expose a user list");

            // Identified before read. An account audit that names the wrong recorder is
            // worse than no audit — and two records behind one address, one shared
            // password between them, is exactly how that happens.
            var check = await DeviceIdentityGuard.CheckAsync(client,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name,
                ct: ct);
            if (check.Verdict == IdentityVerdict.Mismatch)
                return DeviceUsersResult.Failed(device.Name, $"WRONG DEVICE — {check.Message}");

            // First sight of this hardware through this record: bind the two, exactly as
            // device selection does. Safe to save here — every continuation of this method
            // resumes on the UI thread.
            if (device.ExpectedSerial.Length == 0 && check.Seen.IsUsable)
            {
                device.ExpectedSerial = check.Seen.Serial.Trim();
                DeviceStore.Save(_devices);
            }

            return new DeviceUsersResult
            {
                DeviceName = device.Name,
                Users = await users.GetUsersAsync(ct),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DeviceUsersResult.Failed(device.Name, Shorten(ex.Message));
        }
        finally
        {
            client?.Dispose();
        }
    }

    private async Task LoadCardholderMatrixAsync(
        IReadOnlyList<SavedDevice> panels, int gen, CancellationToken ct)
    {
        var entries = panels.Select(p => new PanelEntry(p.ToPanelConnection(), p)).ToList();
        var settings = new PanelConnectionSettings(entries, CurrentSdkDirectory);

        SetStatus($"Reading {entries.Count} panel(s) …");
        var roster = await ReadRosterAsync(settings, ct);
        if (gen != _usersGen)
            return;

        BindPanelSerials(roster, entries);

        var map = TryLoadIdentityMap();
        var enriched = map is null ? roster : roster.EnrichWith(map);

        // Column headers and status prose wear the record names; the join underneath stays
        // on the port-qualified labels every card is stamped with. TryAdd, not ToDictionary:
        // two records on one address is a fleet mistake for FleetAudit to report, not a
        // crash for this view to add to it.
        var displayByLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            displayByLabel.TryAdd(entry.Panel.Label, entry.DisplayName);
        string DisplayName(string label) =>
            displayByLabel.TryGetValue(label, out var name) ? name : label;

        var matrix = AccessMatrix.Build(enriched, DisplayName);
        var readable = matrix.Panels.Select(p => p.Ok).ToList();
        ShowFleetMatrix(
            keyHeader: "Card",
            nameHeader: "Name",
            deviceHeaders: matrix.Panels
                .Select(p => p.Ok
                    ? DisplayName(p.PanelHost)
                    : $"{DisplayName(p.PanelHost)} ⚠").ToList(),
            rows: matrix.Rows
                .Select(r => new FleetRow(r.CardNo, r.Name ?? "",
                    PrepareCells(r.Cells, readable), r.Status))
                .ToList(),
            failures: matrix.FailedPanels
                .Select(p => (DisplayName(p.PanelHost), p.Error ?? "unreadable")).ToList(),
            summary: $"{matrix.Rows.Count} fob(s) across " +
                     $"{matrix.Panels.Count(p => p.Ok)} panel(s)",
            partialCaveat: "A fob could still be active on a panel that could not be read, " +
                "so “no access” is not a safe conclusion from this view.");
    }

    /// <summary>
    /// Rebuilds the Users grid for one load: the key column(s), one column per selected
    /// device, status last. Built in code because the column set is whatever the operator
    /// picked, not something XAML can know.
    /// </summary>
    private void ShowFleetMatrix(string keyHeader, string? nameHeader,
        IReadOnlyList<string> deviceHeaders, IReadOnlyList<FleetRow> rows,
        IReadOnlyList<(string Device, string Error)> failures, string summary,
        string partialCaveat)
    {
        UsersGrid.Columns.Clear();
        UsersGrid.Columns.Add(MatrixColumn(keyHeader, nameof(FleetRow.Key), 130));
        if (nameHeader is not null)
            UsersGrid.Columns.Add(MatrixColumn(nameHeader, nameof(FleetRow.Name), 170));
        for (int i = 0; i < deviceHeaders.Count; i++)
            UsersGrid.Columns.Add(MatrixColumn(deviceHeaders[i], $"Cells[{i}]", 130));
        UsersGrid.Columns.Add(MatrixColumn("Status", nameof(FleetRow.Status), 240));
        UsersGrid.ItemsSource = rows;

        if (failures.Count == 0)
        {
            UsersWarning.Text = "";
            UsersWarning.Visibility = Visibility.Collapsed;
            SetStatus($"{summary}.");
            return;
        }

        // Same rule as the Access tab: a partial view gets a visible warning of its own,
        // not just a status-bar line the operator may have scrolled past.
        UsersWarning.Text = "PARTIAL — " +
            string.Join("; ", failures.Select(f => $"{f.Device}: {Shorten(f.Error)}")) +
            ". " + partialCaveat;
        UsersWarning.Visibility = Visibility.Visible;
        SetStatus($"PARTIAL — {summary}; see the warning above.");
    }

    private static System.Windows.Controls.DataGridTextColumn MatrixColumn(
        string header, string bindingPath, double width) => new()
    {
        Header = header,
        Binding = new System.Windows.Data.Binding(bindingPath),
        Width = width,
    };

    /// <summary>
    /// Turns a matrix row's cells into what the grid shows: the value, "—" where the
    /// device answered and holds no such entry, "?" where the device could not be read —
    /// which is not the same thing, and must not look it.
    /// </summary>
    private static string[] PrepareCells(IReadOnlyList<string?> cells, IReadOnlyList<bool> readable)
    {
        var prepared = new string[cells.Count];
        for (int i = 0; i < cells.Count; i++)
            prepared[i] = cells[i] ?? (readable[i] ? "—" : "?");
        return prepared;
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

    private void SetStatus(string text) => StatusText.Text = text;

    private static string Shorten(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
