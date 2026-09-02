using System.Windows;
using DVRTool.Core;

namespace DVRTool.App;

/// <summary>
/// The Storage tab: disk inventory, retention ("how many days are we actually holding"),
/// and the bitrate planner — Hikvision and Dahua recorders and DW Spectrum / Nx Witness
/// servers via <see cref="IStorageClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reads follow the Users-tab pattern: an ephemeral client per load, identity checked
/// before content, partial results labeled rather than dropped. The one write — applying
/// a bitrate plan — is deliberately heavier than any other GUI action: it re-verifies
/// device identity on a fresh client, asks for explicit confirmation, writes camera by
/// camera, and reports the read-back value of every write. Unlike door access (whose
/// writes stay CLI-only), a bitrate change is reversible from this same tab, which is why
/// it earns a confirmed button rather than a CLI-only gate.
/// </para>
/// <para>
/// Estimates are worst-case on purpose: recorders in overwrite mode report zero free
/// space forever, so days-held come from capacity ÷ configured max bitrates — including,
/// on Nx, the secondary stream the server archives alongside the main one. See
/// <c>docs/hikvision-storage.md</c>, <c>docs/dahua-storage.md</c> and
/// <c>docs/nx-witness-storage.md</c>.
/// </para>
/// </remarks>
public partial class MainWindow
{
    // Same stale-completion guard as the Users/Access tabs: this tab reads whatever
    // system its own combo names, so it cannot ride on _selectionGen.
    private int _storageGen;
    private CancellationTokenSource? _storageCts;
    private Task? _storageTask;

    // Snapshot of the last successful load; the planner is pure math over it.
    private SavedDevice? _storageDevice;
    private StorageInfo? _storageInfo;
    private IReadOnlyList<StorageCamera>? _storageCameras;
    private BitratePlan? _storagePlan;

    /// <summary>One camera as loaded: its stream config plus the planner's inputs.</summary>
    private sealed record StorageCamera(
        CameraStream Stream, string Name, BitrateRange Range, DateTime? Oldest, string? OldestError);

    private sealed record HddRow(
        string Bay, string Status, string Capacity, string Free, string Type, string Model,
        string Serial);

    private sealed record StorageCameraRow(
        int Ch, string Name, string Codec, string Resolution, string Fps, string Mode,
        string MaxKbps, string Planned, string Oldest, string Days);

    private void InitializeStorageTab()
    {
        // Re-list on every open, like DevicePicker's ChoicesProvider: a device added,
        // renamed or removed mid-session shows up without refresh plumbing.
        StorageDeviceCombo.DropDownOpened += (_, _) => RefreshStorageDevices();
        RefreshStorageDevices();
    }

    private void RefreshStorageDevices()
    {
        var current = StorageDeviceCombo.SelectedItem as SavedDevice;
        var recorders = _devices.Where(d => !d.IsPanel).ToList();
        StorageDeviceCombo.ItemsSource = recorders;
        // Selection survives the rebuild by identity (address), not reference.
        if (current is not null)
            StorageDeviceCombo.SelectedItem = recorders.FirstOrDefault(d =>
                string.Equals(d.Address, current.Address, StringComparison.OrdinalIgnoreCase));
        if (StorageDeviceCombo.SelectedItem is null && recorders.Count > 0)
            StorageDeviceCombo.SelectedIndex = 0;
    }

    private async void OnLoadStorage(object sender, RoutedEventArgs e) =>
        await RunStorageWorkAsync(LoadStorageAsync);

    private async void OnApplyStoragePlan(object sender, RoutedEventArgs e) =>
        await RunStorageWorkAsync(ApplyStoragePlanAsync);

    /// <summary>
    /// Runs one piece of Storage-tab work, superseding whatever was running before it —
    /// the same shape as <c>RunAccessWorkAsync</c>, tracked so shutdown can wait for it.
    /// </summary>
    private async Task RunStorageWorkAsync(Func<CancellationToken, Task> work)
    {
        if (_cleanupStarted)
            return; // window is closing; don't open clients OnClosing will not see

        _storageCts?.Cancel();
        _storageCts?.Dispose();
        _storageCts = new CancellationTokenSource();
        var task = work(_storageCts.Token);
        _storageTask = task;
        try { await task; }
        catch { /* already reported by the work itself */ }
        finally
        {
            if (ReferenceEquals(task, _storageTask))
                _storageTask = null;
        }
    }

