using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DVRTool.Core;

namespace DVRTool.App;

/// <summary>
/// The Camera tab: the encoder settings of the cameras on a recorder — resolution, frame rate,
/// codec, CBR/VBR, bitrate, I-frame interval, audio and the channel's name.
/// </summary>
/// <remarks>
/// <para>
/// Hikvision only, through <see cref="ICameraSettingsWriter"/>. It sits beside the Storage tab
/// rather than inside it because the two answer different questions: Storage asks what the
/// cameras will write to disk and plans a fleet's retention, this asks what one camera is
/// configured to do. They meet at the bitrate, and the pins Storage keeps are honoured here —
/// a camera an operator pinned is held back from a rate change and said out loud.
/// </para>
/// <para>
/// The editors offer <b>only what each camera declares it accepts</b>, read from its own
/// capabilities document and intersected across a multi-camera selection
/// (<see cref="EncodingOptions.Intersect"/>). A camera silently ignores a resolution it does
/// not support, so a free-text box here would produce writes that report success and change
/// nothing. Where a camera declares no list at all, the field falls back to free entry and the
/// hint says so — "declared nothing" is not "allows nothing".
/// </para>
/// <para>
/// Apply is the deliberate exception to "GUI writes stay in the CLI", on the same grounds the
/// Storage tab's Apply earns it: an encoder setting is reversible from this same tab. It
/// re-verifies device identity on a fresh client between the read and the write, confirms
/// against a dialog that names every change and defaults to No, writes camera by camera, and
/// repaints from the recorder's own read-back rather than from the plan.
/// </para>
/// </remarks>
public partial class MainWindow
{
    // The same stale-completion guard the Storage and Users tabs use: this tab reads whatever
    // system its own combo names, so it cannot ride on _selectionGen.
    private int _cameraGen;
    private CancellationTokenSource? _cameraCts;
    private Task? _cameraTask;

    private SavedDevice? _cameraDevice;
    private IReadOnlyList<ChannelEncoding>? _cameraTracks;
    private Dictionary<int, string> _cameraNames = [];
    private Dictionary<int, EncodingOptions> _cameraOptions = [];
    private ChannelPinSet? _cameraPins;
    private CameraSettingsPlan? _cameraPlan;
    private EncodingSettings? _cameraRequested;
    private string? _cameraRename;

    /// <summary>The channel <see cref="_cameraRename"/> belongs to. A rename is single-camera.</summary>
    private int? _cameraRenameChannel;

    /// <summary>The literal shown for "leave this field alone" — never a value.</summary>
    private const string Unchanged = "(unchanged)";

    /// <summary>
    /// Full Frame Rate: the camera follows its own maximum, which the firmware re-resolves
    /// whenever the resolution moves. Deliberately its own entry rather than the number it
    /// happens to resolve to today, which would pin a camera that was set to follow.
    /// </summary>
    private const string FullRate = "full";

    private sealed record CameraRow(
        int Ch, string Name, string Stream, string Resolution, string Fps, string Codec,
        string Mode, string Kbps, string Gop, string Audio, string AudioDetail,
        string Planned, string PlannedDetail);

    private void InitializeCameraTab()
    {
        // Re-list on every open, like the Storage tab: a device added, renamed or removed
        // mid-session shows up with no refresh plumbing.
        CameraDeviceCombo.DropDownOpened += (_, _) => RefreshCameraDevices();
        CameraGrid.SelectionChanged += (_, _) => OnCameraSelectionChanged();
        RefreshCameraDevices();
        ResetCameraEditors();
    }

    private void RefreshCameraDevices()
    {
        var current = CameraDeviceCombo.SelectedItem as SavedDevice;
        var recorders = _devices.Where(d => !d.IsPanel).ToList();
        CameraDeviceCombo.ItemsSource = recorders;
        if (current is not null)
            CameraDeviceCombo.SelectedItem = recorders.FirstOrDefault(d =>
                string.Equals(d.Address, current.Address, StringComparison.OrdinalIgnoreCase));
        if (CameraDeviceCombo.SelectedItem is null && recorders.Count > 0)
            CameraDeviceCombo.SelectedIndex = 0;
    }

