using System.Globalization;
using System.Text;
using System.Windows;
using DVRTool.Core;

namespace DVRTool.App;

/// <summary>
/// The Config tab: what a recorder says about <em>itself</em> — its clock, its time source,
/// its service ports, its LAN address — plus the <b>fleet clock audit</b>, which is the tab's
/// reason to exist.
/// </summary>
/// <remarks>
/// <para>
/// Footage search, the timeline (<see cref="TimelineWindow"/>, <see cref="PlaybackClock"/>) and
/// export filenames (<see cref="ExportNaming"/>) all run on NVR-local wall clock, so a recorder
/// whose clock is out stamps the wrong time onto everything it writes — and the 2026-09-09
/// discovery pass found two wrong clocks on the first three recorders it read. Reading clocks
/// across the fleet is a correctness check on the export product this app already ships, which
/// is why the fleet panel is reachable without picking a device.
/// </para>
/// <para>
/// Two rendering rules, and they are the point of the whole design:
/// <b>out of scope renders "n/a" with the reason; a failed read renders "?"</b>, never the same
/// glyph and never a blank — the Users tab's precedent, applied to a tab that would otherwise
/// show an Nx server an empty Network grid and imply a third of the fleet is broken. And a
/// range that came from the device is shown as the device's, while one that came from
/// <see cref="ServicePortRange.Fallback"/> says it is ours.
/// </para>
/// <para>
/// The writes here (NTP, zone, DST, name, set-clock) are the same deliberate exception the
/// Storage tab's Apply earned: reversible from this same tab. Address and port writes are not,
/// and are offered nowhere — see <c>docs/device-config-spec.md</c> §7.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private int _configGen;
    private CancellationTokenSource? _configCts;
    private Task? _configTask;

    // The device and the client that produced the current view. The client is kept for the
    // life of the view because a config write is read-modify-write and the writer refuses to
    // write without the read that this client did — the same reason the Nx client keeps its
    // camera list.
    private SavedDevice? _configDevice;
    private INvrClient? _configClient;
    private DeviceConfiguration? _configDoc;

    private sealed record ConfigFleetRow(
        string Device, string Clock, string Drift, string Source, string Verdict, string Detail);

    /// <param name="Saved">
    /// How the device's port compares with the port DVRTool has saved for it — the cheap
    /// consistency check a single ports document makes possible, and the one that catches a
    /// record left pointing at a stale forward.
    /// </param>
    private sealed record ConfigPortRow(
        string Protocol, string Port, string State, string Range, string RangeDetail,
        string Saved, string SavedDetail);

    private void InitializeConfigTab()
    {
        // Re-list on every open, like the Storage tab: a device added, renamed or removed
        // mid-session shows up without refresh plumbing.
        ConfigDeviceCombo.DropDownOpened += (_, _) => RefreshConfigDevices();
        RefreshConfigDevices();
    }

    private void RefreshConfigDevices()
    {
        var current = ConfigDeviceCombo.SelectedItem as SavedDevice;
        var recorders = _devices.Where(d => !d.IsPanel).ToList();
        ConfigDeviceCombo.ItemsSource = recorders;
        if (current is not null)
            ConfigDeviceCombo.SelectedItem = recorders.FirstOrDefault(d =>
                string.Equals(d.Address, current.Address, StringComparison.OrdinalIgnoreCase));
        if (ConfigDeviceCombo.SelectedItem is null && recorders.Count > 0)
            ConfigDeviceCombo.SelectedIndex = 0;
    }

    private async void OnLoadConfig(object sender, RoutedEventArgs e) =>
        await RunConfigWorkAsync(LoadConfigAsync);

    private async void OnAuditFleetClocks(object sender, RoutedEventArgs e) =>
        await RunConfigWorkAsync(AuditFleetClocksAsync);

    private async void OnApplyConfig(object sender, RoutedEventArgs e) =>
        await RunConfigWorkAsync(ApplyConfigAsync);

    private async void OnSyncConfigClock(object sender, RoutedEventArgs e) =>
        await RunConfigWorkAsync(SyncConfigClockAsync);

    /// <summary>
    /// Runs one piece of Config-tab work, superseding whatever was running before it — the
    /// same shape as the Storage and Access tabs, tracked so shutdown can wait for it.
    /// </summary>
    private async Task RunConfigWorkAsync(Func<CancellationToken, Task> work)
    {
        if (_cleanupStarted)
            return; // window is closing; don't open clients OnClosing will not see

        _configCts?.Cancel();
        _configCts?.Dispose();
        _configCts = new CancellationTokenSource();
        var task = work(_configCts.Token);
        _configTask = task;
        try { await task; }
        catch { /* already reported by the work itself */ }
        finally
        {
            if (ReferenceEquals(task, _configTask))
                _configTask = null;
        }
    }

    // ----- the fleet clock audit -----

    private async Task AuditFleetClocksAsync(CancellationToken ct)
    {
        int gen = ++_configGen;
        var recorders = _devices.Where(d => !d.IsPanel).ToList();
        if (recorders.Count == 0)
        {
            SetStatus("No recorders saved yet — Add Device….");
            return;
        }

        ConfigWarning.Visibility = Visibility.Collapsed;
        SetStatus($"Reading the clock of {recorders.Count} recorder(s) …");

        // Ephemeral clients, all recorders at once: one slow site must not serialize the rest,
        // and each read carries its own failure instead of faulting the sweep.
        var rows = await Task.WhenAll(recorders.Select(d => SweepOneAsync(d, ct)));
        if (gen != _configGen)
            return;

        var audit = ConfigAudit.Build(rows);
        ConfigFleetGrid.ItemsSource = audit.Rows.Select(r => new ConfigFleetRow(
            r.DeviceName,
            r.Ok ? r.Clock!.WallClockText : "?",
            r.Ok ? r.Drift!.Text : "?",
            r.Ok
                ? r.TimeSource.NtpEnabled switch { true => "NTP", false => "none", null => "n/a" }
                : "?",
            r.Verdict,
            FleetRowDetail(r))).ToList();

        ConfigFleetSummary.Text = audit.Summary;
        if (audit.Faults.Any() || audit.IsPartial)
            ShowConfigWarning(audit.Faults.Any()
                ? "Clocks to fix: " + string.Join("; ",
                    audit.Faults.Select(f => $"{f.DeviceName} — {f.Verdict}"))
                : "Some recorders could not be read — their clocks are unknown, not fine.");
        SetStatus(audit.Summary);
    }

    private static string FleetRowDetail(ClockAuditRow row)
    {
        if (!row.Ok)
            return row.Error ?? "";
        var text = new StringBuilder();
        text.AppendLine($"This workstation read {row.Drift!.ReferenceWallClock:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"Round trip ±{row.Drift.ReadLatency.TotalSeconds:0.0} s");
        if (row.Drift.ExpectedOffsetMinutes is int shift)
            text.AppendLine($"Measured against this recorder's own zone ({shift:+#;-#;0} min)");
        if (row.ZoneText.Length > 0)
            text.AppendLine($"Zone (the vendor's own words): {row.ZoneText}");
        if (row.Clock!.DstEnabled is bool dst)
            text.AppendLine($"DST: {(dst ? "on" : "off")}");
        if (row.Clock.DeclaredOffsetText is { } offset)
            text.AppendLine($"The device claims offset {offset}" +
                (row.Drift.DeclaredOffsetDiffers
                    ? " — which disagrees with this workstation; DVRTool compares wall clocks " +
                      "and ignores it"
                    : ""));
        if (row.TimeSource.Detail.Length > 0)
            text.AppendLine($"Time source: {row.TimeSource.Detail}");
        return text.ToString().TrimEnd();
    }

    private async Task<ClockAuditRow> SweepOneAsync(SavedDevice device, CancellationToken ct)
    {
        INvrClient? client = null;
        try
        {
            client = SavedDeviceClients.CreateClient(device);
            if (client is not IDeviceConfigClient config)
                return ClockAuditRow.Failed(device.Name,
                    $"reading the clock isn't implemented for {device.VendorKind} devices");

            // Identity before content, like every other fleet read: host:port names a socket,
            // not a recorder, and a clock audit that credits the wrong box is worse than none.
            var check = await DeviceIdentityGuard.CheckAsync(client,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name,
                ct: ct);
            if (check.Verdict == IdentityVerdict.Mismatch)
                return ClockAuditRow.Failed(device.Name, $"WRONG DEVICE — {check.Message}");

            return await ClockSweep.ReadAsync(device.Name, config, device.ExpectedOffsetMinutes,
                ct);
        }
        catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
        {
            return ClockAuditRow.Failed(device.Name, Shorten(ex.Message));
        }
        finally
        {
            client?.Dispose();
        }
    }

    // ----- one recorder -----

    private async Task LoadConfigAsync(CancellationToken ct)
    {
        int gen = ++_configGen;
        if (ConfigDeviceCombo.SelectedItem is not SavedDevice device)
        {
            SetStatus("Pick a system to read.");
            return;
        }

        ClearConfigView();
        try
        {
            var client = SavedDeviceClients.CreateClient(device);
            if (client is not IDeviceConfigClient config)
            {
                client.Dispose();
                SetStatus($"Reading device settings isn't implemented for " +
                    $"{device.VendorKind} devices.");
                return;
            }

            SetStatus($"Connecting to {device.Name} …");
            var check = await DeviceIdentityGuard.CheckAsync(client,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name,
                ct: ct);
            if (gen != _configGen)
            {
                client.Dispose();
                return;
            }
            if (check.Verdict == IdentityVerdict.Mismatch)
            {
                client.Dispose();
                ShowConfigWarning($"WRONG DEVICE — {check.Message} Nothing was read.");
                SetStatus($"{device.Name}: WRONG DEVICE — not read.");
                return;
            }
            if (device.ExpectedSerial.Length == 0 && check.Seen.IsUsable)
            {
                device.ExpectedSerial = check.Seen.Serial.Trim();
                DeviceStore.Save(_devices);
            }

            SetStatus($"{device.Name}: reading its own settings …");
            var started = DateTimeOffset.Now;
            var doc = await config.GetConfigurationAsync(ct);
            var finished = DateTimeOffset.Now;
            if (gen != _configGen)
            {
                client.Dispose();
                return;
            }

            // The client that did the read is the one a write has to go through.
            _configClient?.Dispose();
            _configClient = client;
            _configDevice = device;
            _configDoc = doc;

            ShowConfig(device, doc,
                ClockDrift.Measure(doc.Clock, started, finished, device.ExpectedOffsetMinutes));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (gen != _configGen)
                return;
            SetStatus($"Device settings read failed: {Shorten(ex.Message)}");
        }
    }

    private void ClearConfigView()
    {
        _configDoc = null;
        _configDevice = null;
        _configClient?.Dispose();
        _configClient = null;
        ConfigWarning.Visibility = Visibility.Collapsed;
        ConfigClockText.Text = "";
        ConfigNetworkText.Text = "";
        ConfigNotesText.Text = "";
        ConfigPortGrid.ItemsSource = null;
        ConfigWriteResult.Text = "";
        ConfigApplyButton.IsEnabled = false;
        ConfigSyncNowButton.IsEnabled = false;
    }

    private void ShowConfig(SavedDevice device, DeviceConfiguration doc, ClockDrift drift)
    {
        var row = ClockAuditRow.For(device.Name, doc.Clock, drift, doc.TimeSource);

        var clock = new StringBuilder();
        clock.AppendLine($"{doc.Clock.WallClockText}   (this workstation: " +
            $"{drift.ReferenceWallClock:HH:mm:ss}" +
            (drift.ExpectedOffsetMinutes is int shift
                ? $", shifted {shift:+#;-#;0} min for this recorder's zone"
                : "") + ")");
        clock.AppendLine($"drift {drift.Text}   ±{drift.ReadLatency.TotalSeconds:0.0} s " +
            "(the read's own round trip)");

        // The offset is shown as a claim DVRTool ignores, because an operator comparing this
        // against the recorder's own web UI has to see why the two disagree.
        if (doc.Clock.DeclaredOffsetText is { } offset)
            clock.AppendLine(drift.DeclaredOffsetDiffers
                ? $"the device claims offset {offset} — it disagrees with this workstation, " +
                  "and DVRTool compares wall clocks and ignores it"
                : $"offset {offset}");
        clock.AppendLine("zone: " + (doc.Scope.TimeZone
            ? doc.Clock.VendorZoneLabel is { Length: > 0 } label ? label : "?"
            : $"n/a — {doc.Scope.Reason}"));
        if (doc.Clock.DstEnabled is bool dst)
            clock.AppendLine($"DST: {(dst ? "on" : "off")}");
        clock.AppendLine("time source: " + (doc.Scope.Ntp
            ? doc.NtpEnabled switch
            {
                true => $"NTP — {doc.NtpSummary}",
                false => $"NONE — nothing is keeping this clock ({doc.NtpSummary})",
                null => "?",
            }
            : $"n/a — {doc.Scope.Reason}"));
        if (row.Verdict.Length > 0)
            clock.AppendLine($"verdict: {row.Verdict}");
        ConfigClockText.Text = clock.ToString().TrimEnd();

        ShowConfigPorts(device, doc);
        ConfigNetworkText.Text = DescribeNetwork(doc);
        ConfigNotesText.Text = DescribeNotes(doc);

        // The write boxes start from what the recorder holds, so Apply writes only what the
        // operator actually changed.
        ConfigNtpServerBox.Text = doc.NtpServers?.FirstOrDefault()?.Address ?? "";
        ConfigNtpPortBox.Text = doc.NtpServers?.FirstOrDefault()?.Port.ToString() ?? "";
        ConfigNtpIntervalBox.Text = doc.NtpInterval is TimeSpan interval
            ? ((int)interval.TotalMinutes).ToString(CultureInfo.InvariantCulture)
            : "";
        ConfigNtpEnabledCheck.IsChecked = doc.NtpEnabled ?? false;
        ConfigNtpEnabledCheck.IsEnabled = doc.Scope.Ntp;
        ConfigZoneBox.Text = doc.Clock.VendorZoneLabel ?? "";
        ConfigZoneBox.IsEnabled = doc.Scope.TimeZone;
        ConfigDstCheck.IsChecked = doc.Clock.DstEnabled ?? false;
        // Hikvision folds DST into the zone string and has no switch to set; a checkbox that
        // silently does nothing is worse than a disabled one.
        ConfigDstCheck.IsEnabled = doc.Clock.DstEnabled is not null;
        ConfigNameBox.Text = doc.DeviceName ?? "";

        bool writable = _configClient is IDeviceConfigWriter;
        ConfigApplyButton.IsEnabled = writable;
        ConfigSyncNowButton.IsEnabled = writable;
        ConfigWriteResult.Text = writable
            ? ""
            : "This system has no writable device configuration — a software VMS keeps one " +
              "clock for the whole system and takes its zone from the Windows box it runs on.";

        var problems = new List<string>();
        if (row.IsFault)
            problems.Add(row.Verdict);
        foreach (var failure in doc.Failures)
            problems.Add($"{failure.Label} could not be read ({failure.Value})");
        if (problems.Count > 0)
            ShowConfigWarning(string.Join("; ", problems) + ".");

        SetStatus($"{device.Name}: {(row.Verdict.Length > 0 ? row.Verdict : "clock in step")}.");
    }

    private void ShowConfigPorts(SavedDevice device, DeviceConfiguration doc)
    {
        if (!doc.Scope.Ports)
        {
            ConfigPortGrid.ItemsSource = new[]
            {
                new ConfigPortRow("n/a", "", "", "", "", doc.Scope.Reason, doc.Scope.Reason),
            };
            return;
        }

        var rows = new List<ConfigPortRow>();
        foreach (var port in doc.Ports)
        {
            var (range, fromDevice) = port.EffectiveRange;
            var (saved, savedDetail) = CompareSavedPort(device, port);
            string extras = string.Join(", ", port.Extras.Select(e => $"{e.Key} = {e.Value}"));
            rows.Add(new ConfigPortRow(
                port.Protocol,
                port.Port.ToString(CultureInfo.InvariantCulture),
                port.Enabled ? "enabled" : "disabled",
                range.Text + (fromDevice ? "" : " *"),
                fromDevice
                    ? "The range this device declares for the port."
                    : $"* {ServicePortRange.FallbackLabel}.",
                saved,
                extras.Length > 0 ? $"{savedDetail}\n{extras}" : savedDetail));
        }
        if (rows.Count == 0)
            rows.Add(new ConfigPortRow("—", "", "", "", "",
                "this device exposes no port configuration by name",
                "On Dahua only RTSP answers; HTTP, HTTPS, Telnet and the client port all " +
                "return a 403 that is indistinguishable from a permission denial, so DVRTool " +
                "does not ask for them."));
        ConfigPortGrid.ItemsSource = rows;
    }

    /// <summary>
    /// The device's port against the port the saved record dials. A record left pointing at a
    /// stale forward is exactly what this catches, and it costs no extra request.
    /// </summary>
    private static (string Text, string Detail) CompareSavedPort(SavedDevice device,
        ServicePort port)
    {
        int? recorded = port.Kind switch
        {
            ServiceKind.Http => device.UseTls ? null : device.HttpPort,
            ServiceKind.Https => device.UseTls ? device.HttpPort : null,
            ServiceKind.Rtsp => device.RtspPort,
            ServiceKind.Sdk => VendorPorts.HasSdkPort(device.VendorKind) ? device.SdkPort : null,
            _ => null,
        };
        if (recorded is null)
            return ("", "DVRTool stores no port of its own for this protocol.");
        if (recorded == port.Port)
            return ($"matches ({recorded})",
                "The port this record dials is the port the device is listening on.");

        // Not necessarily wrong: a forwarded port legitimately differs from the device's own.
        // Saying which is which is the useful part.
        return ($"record says {recorded}",
            $"This record connects on {recorded} while the device's own {port.Protocol} port " +
            $"is {port.Port}. That is normal behind a port forward, and a mistake when there " +
            "is no forward.");
    }

    private static string DescribeNetwork(DeviceConfiguration doc)
    {
        if (!doc.Scope.Network)
            return $"Network: n/a — {doc.Scope.Reason}";
        if (doc.Interfaces is null)
            return "Network: ?  (this recorder would not answer its addressing)";

        var text = new StringBuilder();
        foreach (var nic in doc.Interfaces)
        {
            bool configured = nic.IpAddress.Length > 0 && nic.IpAddress != "0.0.0.0";
            text.AppendLine($"{nic.Name}{(nic.IsDefault ? "  (default)" : "")}" +
                (configured ? "" : "  (no address)"));
            if (configured)
                text.AppendLine($"  {nic.AddressingText}  {nic.IpAddress}" +
                    (nic.SubnetMask.Length > 0 && nic.SubnetMask != "0.0.0.0"
                        ? $" / {nic.SubnetMask}" : "") +
                    (nic.Gateway.Length > 0 && nic.Gateway != "0.0.0.0"
                        ? $"  gw {nic.Gateway}" : ""));
            if (nic.Dns.Count > 0)
                text.AppendLine($"  DNS {string.Join(", ", nic.Dns)}" +
                    (nic.DnsAuto is true ? "  (automatic)" : ""));
            var bits = new List<string>();
            if (nic.MacAddress.Length > 0)
                bits.Add(nic.MacAddress);
            if (nic.Mtu is int mtu)
                bits.Add($"MTU {mtu}" + (nic.MtuRange is { } r ? $" of {r.Text}" : ""));
            if (nic.LinkSpeedMbps is int speed)
                bits.Add($"{speed} Mbps");
            if (nic.LinkUp is bool up)
                bits.Add(up ? "link up" : "link down");
            if (bits.Count > 0)
                text.AppendLine($"  {string.Join("   ", bits)}");
        }
        return text.ToString().TrimEnd();
    }

    private static string DescribeNotes(DeviceConfiguration doc)
    {
        var text = new StringBuilder();
        foreach (var group in doc.Notes.GroupBy(n => n.Group))
        {
            text.AppendLine(group.Key);
            foreach (var note in group)
                text.AppendLine($"  {note.Label}: {note.Value}");
        }
        if (doc.Failures.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Parts this recorder would not answer (shown as “?” above)");
            foreach (var failure in doc.Failures)
                text.AppendLine($"  {failure.Label}: {failure.Value}");
        }
        return text.ToString().TrimEnd();
    }

    private void ShowConfigWarning(string text)
    {
        ConfigWarning.Text = text;
        ConfigWarning.Visibility = Visibility.Visible;
    }

    // ----- writes -----

    private async Task ApplyConfigAsync(CancellationToken ct)
    {
        if (_configDoc is not { } before || _configDevice is not { } device ||
            _configClient is not IDeviceConfigWriter writer)
        {
            SetStatus("Read a system first — a config write is its own document with fields " +
                "replaced.");
            return;
        }

        // Only what the operator actually changed. Writing a field back at its current value
        // would be a no-op the recorder still has to accept, and would make a rejection
        // impossible to attribute.
        string? server = Changed(ConfigNtpServerBox.Text,
            before.NtpServers?.FirstOrDefault()?.Address);
        int? port = ChangedInt(ConfigNtpPortBox.Text,
            before.NtpServers?.FirstOrDefault()?.Port, out bool portBad);
        int? interval = ChangedInt(ConfigNtpIntervalBox.Text,
            before.NtpInterval is TimeSpan held ? (int)held.TotalMinutes : null,
            out bool intervalBad);
        if (portBad || intervalBad)
        {
            ShowConfigWarning("The NTP port and interval must be whole positive numbers " +
                "(the interval is in minutes).");
            return;
        }
        bool? ntpEnabled = ConfigNtpEnabledCheck.IsEnabled &&
            ConfigNtpEnabledCheck.IsChecked != before.NtpEnabled
            ? ConfigNtpEnabledCheck.IsChecked == true
            : null;
        string? zone = ConfigZoneBox.IsEnabled
            ? Changed(ConfigZoneBox.Text, before.Clock.VendorZoneLabel)
            : null;
        bool? dst = ConfigDstCheck.IsEnabled &&
            ConfigDstCheck.IsChecked != before.Clock.DstEnabled
            ? ConfigDstCheck.IsChecked == true
            : null;
        string? name = Changed(ConfigNameBox.Text, before.DeviceName);

        var asked = new List<string>();
        if (server is not null)
            asked.Add($"NTP server → {server}");
        if (port is int p)
            asked.Add($"NTP port → {p}");
        if (interval is int m)
            asked.Add($"NTP interval → {m} min");
        if (ntpEnabled is bool on)
            asked.Add($"NTP → {(on ? "on" : "off")}");
        if (zone is not null)
            asked.Add($"time zone → {zone}");
        if (dst is bool wantDst)
            asked.Add($"DST → {(wantDst ? "on" : "off")}");
        if (name is not null)
            asked.Add($"device name → {name}");
        if (asked.Count == 0)
        {
            ConfigWriteResult.Text = "Nothing changed in the boxes above.";
            return;
        }

        if (MessageBox.Show(
                $"Write to {device.Name}:\n\n  " + string.Join("\n  ", asked) +
                "\n\nEach change is read back and reported as what the recorder now holds.",
                "Change device settings", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
            return;

        var results = new List<string>();
        try
        {
            if (server is not null || port is not null || interval is not null ||
                ntpEnabled is not null)
                results.Add(Describe(await writer.SetNtpAsync(
                    new NtpSettings(server, port,
                        interval is int minutes ? TimeSpan.FromMinutes(minutes) : null,
                        ntpEnabled), ct)));
            if (zone is not null || dst is not null)
                results.Add(Describe(await writer.SetTimeAsync(new TimeSettings(zone, dst), ct)));
            if (name is not null)
                results.Add(Describe(await writer.SetDeviceNameAsync(name, ct)));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ShowConfigWarning($"The write failed: {Shorten(ex.Message)}");
            SetStatus($"{device.Name}: nothing was written.");
            return;
        }

        ConfigWriteResult.Text = string.Join("   ", results);
        SetStatus($"{device.Name}: {string.Join("; ", results)}");

        // Re-read so the view — and the writer's own document — describe the recorder as it
        // is now, not as it was before the write.
        await LoadConfigAsync(ct);
    }

    private async Task SyncConfigClockAsync(CancellationToken ct)
    {
        if (_configDevice is not { } device || _configClient is not IDeviceConfigWriter writer)
        {
            SetStatus("Read a system first.");
            return;
        }

        if (MessageBox.Show(
                $"Set {device.Name}'s clock to this workstation's " +
                $"({DateTime.Now:yyyy-MM-dd HH:mm:ss})?\n\n" +
                "This is refused on a recorder that syncs from NTP: the next sync would " +
                "overwrite it and the real cause — usually the zone or the DST switch — " +
                "would stay hidden.",
                "Set the recorder's clock", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
            return;

        try
        {
            var change = await writer.SyncTimeNowAsync(ct);
            ConfigWriteResult.Text = Describe(change);
            SetStatus($"{device.Name}: {Describe(change)}");
            if (change.Changed)
                await LoadConfigAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowConfigWarning($"The clock write failed: {Shorten(ex.Message)}");
        }
    }

    /// <summary>What the recorder now holds — never what it was asked for.</summary>
    private static string Describe(ConfigChange change) => change.Rejected
        ? $"{change.Field}: REJECTED" +
          (change.Note.Length > 0 ? $" ({change.Note})" : "")
        : change.Changed
            ? $"{change.Field}: changed"
            : $"{change.Field}: unchanged" +
              (change.Note.Length > 0 ? $" ({change.Note})" : "");

    /// <summary>The box's value, or null when it still says what the recorder said.</summary>
    private static string? Changed(string text, string? current)
    {
        string value = text.Trim();
        if (value.Length == 0)
            return null;
        return string.Equals(value, (current ?? "").Trim(), StringComparison.Ordinal)
            ? null
            : value;
    }

    private static int? ChangedInt(string text, int? current, out bool invalid)
    {
        invalid = false;
        string value = text.Trim();
        if (value.Length == 0)
            return null;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ||
            parsed <= 0)
        {
            invalid = true;
            return null;
        }
        return parsed == current ? null : parsed;
    }
}