    private async Task LoadStorageAsync(CancellationToken ct)
    {
        int gen = ++_storageGen;
        if (StorageDeviceCombo.SelectedItem is not SavedDevice device)
        {
            SetStatus("Pick a system to read.");
            return;
        }

        ClearStoragePlan();
        _storageDevice = null;
        _storageInfo = null;
        _storageCameras = null;

        INvrClient? client = null;
        try
        {
            client = device.CreateClient();
            if (client is not IStorageClient storage)
            {
                SetStatus($"Storage management isn't implemented for {device.VendorKind} devices.");
                return;
            }

            SetStatus($"Connecting to {device.Name} …");

            // Identity before content — a storage report that names the wrong recorder
            // would have someone buying disks for the wrong building.
            var check = await DeviceIdentityGuard.CheckAsync(client,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name,
                ct: ct);
            if (gen != _storageGen)
                return;
            if (check.Verdict == IdentityVerdict.Mismatch)
            {
                ShowStorageWarning($"WRONG DEVICE — {check.Message} Nothing was read.");
                SetStatus($"{device.Name}: WRONG DEVICE — not read.");
                return;
            }
            if (device.ExpectedSerial.Length == 0 && check.Seen.IsUsable)
            {
                device.ExpectedSerial = check.Seen.Serial.Trim();
                DeviceStore.Save(_devices);
            }

            SetStatus($"{device.Name}: reading disks and camera settings …");
            var info = await storage.GetStorageInfoAsync(ct);
            var streams = await storage.GetMainStreamsAsync(ct);
            if (gen != _storageGen)
                return;

            // Names are enrichment; a recorder that answers storage but stumbles on the
            // channel list still gets its report.
            var names = new Dictionary<int, string>();
            try
            {
                foreach (var ch in await client.GetChannelsAsync(ct))
                    names[ch.Id] = ch.Name;
            }
            catch (NvrException)
            {
            }
            if (gen != _storageGen)
                return;

            var cameras = new List<StorageCamera>(streams.Count);
            bool wantOldest = StorageOldestCheck.IsChecked == true;
            for (int i = 0; i < streams.Count; i++)
            {
                var s = streams[i];

                // The writable range now, so the planner previews offline later.
                var range = s.Enabled
                    ? await storage.GetBitrateRangeAsync(s.Channel, ct)
                        ?? new BitrateRange(32, 16384)
                    : new BitrateRange(32, 16384);

                DateTime? oldest = null;
                string? oldestError = null;
                if (wantOldest)
                {
                    SetStatus($"{device.Name}: oldest footage, camera {i + 1} of {streams.Count} …");
                    try
                    {
                        oldest = await storage.FindOldestRecordingAsync(s.Channel, ct);
                    }
                    catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
                    {
                        oldestError = ex.Message;
                    }
                }
                if (gen != _storageGen)
                    return;
                cameras.Add(new StorageCamera(s, names.GetValueOrDefault(s.Channel, ""),
                    range, oldest, oldestError));
            }

            _storageDevice = device;
            _storageInfo = info;
            _storageCameras = cameras;
            ShowStorage(device, info, cameras);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (gen != _storageGen)
                return;
            SetStatus($"Storage read failed: {Shorten(ex.Message)}");
        }
        finally
        {
            client?.Dispose();
        }
    }