    private StreamType SelectedCameraStream =>
        ((CameraStreamCombo.SelectedItem as ComboBoxItem)?.Content as string) switch
        {
            "sub" => StreamType.Sub,
            "third" => StreamType.Third,
            _ => StreamType.Main,
        };

    private async void OnLoadCameraSettings(object sender, RoutedEventArgs e) =>
        await RunCameraWorkAsync(LoadCameraSettingsAsync);

    private async void OnApplyCameraSettings(object sender, RoutedEventArgs e) =>
        await RunCameraWorkAsync(ApplyCameraSettingsAsync);

    /// <summary>
    /// Runs one piece of Camera-tab work, superseding whatever was running before it — the same
    /// shape as <c>RunStorageWorkAsync</c>, tracked so shutdown can wait for it.
    /// </summary>
    private async Task RunCameraWorkAsync(Func<CancellationToken, Task> work)
    {
        if (_cleanupStarted)
            return; // the window is closing; do not open clients OnClosing will not see

        _cameraCts?.Cancel();
        _cameraCts?.Dispose();
        _cameraCts = new CancellationTokenSource();
        var task = work(_cameraCts.Token);
        _cameraTask = task;
        try { await task; }
        catch { /* already reported by the work itself */ }
        finally
        {
            if (ReferenceEquals(task, _cameraTask))
                _cameraTask = null;
        }
    }

    // ----- load -----

