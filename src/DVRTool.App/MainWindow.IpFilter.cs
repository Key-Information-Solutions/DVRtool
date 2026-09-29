using System.Text;
using System.Windows;
using System.Windows.Controls;
using DVRTool.Core;

namespace DVRTool.App;

/// <summary>
/// The IP filter tab: which recorders have a blocklist, what is on it, and blocking or
/// unblocking addresses across several at once.
/// </summary>
/// <remarks>
/// <para>
/// Most recorders do not show this page in their web UI at all, which is why the tab opens on
/// a fleet read: the first sweep (2026-09-29) found the filter on 8 of 17 Hikvision recorders,
/// in use on one. The <c>dvrtool ipfilter</c> CLI is the automation surface; this tab is the
/// same plan with a confirmation in front of it.
/// </para>
/// <para>
/// The writes follow <see cref="IpFilterPlan"/>'s rules exactly — blocklists only, never the
/// mode, never this workstation, the recorder, its gateway or a live session, and a plan with
/// one refusal writes nothing. The one thing the GUI adds is that each write re-reads the
/// recorder through the client that will write it, immediately before the plan is drawn up,
/// so the dialog describes the list as it is now and not as it was at the last sweep.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private int _ipFilterGen;
    private CancellationTokenSource? _ipFilterCts;
    private Task? _ipFilterTask;

    /// <summary>
    /// The client that read each recorder, kept for the life of the view: a write must be
    /// planned from that client's own read, like the Config tab's.
    /// </summary>
    private readonly Dictionary<string, (SavedDevice Device, INvrClient Client)> _ipFilterClients =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record IpFilterFleetRow(string Device, string Filter, string Verdict,
        string Detail, IpFilterAuditRow Row);

    private sealed record IpFilterEntryRow(int Id, string Address, string AlsoOn);

    private IpFilterAudit? _ipFilterAudit;

    private async void OnReadIpFilters(object sender, RoutedEventArgs e) =>
        await RunIpFilterWorkAsync(ReadIpFiltersAsync);

    private async void OnIpFilterBlock(object sender, RoutedEventArgs e) =>
        await RunIpFilterWorkAsync(ct => ChangeIpFiltersAsync("block", ct));

    private async void OnIpFilterUnblock(object sender, RoutedEventArgs e) =>
        await RunIpFilterWorkAsync(ct => ChangeIpFiltersAsync("unblock", ct));

    private async void OnIpFilterTurnOn(object sender, RoutedEventArgs e) =>
        await RunIpFilterWorkAsync(ct => ChangeIpFiltersAsync("on", ct));

    private async void OnIpFilterTurnOff(object sender, RoutedEventArgs e) =>
        await RunIpFilterWorkAsync(ct => ChangeIpFiltersAsync("off", ct));

    private async Task RunIpFilterWorkAsync(Func<CancellationToken, Task> work)
    {
        if (_cleanupStarted)
            return;

        _ipFilterCts?.Cancel();
        _ipFilterCts?.Dispose();
        _ipFilterCts = new CancellationTokenSource();
        var task = work(_ipFilterCts.Token);
        _ipFilterTask = task;
        try { await task; }
        catch { /* already reported by the work itself */ }
        finally
        {
            if (ReferenceEquals(task, _ipFilterTask))
                _ipFilterTask = null;
        }
    }

    private void DisposeIpFilterClients()
    {
        foreach (var (_, client) in _ipFilterClients.Values)
            client.Dispose();
        _ipFilterClients.Clear();
    }

    // ----- the fleet read -----

    private async Task ReadIpFiltersAsync(CancellationToken ct)
    {
        int gen = ++_ipFilterGen;
        var recorders = _devices.Where(d => !d.IsPanel).ToList();
        if (recorders.Count == 0)
        {
            SetStatus("No recorders saved yet — Add Device….");
            return;
        }

        IpFilterWarning.Visibility = Visibility.Collapsed;
        IpFilterResult.Text = "";
        SetStatus($"Reading the IP filter of {recorders.Count} recorder(s) …");
        DisposeIpFilterClients();

        var reads = await Task.WhenAll(recorders.Select(d => ReadOneIpFilterAsync(d, ct)));
        if (gen != _ipFilterGen)
        {
            foreach (var (_, client) in reads)
                client?.Dispose();
            return;
        }

        foreach (var (row, client) in reads)
        {
            var device = recorders.First(d => d.Name == row.DeviceName);
            if (client is not null)
                _ipFilterClients[device.Name] = (device, client);
        }

        ShowIpFilterAudit(IpFilterAudit.Build(reads.Select(r => r.Row)));
    }

    private void ShowIpFilterAudit(IpFilterAudit audit)
    {
        _ipFilterAudit = audit;
        var selected = SelectedIpFilterDevices().ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Recorders with a filter first: on most fleets they are the minority, and the list is
        // what the operator came to act on.
        var rows = audit.Rows
            .OrderByDescending(r => r.Ok && r.State!.Supported)
            .ThenBy(r => r.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(r => new IpFilterFleetRow(r.DeviceName, r.Summary, r.Verdict, IpFilterDetail(r), r))
            .ToList();
        IpFilterFleetGrid.ItemsSource = rows;
        foreach (var row in rows.Where(r => selected.Contains(r.Device)))
            IpFilterFleetGrid.SelectedItems.Add(row);

        IpFilterSummary.Text = audit.Summary;
        if (audit.IsPartial)
            ShowIpFilterWarning("Some recorders could not be read — their filters are unknown, not empty.");
        ShowSelectedIpFilterEntries();
        SetStatus(audit.Summary);
    }

    private static string IpFilterDetail(IpFilterAuditRow row)
    {
        if (!row.Ok)
            return row.Error ?? "";
        var s = row.State!;
        var text = new StringBuilder();
        text.AppendLine(s.Summary);
        if (s.Supported)
        {
            if (s.DeclaredModes.Count > 0)
                text.AppendLine($"Modes the firmware offers: {string.Join(", ", s.DeclaredModes)}");
            text.AppendLine($"IPv6 entries: {(s.AcceptsIpv6 ? "yes" : "no")}");
            if (s.OwnAddresses.Count > 0)
                text.AppendLine($"Never blocked (the recorder and its gateway): {string.Join(", ", s.OwnAddresses)}");
            foreach (var session in s.Sessions)
                text.AppendLine($"Logged in now: {session.User} from {session.Address}" +
                    (session.LoginTime.Length > 0 ? $" since {session.LoginTime}" : ""));
            foreach (var note in s.Failures)
                text.AppendLine($"Could not read {note.Label}: {note.Value}");
        }
        return text.ToString().TrimEnd();
    }

    private async Task<(IpFilterAuditRow Row, INvrClient? Client)> ReadOneIpFilterAsync(
        SavedDevice device, CancellationToken ct)
    {
        INvrClient? client = null;
        try
        {
            client = SavedDeviceClients.CreateClient(device);
            if (client is not IIpFilterClient filter)
            {
                client.Dispose();
                return (IpFilterAuditRow.Unimplemented(device.Name, device.VendorKind.ToString()), null);
            }

            var check = await DeviceIdentityGuard.CheckAsync(client,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name, ct: ct);
            if (check.Verdict == IdentityVerdict.Mismatch)
            {
                client.Dispose();
                return (IpFilterAuditRow.Failed(device.Name, $"WRONG DEVICE — {check.Message}"), null);
            }

            var state = await filter.GetIpFilterAsync(ct);
            return (new IpFilterAuditRow { DeviceName = device.Name, State = state }, client);
        }
        catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
        {
            client?.Dispose();
            return (IpFilterAuditRow.Failed(device.Name, Shorten(ex.Message)), null);
        }
    }

    // ----- selection -----

    private IEnumerable<string> SelectedIpFilterDevices() =>
        IpFilterFleetGrid.SelectedItems.OfType<IpFilterFleetRow>().Select(r => r.Device);

    private void OnIpFilterFleetSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowSelectedIpFilterEntries();

    /// <summary>The list of the one recorder selected; several selected shows none.</summary>
    private void ShowSelectedIpFilterEntries()
    {
        var picked = IpFilterFleetGrid.SelectedItems.OfType<IpFilterFleetRow>().ToList();
        if (picked.Count != 1 || picked[0].Row is not { Ok: true, State: { Supported: true } state })
        {
            IpFilterEntryGrid.ItemsSource = null;
            IpFilterEntriesHeader.Text = picked.Count > 1
                ? $"{picked.Count} recorders selected — Block / Turn on / Turn off apply to all of them."
                : "Pick a recorder with a filter to see its list.";
            return;
        }

        var audit = _ipFilterAudit;
        IpFilterEntryGrid.ItemsSource = state.Entries.Select(e => new IpFilterEntryRow(e.Id, e.Address,
            string.Join(", ", audit?.Addresses
                .FirstOrDefault(a => IpFilterAddress.Same(a.Address, e.Address)).Devices?
                .Where(d => !d.Equals(picked[0].Device, StringComparison.OrdinalIgnoreCase)) ?? [])))
            .ToList();
        IpFilterEntriesHeader.Text = $"{picked[0].Device}: {state.Summary}" +
            (!state.Enabled && state.Entries.Count > 0 ? " — listed, NOT blocked while the filter is off" : "");
    }

    private void ShowIpFilterWarning(string text)
    {
        IpFilterWarning.Text = text;
        IpFilterWarning.Visibility = Visibility.Visible;
    }

    // ----- writes -----

    private async Task ChangeIpFiltersAsync(string verb, CancellationToken ct)
    {
        IpFilterWarning.Visibility = Visibility.Collapsed;
        var devices = SelectedIpFilterDevices().ToList();
        if (devices.Count == 0)
        {
            ShowIpFilterWarning("Select one or more recorders in the list first (Read fleet if it is empty).");
            return;
        }

        var typed = IpFilterAddressBox.Text
            .Split([',', ' ', '\t', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .ToList();
        var addresses = verb switch
        {
            "block" => typed,
            // Unblock takes the highlighted entries of the recorder shown, plus anything typed.
            "unblock" => IpFilterEntryGrid.SelectedItems.OfType<IpFilterEntryRow>()
                .Select(r => r.Address).Concat(typed).ToList(),
            _ => [],
        };
        if (verb is "block" or "unblock" && addresses.Count == 0)
        {
            ShowIpFilterWarning(verb == "block"
                ? "Type the addresses to block (one per line, or comma-separated)."
                : "Highlight addresses in the list, or type them, to unblock.");
            return;
        }

        var protect = IpFilterProtectBox.Text
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (protect.FirstOrDefault(p => IpFilterAddress.Parse(p) is null) is string bad)
        {
            ShowIpFilterWarning($"'{bad}' in Never block is not an IP address.");
            return;
        }

        var request = new IpFilterRequest(
            Add: verb == "block" ? addresses : [],
            Remove: verb == "unblock" ? addresses : [],
            Enable: verb switch
            {
                "on" => true,
                "off" => false,
                "block" when IpFilterEnableCheck.IsChecked == true => true,
                _ => null,
            },
            AllowLocal: IpFilterAllowLanCheck.IsChecked == true,
            // A live session is the CLI's --allow-logged-in, deliberately not a checkbox: in
            // the GUI it is refused, and the refusal names the account.
            AllowLoggedIn: false,
            Protected: protect.Concat(IpFilterAddress.ThisWorkstation()).ToList());

        // Re-read each recorder through the client that will write it, and plan from that.
        SetStatus($"Re-reading {devices.Count} recorder(s) before planning …");
        var plans = new List<(SavedDevice Device, IIpFilterWriter Writer, IpFilterPlan Plan)>();
        var lines = new StringBuilder();
        bool anyRefused = false;
        foreach (string name in devices)
        {
            if (!_ipFilterClients.TryGetValue(name, out var entry) ||
                entry.Client is not IIpFilterClient reader || entry.Client is not IIpFilterWriter writer)
            {
                lines.AppendLine($"{name}: skipped — not read, or not writable.");
                continue;
            }
            IpFilterPlan plan;
            try
            {
                plan = IpFilterPlan.For(await reader.GetIpFilterAsync(ct), request);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
            {
                lines.AppendLine($"{name}: could not be re-read — {Shorten(ex.Message)}");
                anyRefused = true;
                continue;
            }

            lines.AppendLine(DescribeIpFilterPlan(name, plan));
            if (!plan.Before.Supported)
                continue;
            if (!plan.Allowed)
                anyRefused = true;
            else if (plan.HasWork)
                plans.Add((entry.Device, writer, plan));
        }

        if (plans.Count == 0)
        {
            IpFilterResult.Text = lines.ToString().TrimEnd();
            ShowIpFilterWarning(anyRefused
                ? "Nothing was written: something in the request was refused (details below)."
                : "Nothing to change.");
            SetStatus("IP filter: nothing written.");
            return;
        }

        string prompt = lines.ToString().TrimEnd() +
            (anyRefused ? "\n\nRecorders marked REFUSED will not be written; the rest will." : "") +
            "\n\nEach recorder is read back afterwards and reported as what it now holds. Write?";
        if (MessageBox.Show(this, prompt, "Change IP filters", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            SetStatus("IP filter: nothing written.");
            return;
        }

        var results = new StringBuilder();
        foreach (var (device, writer, plan) in plans)
        {
            try
            {
                var change = await writer.ApplyIpFilterAsync(plan, ct);
                results.AppendLine($"{device.Name}: {DescribeIpFilterChange(change)}");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
            {
                results.AppendLine($"{device.Name}: FAILED — {Shorten(ex.Message)}");
            }
        }

        // The grid from what each client now holds — the read-back, not the request.
        var rows = new List<IpFilterAuditRow>();
        foreach (var row in _ipFilterAudit?.Rows ?? [])
        {
            if (plans.Any(p => p.Device.Name == row.DeviceName) &&
                _ipFilterClients.TryGetValue(row.DeviceName, out var entry) &&
                entry.Client is IIpFilterClient reader)
            {
                try
                {
                    rows.Add(new IpFilterAuditRow { DeviceName = row.DeviceName, State = await reader.GetIpFilterAsync(ct) });
                    continue;
                }
                catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
                {
                    rows.Add(IpFilterAuditRow.Failed(row.DeviceName, Shorten(ex.Message)));
                    continue;
                }
            }
            rows.Add(row);
        }

        // Render first, then speak: the repaint sets its own status.
        ShowIpFilterAudit(IpFilterAudit.Build(rows));
        IpFilterResult.Text = results.ToString().TrimEnd();
        if (results.ToString().Contains("REJECTED") || results.ToString().Contains("FAILED"))
            ShowIpFilterWarning("At least one recorder did not take the change — see below.");
        SetStatus("IP filter: " + results.ToString().Replace(Environment.NewLine, "  ").Trim());
    }

    private static string DescribeIpFilterPlan(string name, IpFilterPlan plan)
    {
        var text = new StringBuilder($"{name} ({plan.Before.Summary}):");
        if (!plan.Before.Supported)
            return text.Append(" skipped — no IP filter on this firmware.").ToString();
        if (plan.Blocked is not null)
            return text.Append($" REFUSED — {plan.Blocked}").ToString();
        foreach (var d in plan.Decisions)
            text.Append("\n   " + d.Action switch
            {
                IpFilterAction.Add => $"block {d.Address}",
                IpFilterAction.Remove => $"unblock {d.Address}",
                IpFilterAction.AlreadyPresent => $"{d.Address} already blocked",
                IpFilterAction.NotPresent => $"{d.Address} not listed",
                _ => $"REFUSED {d.Asked} — {d.Reason}",
            });
        if (plan.EnabledAfter != plan.Before.Enabled)
            text.Append($"\n   turn the filter {(plan.EnabledAfter ? "ON" : "OFF")}");
        if (plan.Allowed && plan.InertAfter && plan.Adds.Any())
            text.Append("\n   the filter stays OFF — these will be listed, NOT blocked");
        if (!plan.Allowed)
            text.Append("\n   → nothing will be written to this recorder");
        else if (!plan.HasWork)
            text.Append("\n   nothing to change");
        return text.ToString();
    }

    private static string DescribeIpFilterChange(IpFilterChange change)
    {
        var parts = new List<string>();
        if (change.Added.Any())
            parts.Add("blocked " + string.Join(", ", change.Added));
        if (change.Removed.Any())
            parts.Add("unblocked " + string.Join(", ", change.Removed));
        if (change.Before.Enabled != change.After.Enabled)
            parts.Add($"filter {(change.After.Enabled ? "on" : "off")}");
        string text = (parts.Count > 0 ? string.Join("; ", parts) : "unchanged") +
            $" — now {change.After.Summary}";
        return change.Rejected ? $"REJECTED ({change.Note}) — {text}" : text;
    }
}