    private void ShowStorage(SavedDevice device, StorageInfo info,
        IReadOnlyList<StorageCamera> cameras)
    {
        StorageHddGrid.ItemsSource = info.Hdds.Select(h => new HddRow(
            h.Id.ToString(),
            h.IsInstalled ? h.Status : "empty*",
            h.IsInstalled ? FormatTb(h.CapacityMB) : "—",
            h.IsInstalled ? FormatTb(h.FreeSpaceMB) : "—",
            h.HddType, h.Model, h.SerialNumber)).ToList();

        StorageDiskSummary.Text =
            $"{info.InstalledCount} disk(s) installed, {FormatTb(info.TotalCapacityMB)} total" +
            (info.WorkMode is { Length: > 0 } mode ? $", work mode {mode}" : "") +
            (info.MaxSupportedHdds is int max ? $"; firmware supports up to {max} disks" : "") +
            "." +
            (info.GhostBayCount > 0
                ? $" *{info.GhostBayCount} bay(s) remember a removed disk — wired, currently empty."
                : "") +
            (info.TotalFreeSpaceMB == 0 && info.InstalledCount > 0
                ? " Free 0 is normal: the recorder overwrites oldest footage continuously."
                : "");

        StorageCameraGrid.ItemsSource = BuildCameraRows(cameras, plannedByChannel: null);

        var now = DateTime.Now;
        long totalKbps = EnabledMaxTotalKbps(cameras);
        long secondaryKbps = cameras.Where(c => c.Stream.Enabled)
            .Sum(c => (long)(c.Stream.SecondaryRecordedKbps ?? 0));
        int enabled = cameras.Count(c => c.Stream.Enabled);
        DateTime? systemOldest = cameras
            .Where(c => c.Oldest is not null)
            .Min(c => c.Oldest);

        string summary =
            $"{enabled} enabled camera(s), {totalKbps:N0} kbps configured max total" +
            (secondaryKbps > 0
                ? $" (including {secondaryKbps:N0} kbps of secondary streams the recorder archives too)."
                : ".");
        if (StorageEstimator.EstimateRetentionDays(info.TotalCapacityMB, totalKbps) is double est)
            summary += $" Worst-case retention: {est:F1} days.";
        if (systemOldest is DateTime so)
            summary += $" Oldest footage on the system: {so:yyyy-MM-dd HH:mm} — " +
                       $"{(now - so).TotalDays:F1} days held.";
        StorageCameraSummary.Text = summary;

        var problems = new List<string>();
        foreach (var bad in info.UnhealthyHdds)
            problems.Add($"bay {bad.Id} ({(bad.Model.Length > 0 ? bad.Model : bad.Name)}) " +
                         $"reports status '{bad.Status}'");
        var searchFailures = cameras.Where(c => c.OldestError is not null).ToList();
        if (searchFailures.Count > 0)
            problems.Add($"oldest-footage search failed on {searchFailures.Count} camera(s) — " +
                "their days-held are unknown, not zero");
        if (problems.Count > 0)
            ShowStorageWarning(string.Join("; ", problems) + ".");
        else
            HideStorageWarning();

        SetStatus($"{device.Name}: {info.InstalledCount} disk(s), {enabled} camera(s) read.");
    }

    private List<StorageCameraRow> BuildCameraRows(IReadOnlyList<StorageCamera> cameras,
        IReadOnlyDictionary<int, int>? plannedByChannel)
    {
        var now = DateTime.Now;
        return cameras.Select(c =>
        {
            var s = c.Stream;
            string oldest = c.OldestError is not null
                ? "(search failed)"
                : c.Oldest is DateTime t
                    ? t.ToString("yyyy-MM-dd HH:mm:ss")
                    : StorageOldestCheck.IsChecked == true ? "(no recordings)" : "";
            string days = c.Oldest is DateTime o ? $"{(now - o).TotalDays:F1}" : "";
            string planned = plannedByChannel is not null &&
                             plannedByChannel.TryGetValue(s.Channel, out int p)
                ? p.ToString()
                : "";
            // "4096 (+512)" when the recorder archives a second stream alongside the main
            // one (Nx): the cap the plan can change, plus the part it cannot.
            string maxKbps = (s.MaxBitrateKbps?.ToString() ?? "?") +
                             (s.SecondaryRecordedKbps is int sec ? $" (+{sec})" : "");
            return new StorageCameraRow(
                s.Channel, c.Name, s.CodecType, s.Resolution,
                s.FrameRateText,
                s.Enabled ? s.QualityControlType : $"{s.QualityControlType} (off)",
                maxKbps,
                planned, oldest, days);
        }).ToList();
    }