    private async Task LoadCameraSettingsAsync(CancellationToken ct)
    {
        int gen = ++_cameraGen;
        if (CameraDeviceCombo.SelectedItem is not SavedDevice device)
        {
            SetStatus("Pick a system to read.");
            return;
        }

        HideCameraWarning();
        ClearCameraPlan();
        _cameraDevice = null;
        _cameraTracks = null;
        _cameraNames = [];
        _cameraOptions = [];
        _cameraPins = null;

        INvrClient? client = null;
        try
        {
            client = device.CreateClient();
            if (client is not ICameraSettingsClient camera)
            {
                SetStatus($"Camera settings aren't implemented for {device.VendorKind} devices.");
                CameraGrid.ItemsSource = null;
                CameraSummary.Text = "";
                return;
            }

            SetStatus($"Connecting to {device.Name} …");

            // Identity before content: a settings grid that names the wrong recorder is how
            // somebody re-encodes a building they never meant to touch.
            var check = await DeviceIdentityGuard.CheckAsync(client,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name,
                ct: ct);
            if (gen != _cameraGen)
                return;
            if (check.Verdict == IdentityVerdict.Mismatch)
            {
                ShowCameraWarning($"WRONG DEVICE — {check.Message} Nothing was read.");
                SetStatus($"{device.Name}: WRONG DEVICE — not read.");
                return;
            }
            if (device.ExpectedSerial.Length == 0 && check.Seen.IsUsable)
            {
                device.ExpectedSerial = check.Seen.Serial.Trim();
                DeviceStore.Save(_devices);
            }

            var stream = SelectedCameraStream;
            SetStatus($"{device.Name}: reading camera settings …");
            var tracks = (await camera.GetEncodingAsync(ct))
                .Where(t => t.Stream == stream)
                .ToList();
            if (gen != _cameraGen)
                return;

            // Names are enrichment: a recorder that answers its encoders but stumbles on the
            // channel list still gets an editable grid.
            var names = new Dictionary<int, string>();
            try
            {
                foreach (var ch in await client.GetChannelsAsync(ct))
                    names[ch.Id] = ch.Name;
            }
            catch (NvrException)
            {
            }
            if (gen != _cameraGen)
                return;

            // What each camera declares it will accept. Read up front so the editors can offer
            // real choices and a refusal can name the camera's own list.
            var options = new Dictionary<int, EncodingOptions>();
            foreach (var track in tracks)
            {
                if (await camera.GetEncodingOptionsAsync(track.Channel, track.Stream, ct)
                    is { } declared)
                    options[track.TrackId] = declared;
                if (gen != _cameraGen)
                    return;
            }

            _cameraDevice = device;
            _cameraTracks = tracks;
            _cameraNames = names;
            _cameraOptions = options;
            _cameraPins = ReadCameraPins(device);

            ShowCameraTracks();
            // Render first, then speak: re-rendering the grid sets its own summary, so a status
            // set before it is overwritten.
            SetStatus($"{device.Name}: {tracks.Count} camera(s) read.");
        }
        catch (DeviceIdentityException ex)
        {
            ShowCameraWarning(ex.Message);
            SetStatus($"{device.Name}: WRONG DEVICE — not read.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
        {
            SetStatus($"{device.Name}: {ex.Message}");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// This system's pins, keyed the way every other per-device file is. Unreadable pins are
    /// <b>loud</b>, exactly as the retention planner treats them: "no pins" must never be
    /// inferred from a file that would not open.
    /// </summary>
    private ChannelPinSet? ReadCameraPins(SavedDevice device)
    {
        try
        {
            string address = DeviceIdentityGuard.AddressOf(device.ToConnection());
            return ChannelPinStore.Default.Get(address,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null);
        }
        catch (Exception ex)
        {
            ShowCameraWarning(
                $"The pinned-camera file could not be read ({ex.Message}). Cameras you pinned " +
                "will NOT be protected from a bitrate change here until that is fixed.");
            return null;
        }
    }

    /// <summary>
    /// True while <see cref="ShowCameraTracks"/> is rebuilding the grid. Replacing
    /// <c>ItemsSource</c> drops the selection and raises <c>SelectionChanged</c>, and treating
    /// that as the operator changing their mind would clear the plan the repaint was showing —
    /// leaving an armed Apply button with nothing behind it.
    /// </summary>
    private bool _cameraRepainting;

    private void ShowCameraTracks()
    {
        var tracks = _cameraTracks ?? [];
        var planByTrack = _cameraPlan?.Channels
            .ToDictionary(c => c.Channel * 100 + (int)c.Stream + 1) ?? [];

        var rows = new List<CameraRow>(tracks.Count);
        foreach (var t in tracks)
        {
            planByTrack.TryGetValue(t.TrackId, out var planned);
            rows.Add(new CameraRow(
                Ch: t.Channel,
                Name: _cameraNames.GetValueOrDefault(t.Channel, ""),
                Stream: t.StreamText,
                Resolution: t.Resolution,
                Fps: t.FrameRateText,
                Codec: t.CodecType,
                Mode: t.QualityControlType,
                Kbps: t.BitrateKbps?.ToString(CultureInfo.InvariantCulture) ?? "?",
                Gop: t.GovLength?.ToString(CultureInfo.InvariantCulture) ?? "—",
                // "?" here is "this camera has no audio to configure", not a failed read: a
                // camera without an audio input carries no Audio element at all.
                Audio: t.AudioEnabled is null ? "—" : t.AudioEnabled.Value ? "on" : "off",
                AudioDetail: t.AudioEnabled is null
                    ? "This camera's stream document has no audio section — there is no audio " +
                      "input to switch on or off."
                    : "",
                Planned: DescribePlanned(planned),
                PlannedDetail: planned?.Problem ?? ""));
        }

        // Rebuild the grid without letting it look like the operator deselected everything.
        var selectedChannels = CameraGrid.SelectedItems.OfType<CameraRow>()
            .Select(r => r.Ch)
            .ToHashSet();
        _cameraRepainting = true;
        try
        {
            CameraGrid.ItemsSource = rows;
            if (selectedChannels.Count > 0)
            {
                foreach (var row in rows.Where(r => selectedChannels.Contains(r.Ch)))
                    CameraGrid.SelectedItems.Add(row);
            }
        }
        finally
        {
            _cameraRepainting = false;
        }

        int withOptions = _cameraOptions.Count;
        CameraSummary.Text = tracks.Count == 0
            ? "This recorder reports no stream tracks for the selected stream."
            : $"{tracks.Count} camera(s) on the {SelectedCameraStream.ToString().ToLowerInvariant()} " +
              $"stream. {withOptions} of them declare what they accept" +
              (withOptions < tracks.Count
                  ? "; the rest answer no capabilities document, so their fields are free entry."
                  : ".") +
              (_cameraPins is { Count: > 0 } pins
                  ? $" {pins.Count} camera(s) are pinned in the retention planner and are held " +
                    "back from a bitrate change here."
                  : "");
    }

    private static string DescribePlanned(PlannedChannel? planned) => planned?.Status switch
    {
        null => "",
        ChannelPlanStatus.WillChange => string.Join(", ", planned.Fields),
        ChannelPlanStatus.NoChange => "already set",
        ChannelPlanStatus.OutOfRange => "REFUSED — " + planned.Problem,
        ChannelPlanStatus.Pinned => "HELD BACK — " + planned.Problem,
        _ => planned.Problem,
    };

    // ----- the editors -----

    /// <summary>
    /// Repopulates the editors from what the selected cameras declare. Called on every
    /// selection change, so what is offered always belongs to what is actually selected.
    /// </summary>
    private void OnCameraSelectionChanged()
    {
        if (_cameraRepainting)
            return; // our own repaint, not the operator changing the selection

        var selected = SelectedCameraTracks();
        if (selected.Count == 0)
        {
            ResetCameraEditors();
            return;
        }

        // Only what every selected camera accepts — offering a value one of them would
        // silently drop is how a batch write reports success and changes nothing.
        var declared = EncodingOptions.Intersect(
            selected.Where(t => _cameraOptions.ContainsKey(t.TrackId))
                .Select(t => _cameraOptions[t.TrackId]));

        Fill(CameraResolutionCombo, declared.Resolutions.Select(r => r.ToString()));
        Fill(CameraFpsCombo, declared.FrameRates
            .Select(f => f.ToString("0.##", CultureInfo.InvariantCulture))
            .Concat(declared.SupportsFullFrameRate ? [FullRate] : []));
        Fill(CameraCodecCombo, declared.Codecs);
        Fill(CameraQualityCombo, declared.QualityControlTypes);
        Fill(CameraAudioCombo, selected.Any(t => t.AudioEnabled is not null)
            ? ["on", "off"]
            : []);

        // The rename is a single-camera action: every camera sharing one name is never what
        // somebody meant, and it would undo the labelling the rest of the product reads back.
        CameraNameBox.IsEnabled = selected.Count == 1;
        if (selected.Count == 1)
            CameraNameBox.Text = _cameraNames.GetValueOrDefault(selected[0].Channel, "");
        else
            CameraNameBox.Text = "";

        var hints = new List<string>();
        int undeclared = selected.Count(t => !_cameraOptions.ContainsKey(t.TrackId));
        if (undeclared > 0)
            hints.Add($"{undeclared} of the selected camera(s) declare no limits, so the lists " +
                "below are only as narrow as the ones that do — anything typed is sent as " +
                "typed and the read-back is the only check.");
        if (declared.Bitrate is { } rate)
            hints.Add($"Max kbps: {rate.MinKbps}–{rate.MaxKbps}.");
        if (declared.GovLength is { } gov)
            hints.Add($"I-frame gap: {gov.Min}–{gov.Max}.");
        if (selected.Count > 1 && declared.Resolutions.Count == 0 &&
            selected.Any(t => _cameraOptions.ContainsKey(t.TrackId)))
            hints.Add("These cameras share no resolution, so that field has nothing to offer.");
        CameraOptionsHint.Text = string.Join("  ", hints);

        // A plan belongs to the selection it was built from.
        ClearCameraPlan();
    }

    private static void Fill(ComboBox box, IEnumerable<string> values)
    {
        var items = new List<string> { Unchanged };
        items.AddRange(values);
        box.ItemsSource = items;
        box.SelectedIndex = 0;
        // A field the cameras say nothing about is still writable — it just has no list to
        // pick from, which is a different state from "not allowed".
        box.IsEnabled = items.Count > 1;
    }

    private void ResetCameraEditors()
    {
        foreach (var box in new[]
                 {
                     CameraResolutionCombo, CameraFpsCombo, CameraCodecCombo,
                     CameraQualityCombo, CameraAudioCombo,
                 })
        {
            box.ItemsSource = new List<string> { Unchanged };
            box.SelectedIndex = 0;
            box.IsEnabled = false;
        }
        CameraKbpsBox.Text = "";
        CameraGopBox.Text = "";
        CameraNameBox.Text = "";
        CameraNameBox.IsEnabled = false;
        CameraOptionsHint.Text = "";
        ClearCameraPlan();
    }

    private void OnClearCameraSettings(object sender, RoutedEventArgs e)
    {
        CameraKbpsBox.Text = "";
        CameraGopBox.Text = "";
        foreach (var box in new[]
                 {
                     CameraResolutionCombo, CameraFpsCombo, CameraCodecCombo,
                     CameraQualityCombo, CameraAudioCombo,
                 })
            box.SelectedIndex = 0;
        if (CameraGrid.SelectedItems.Count == 1 &&
            SelectedCameraTracks().FirstOrDefault() is { } one)
            CameraNameBox.Text = _cameraNames.GetValueOrDefault(one.Channel, "");
        ClearCameraPlan();
        SetStatus("Cleared — nothing was written.");
    }

    private List<ChannelEncoding> SelectedCameraTracks()
    {
        if (_cameraTracks is not { } tracks)
            return [];
        var channels = CameraGrid.SelectedItems.OfType<CameraRow>()
            .Select(r => r.Ch)
            .ToHashSet();
        return tracks.Where(t => channels.Contains(t.Channel)).ToList();
    }

    // ----- preview -----

    private void OnPreviewCameraSettings(object sender, RoutedEventArgs e)
    {
        var selected = SelectedCameraTracks();
        if (selected.Count == 0)
        {
            SetStatus("Select the camera(s) to change first.");
            return;
        }

        EncodingSettings requested;
        try
        {
            requested = ReadCameraEditors();
        }
        catch (ArgumentException ex)
        {
            ClearCameraPlan();
            SetStatus(ex.Message);
            return;
        }

        string? rename = null;
        if (selected.Count == 1)
        {
            string typed = CameraNameBox.Text.Trim();
            string current = _cameraNames.GetValueOrDefault(selected[0].Channel, "");
            if (typed.Length > 0 && !string.Equals(typed, current, StringComparison.Ordinal))
                rename = typed;
        }

        if (requested.IsEmpty && rename is null)
        {
            ClearCameraPlan();
            SetStatus("Nothing to change — pick a value, or type a new name.");
            return;
        }

        var plan = CameraSettingsPlan.Build(selected, requested, _cameraOptions, _cameraNames,
            _cameraPins);
        _cameraPlan = plan;
        _cameraRequested = requested;
        _cameraRename = rename;
        _cameraRenameChannel = rename is null ? null : selected[0].Channel;

        ShowCameraTracks();
        CameraPlanSummary.Text = plan.Summary +
            (rename is not null ? $", and ch{selected[0].Channel} renamed to '{rename}'" : "") +
            (plan.HeldBack.Count > 0
                ? " — see the Planned column for what was refused and why."
                : "");
        CameraApplyButton.IsEnabled = plan.WriteCount > 0 || rename is not null;
        SetStatus(plan.WriteCount > 0 || rename is not null
            ? "Previewed — nothing has been written yet."
            : "Nothing to write.");
    }

    /// <summary>
    /// Reads the editors into a request. A field left on "(unchanged)" or blank is left alone,
    /// the same convention the CLI and the writer use.
    /// </summary>
    private EncodingSettings ReadCameraEditors()
    {
        Resolution? resolution = null;
        if (Chosen(CameraResolutionCombo) is { } res)
            resolution = Resolution.Parse(res)
                ?? throw new ArgumentException($"Can't read the resolution '{res}'.");

        double? fps = null;
        bool? fullRate = null;
        if (Chosen(CameraFpsCombo) is { } rate)
        {
            if (string.Equals(rate, FullRate, StringComparison.OrdinalIgnoreCase))
                fullRate = true;
            else if (double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture,
                         out double parsed))
                fps = parsed;
            else
                throw new ArgumentException($"Can't read the frame rate '{rate}'.");
        }

        int? kbps = null;
        if (CameraKbpsBox.Text.Trim() is { Length: > 0 } kbpsText)
        {
            if (!int.TryParse(kbpsText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int parsed) || parsed <= 0)
                throw new ArgumentException(
                    "Max kbps must be a whole number greater than zero.");
            kbps = parsed;
        }

        int? gop = null;
        if (CameraGopBox.Text.Trim() is { Length: > 0 } gopText)
        {
            if (!int.TryParse(gopText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int parsed) || parsed <= 0)
                throw new ArgumentException(
                    "The I-frame gap must be a whole number greater than zero.");
            gop = parsed;
        }

        bool? audio = Chosen(CameraAudioCombo) switch
        {
            null => null,
            "on" => true,
            _ => false,
        };

        return new EncodingSettings(
            Resolution: resolution,
            FrameRateFps: fps,
            FullFrameRate: fullRate,
            CodecType: Chosen(CameraCodecCombo),
            QualityControlType: Chosen(CameraQualityCombo),
            BitrateKbps: kbps,
            GovLength: gop,
            AudioEnabled: audio);

        static string? Chosen(ComboBox box) =>
            box.SelectedItem as string is { } value && value != Unchanged ? value : null;
    }

    private void ClearCameraPlan()
    {
        _cameraPlan = null;
        _cameraRequested = null;
        _cameraRename = null;
        _cameraRenameChannel = null;
        CameraPlanSummary.Text = "";
        CameraApplyButton.IsEnabled = false;
    }

    // ----- apply -----

    private async Task ApplyCameraSettingsAsync(CancellationToken ct)
    {
        int gen = ++_cameraGen;
        if (_cameraPlan is not { } plan || _cameraDevice is not { } device ||
            _cameraRequested is not { } requested)
        {
            SetStatus("Preview the changes first.");
            return;
        }

        var toWrite = plan.Writes;
        string? rename = _cameraRename;
        if (toWrite.Count == 0 && rename is null)
        {
            SetStatus("Nothing to write.");
            return;
        }

        if (_cleanupStarted)
            return;

        var preview = string.Join("\n", toWrite.Take(8).Select(c => "  " + c.Describe()));
        if (toWrite.Count > 8)
            preview += $"\n  … and {toWrite.Count - 8} more";
        if (rename is not null)
            preview += (preview.Length > 0 ? "\n" : "") +
                $"  ch{plan.Channels[0].Channel}: renamed to '{rename}'";

        string held = plan.HeldBack.Count > 0
            ? $"\n\n{plan.HeldBack.Count} camera(s) are NOT being written — see the Planned " +
              "column for why."
            : "";

        if (MessageBox.Show(this,
                $"Change {toWrite.Count} camera(s) on {device.Name}?\n\n" + preview + held +
                "\n\nThis changes what these cameras record. A bitrate or resolution change " +
                "moves how many days the disks hold. Each write is read back and reported.",
                "DVRTool — apply camera settings", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            SetStatus("Apply canceled — nothing was written.");
            return;
        }

        INvrClient? client = null;
        try
        {
            client = device.CreateClient();
            if (client is not ICameraSettingsWriter writer)
            {
                SetStatus($"Camera settings are read-only for {device.VendorKind} devices.");
                return;
            }

            // A write gets the strict identity gate on a fresh client: between the read and the
            // write this address could be answering somewhere else entirely.
            SetStatus($"Verifying {device.Name} before writing …");
            var check = await DeviceIdentityGuard.CheckAsync(client,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name,
                ct: ct);
            DeviceIdentityGuard.Ensure(check);
            if (gen != _cameraGen)
                return;

            var failures = new List<string>();
            int changed = 0, kept = 0;

            for (int i = 0; i < toWrite.Count; i++)
            {
                var one = toWrite[i];
                SetStatus($"{device.Name}: writing ch{one.Channel} ({i + 1} of {toWrite.Count}) …");
                try
                {
                    var result = await writer.SetEncodingAsync(one.Channel, one.Stream,
                        requested, ct);
                    if (result.Rejected)
                    {
                        kept++;
                        failures.Add($"ch{one.Channel}: {result.Note}");
                    }
                    else
                    {
                        changed++;
                    }
                }
                catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
                {
                    // One camera refusing must not abandon the rest — but it is reported,
                    // never counted as done. DeviceIdentityException is deliberately not an
                    // NvrException, so it escapes this handler and aborts the batch.
                    failures.Add($"ch{one.Channel}: {ex.Message}");
                }
                if (gen != _cameraGen)
                    return;
            }

            if (rename is not null && _cameraRenameChannel is int channel)
            {
                SetStatus($"{device.Name}: renaming ch{channel} …");
                try
                {
                    var result = await writer.SetChannelNameAsync(channel, rename, ct);
                    if (result.Rejected)
                    {
                        kept++;
                        failures.Add($"ch{channel} rename: {result.Note}");
                    }
                    else if (result.Changed)
                    {
                        changed++;
                    }
                }
                catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
                {
                    failures.Add($"ch{channel} rename: {ex.Message}");
                }
                if (gen != _cameraGen)
                    return;
            }

            // Repaint from the recorder's own numbers, never from the plan's: what the caller
            // is owed is what the device holds now.
            SetStatus($"{device.Name}: reading back …");
            var after = (await writer.GetEncodingAsync(ct))
                .Where(t => t.Stream == SelectedCameraStream)
                .ToList();
            if (gen != _cameraGen)
                return;

            try
            {
                var names = new Dictionary<int, string>();
                foreach (var ch in await client.GetChannelsAsync(ct))
                    names[ch.Id] = ch.Name;
                _cameraNames = names;
            }
            catch (NvrException)
            {
            }
            if (gen != _cameraGen)
                return;

            _cameraTracks = after;
            ClearCameraPlan();
            ShowCameraTracks();

            if (failures.Count > 0)
            {
                ShowCameraWarning($"PARTIAL APPLY — {changed} written, {kept} kept by the " +
                    $"recorder, {failures.Count - kept} failed:\n" +
                    string.Join("\n", failures.Take(6)) +
                    (failures.Count > 6 ? $"\n… and {failures.Count - 6} more" : ""));
                SetStatus($"{device.Name}: {changed} written, {failures.Count} not applied.");
            }
            else
            {
                HideCameraWarning();
                SetStatus($"{device.Name}: {changed} camera(s) written and verified by read-back.");
            }
        }
        catch (DeviceIdentityException ex)
        {
            MessageBox.Show(this, ex.Message + "\n\nNothing was written.",
                "DVRTool — wrong device", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus($"{device.Name}: WRONG DEVICE — nothing was written.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
        {
            SetStatus($"{device.Name}: {ex.Message}");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    private void ShowCameraWarning(string text)
    {
        CameraWarning.Text = text;
        CameraWarning.Visibility = Visibility.Visible;
    }

    private void HideCameraWarning()
    {
        CameraWarning.Text = "";
        CameraWarning.Visibility = Visibility.Collapsed;
    }
}