    /// <summary>Everything the enabled cameras write: main-stream caps plus any archived second streams.</summary>
    private static long EnabledMaxTotalKbps(IReadOnlyList<StorageCamera> cameras) =>
        cameras.Where(c => c.Stream.Enabled)
            .Sum(c => (long)(c.Stream.RecordedBitrateKbps ?? 0));

    // ----- planner -----

    private void OnPreviewStoragePlan(object sender, RoutedEventArgs e)
    {
        if (_storageInfo is not { } info || _storageCameras is not { } cameras ||
            _storageDevice is not { } device)
        {
            SetStatus("Load a system first — the planner works from what was read.");
            return;
        }
        if (!double.TryParse(StorageTargetDaysBox.Text.Trim(), out double days) || days <= 0)
        {
            SetStatus("Enter the target days (e.g. 30).");
            return;
        }

        // A second stream the recorder archives alongside the main one (Nx) is a fixed cost
        // the plan spends before splitting the rest; it is never written.
        var planCameras = cameras
            .Where(c => c.Stream.Enabled)
            .Select(c => new PlanCamera(c.Stream.Channel, c.Name, c.Stream.MaxBitrateKbps,
                c.Range.MinKbps, c.Range.MaxKbps, FixedKbps: c.Stream.SecondaryRecordedKbps ?? 0))
            .ToList();
        if (planCameras.Count == 0)
        {
            SetStatus("No enabled cameras to plan for.");
            return;
        }

        var plan = StorageEstimator.PlanUniform(info.TotalCapacityMB, days, planCameras);
        _storagePlan = plan;

        StorageCameraGrid.ItemsSource = BuildCameraRows(cameras,
            plan.Cameras.ToDictionary(c => c.Channel, c => c.PlannedKbps));

        int changes = plan.Cameras.Count(c => c.Changes);
        int clamped = plan.Cameras.Count(c => c.Clamped);
        string text =
            $"{days:F1} days on {FormatTb(info.TotalCapacityMB)} → {plan.UniformKbps} kbps per " +
            $"camera; planned total {plan.PlannedTotalKbps:N0} kbps → estimated " +
            $"{plan.EstimatedDays:F1} days (worst-case). {changes} camera(s) would change" +
            (clamped > 0 ? $", {clamped} clamped to their writable range" : "") + ".";
        if (!plan.MeetsTarget)
            text += " ⚠ The target is NOT reached — camera minimums keep the total above " +
                    "the budget (more disk, fewer cameras, or a lower target).";
        StoragePlanSummary.Text = text;
        StorageApplyButton.IsEnabled = changes > 0;
        SetStatus(changes > 0
            ? $"Plan previewed — Apply writes {changes} camera(s) on {device.Name}."
            : "Plan previewed — every camera is already at its planned bitrate.");
    }

    private void ClearStoragePlan()
    {
        _storagePlan = null;
        StorageApplyButton.IsEnabled = false;
        StoragePlanSummary.Text = "";
    }

    private async Task ApplyStoragePlanAsync(CancellationToken ct)
    {
        int gen = ++_storageGen;
        if (_storagePlan is not { } plan || _storageDevice is not { } device ||
            _storageInfo is not { } info || _storageCameras is not { } cameras)
        {
            SetStatus("Preview a plan first.");
            return;
        }

        var toWrite = plan.Cameras.Where(c => c.Changes).ToList();
        if (toWrite.Count == 0)
        {
            SetStatus("Nothing to write — every camera is already at its planned bitrate.");
            return;
        }

        if (_cleanupStarted)
            return;
        var preview = string.Join("\n", toWrite.Take(8).Select(c =>
            $"  ch{c.Channel} {c.Name}: {c.CurrentKbps?.ToString() ?? "?"} → {c.PlannedKbps} kbps"));
        if (toWrite.Count > 8)
            preview += $"\n  … and {toWrite.Count - 8} more";
        if (MessageBox.Show(this,
                $"Write max recording bitrate to {toWrite.Count} camera(s) on {device.Name}?\n\n" +
                preview + "\n\n" +
                $"Planned result: ~{plan.EstimatedDays:F1} days of retention (worst-case). " +
                "This changes recording quality on every camera it touches. Each write is " +
                "read back and reported.",
                "DVRTool — apply bitrate plan", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            SetStatus("Apply canceled — nothing was written.");
            return;
        }

        INvrClient? client = null;
        try
        {
            client = device.CreateClient();
            if (client is not IStorageClient storage)
            {
                SetStatus($"Storage management isn't implemented for {device.VendorKind} devices.");
                return;
            }

            // A write gets the strict identity gate: mismatch (or a serial that cannot be
            // verified against a pinned expectation) throws rather than warns.
            SetStatus($"Verifying {device.Name} before writing …");
            var check = await DeviceIdentityGuard.CheckAsync(client,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name,
                ct: ct);
            DeviceIdentityGuard.Ensure(check);
            if (gen != _storageGen)
                return;

            var failures = new List<string>();
            var actualByChannel = new Dictionary<int, int>();
            for (int i = 0; i < toWrite.Count; i++)
            {
                var cam = toWrite[i];
                SetStatus($"Writing camera {cam.Channel} ({i + 1} of {toWrite.Count}) …");
                try
                {
                    int actual = await storage.SetMaxBitrateAsync(cam.Channel, cam.PlannedKbps, ct);
                    actualByChannel[cam.Channel] = actual;
                }
                catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
                {
                    failures.Add($"ch{cam.Channel}: {Shorten(ex.Message)}");
                }
                if (gen != _storageGen)
                    return;
            }

            // Re-read the streams so the grid and totals show the device's own numbers,
            // not the plan's hopes.
            var streams = await storage.GetMainStreamsAsync(ct);
            if (gen != _storageGen)
                return;
            var byChannel = streams.ToDictionary(s => s.Channel);
            var refreshed = cameras
                .Select(c => byChannel.TryGetValue(c.Stream.Channel, out var s)
                    ? c with { Stream = s }
                    : c)
                .ToList();
            _storageCameras = refreshed;
            ClearStoragePlan();
            ShowStorage(device, info, refreshed);

            long newTotal = EnabledMaxTotalKbps(refreshed);
            double? newEst = StorageEstimator.EstimateRetentionDays(info.TotalCapacityMB, newTotal);
            int snapped = actualByChannel.Count(kv =>
                plan.Cameras.First(c => c.Channel == kv.Key).PlannedKbps != kv.Value);
            string outcome =
                $"Wrote {actualByChannel.Count} of {toWrite.Count} camera(s)" +
                (snapped > 0 ? $" ({snapped} snapped to their own steps)" : "") +
                (newEst is double d ? $"; new worst-case retention ≈ {d:F1} days." : ".");
            if (failures.Count > 0)
            {
                ShowStorageWarning($"PARTIAL APPLY — {string.Join("; ", failures)}. " +
                    "The grid shows read-back values; failed cameras kept their old rate.");
                SetStatus($"PARTIAL — {outcome}");
            }
            else
            {
                SetStatus($"{outcome} All verified by read-back.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (DeviceIdentityException ex)
        {
            if (gen != _storageGen)
                return;
            SetStatus($"{device.Name}: WRONG DEVICE — nothing was written.");
            if (!_cleanupStarted)
                MessageBox.Show(this, ex.Message + "\n\nNothing was written.",
                    "DVRTool — wrong device", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            if (gen != _storageGen)
                return;
            SetStatus($"Apply failed: {Shorten(ex.Message)}");
        }
        finally
        {
            client?.Dispose();
        }
    }

    // ----- helpers -----

    private void ShowStorageWarning(string text)
    {
        StorageWarning.Text = text;
        StorageWarning.Visibility = Visibility.Visible;
    }

    private void HideStorageWarning()
    {
        StorageWarning.Text = "";
        StorageWarning.Visibility = Visibility.Collapsed;
    }

    private static string FormatTb(long mb) => mb switch
    {
        <= 0 => "0",
        < 1_000_000 => $"{mb / 1_000.0:F0} GB",
        _ => $"{mb / 1_000_000.0:F2} TB",
    };
}
