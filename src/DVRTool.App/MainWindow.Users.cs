using System.Windows;
using DVRTool.Core;

namespace DVRTool.App;

/// <summary>
/// The Users tab's one write: creating a login account across the selected recorders.
/// </summary>
/// <remarks>
/// The shape is the Access tab's revoke (<see cref="MainWindow.RevokeCardAsync"/>): re-read the
/// fleet, plan from that read, confirm against a dialog that names every recorder and defaults
/// to No, write, read each account back, then re-load the grid so it shows the result rather
/// than the intention.
/// <para>
/// Add-only, and deliberately so. A create cannot lock anyone out of a customer recorder;
/// delete and password-change can, and they are not here. The corollary is that there is no
/// undo from this tab — an account created here is removed on the recorder itself.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// The recorders the loaded account matrix describes. Held so the write plans against the
    /// same fleet the grid is showing, and so the button can say why it is disabled.
    /// </summary>
    private List<SavedDevice> _usersLoaded = [];

    /// <summary>
    /// Enables the write button for the fleet that has just been read. Writable means Hikvision:
    /// the others implement the read interface only, and a button that offers a write the vendor
    /// cannot do is worse than no button.
    /// </summary>
    private void UpdateAddUserButton(IReadOnlyList<SavedDevice> loaded)
    {
        _usersLoaded = loaded.ToList();

        var unwritable = _usersLoaded.Where(d => d.VendorKind != Vendor.Hikvision).ToList();
        UsersAddButton.IsEnabled = !UsersAccessMode && _usersLoaded.Count > 0 && unwritable.Count == 0;

        if (UsersAddButton.IsEnabled || _usersLoaded.Count == 0)
            return;

        // Say which recorder is in the way rather than leaving a dead button.
        UsersAddButton.ToolTip =
            "Creating accounts isn't implemented for " +
            string.Join(", ", unwritable.Select(d => $"{d.Name} ({d.VendorKind})")) +
            ". Only Hikvision recorders can be written today.";
    }

    private async void OnAddUser(object sender, RoutedEventArgs e)
    {
        if (_cleanupStarted)
            return; // window is closing; don't open clients OnClosing will not see

        var targets = _usersLoaded;
        if (targets.Count == 0)
        {
            SetStatus("Load the accounts first: an add is planned against a fresh read.");
            return;
        }

        var dialog = new AddUserWindow(targets.Select(d => d.Name).ToList()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } wanted)
        {
            SetStatus("Add user canceled — nothing was written.");
            return;
        }

        _usersCts?.Cancel();
        _usersCts?.Dispose();
        _usersCts = new CancellationTokenSource();
        var task = AddUserAsync(targets, wanted, _usersCts.Token);
        _usersTask = task;
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus($"Add user failed: {Shorten(ex.Message)}"); }
        finally
        {
            if (ReferenceEquals(task, _usersTask))
                _usersTask = null;
        }
    }

    private async Task AddUserAsync(IReadOnlyList<SavedDevice> devices, NewUser wanted,
        CancellationToken ct)
    {
        int gen = ++_usersGen;

        // Plan off a fresh read, never off the grid on screen: an account added from another
        // client since the last Load would otherwise be planned as a create and refused by the
        // recorder — or worse, the operator would be told it was missing when it is not.
        SetStatus($"Re-reading {devices.Count} recorder(s) before writing …");
        var results = await Task.WhenAll(devices.Select(d => ReadDeviceUsersAsync(d, ct)));
        if (gen != _usersGen)
            return;

        var plan = UserAddPlan.For(results, wanted.Name, wanted.Role);
        if (!plan.HasWork)
        {
            // Render first, then speak: ShowFleetMatrix sets its own summary status, so a
            // message set before it is overwritten and the operator is told nothing.
            await ShowAccountMatrixAsync(results, gen);
            SetStatus(plan.AlreadyPresent.Count > 0
                ? $"'{plan.Name}' is already on every recorder that answered " +
                  $"({string.Join(", ", plan.AlreadyPresent)}) — nothing was written."
                : "No recorder could be read — nothing was written.");
            return;
        }

        if (_cleanupStarted)
            return;

        // The dialog is the GUI's --force.
        var answer = MessageBox.Show(this, DescribeAdd(plan), "DVRTool — add user",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            await ShowAccountMatrixAsync(results, gen);
            SetStatus("Add user canceled — nothing was written.");
            return;
        }

        SetStatus($"Creating '{plan.Name}' on {plan.Creates.Count} recorder(s) …");
        var byName = devices.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var outcomes = new List<(string Device, bool Ok, string Message)>();
        foreach (string deviceName in plan.Creates)
        {
            outcomes.Add(await CreateOneAsync(byName[deviceName],
                new NewUser(plan.Name, wanted.Password, wanted.Role), ct));
            if (gen != _usersGen)
                return;
        }

        // Re-read so the grid shows the result rather than the intention.
        var after = await Task.WhenAll(devices.Select(d => ReadDeviceUsersAsync(d, ct)));
        if (gen != _usersGen)
            return;
        await ShowAccountMatrixAsync(after, gen);
        ReportAdd(plan, outcomes);
    }

    /// <summary>One recorder's leg: fresh client, strict identity gate, create, read back.</summary>
    private async Task<(string Device, bool Ok, string Message)> CreateOneAsync(
        SavedDevice device, NewUser user, CancellationToken ct)
    {
        INvrClient? client = null;
        try
        {
            client = device.CreateClient();
            if (client is not IUserAdminClient admin)
                return (device.Name, false, "this device cannot create accounts");

            // A write gets the strict gate. This is a second login: between the read above and
            // this one, the address could be answering elsewhere.
            DeviceIdentityGuard.Ensure(await DeviceIdentityGuard.CheckAsync(client,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name,
                ct: ct));

            var created = await admin.CreateUserAsync(user, ct);

            // The read-back is the report: the id is the device's, and so is the level.
            return created.Role == user.Role
                ? (device.Name, true, $"created — id {created.Id}, {created.NativeLevel}")
                : (device.Name, true,
                    $"created, but the recorder kept {created.NativeLevel} (asked {user.Role})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (device.Name, false, Shorten(ex.Message));
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>Re-renders the grid from reads that have already happened.</summary>
    private Task ShowAccountMatrixAsync(IReadOnlyList<DeviceUsersResult> results, int gen)
    {
        if (gen != _usersGen)
            return Task.CompletedTask;
        ShowAccountMatrix(results);
        return Task.CompletedTask;
    }

    private static string DescribeAdd(UserAddPlan plan)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"Create the account '{plan.Name}' at level {plan.Role} on:");
        text.AppendLine();
        foreach (string device in plan.Creates)
            text.AppendLine($"  {device}");

        text.AppendLine();
        text.AppendLine("Each write is read back and reported.");
        text.AppendLine(
            "There is no undo here: removing an account is done on the recorder itself.");

        if (plan.AlreadyPresent.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Already has the account and will be left alone:");
            foreach (string device in plan.AlreadyPresent)
                text.AppendLine($"  {device}");
        }

        if (plan.IsPartial)
        {
            text.AppendLine();
            text.AppendLine(
                $"PARTIAL — {plan.Unreadable.Count} recorder(s) could not be read and will not " +
                "be written. This account will not exist on them:");
            foreach (var device in plan.Unreadable)
                text.AppendLine($"  {device.DeviceName} — {device.Error}");
        }

        text.AppendLine();
        text.Append("Create it?");
        return text.ToString();
    }

    private void ReportAdd(UserAddPlan plan,
        IReadOnlyList<(string Device, bool Ok, string Message)> outcomes)
    {
        int ok = outcomes.Count(o => o.Ok);
        var failed = outcomes.Where(o => !o.Ok).ToList();

        SetStatus($"'{plan.Name}': created on {ok} of {outcomes.Count} recorder(s).");

        if (failed.Count == 0 && !plan.IsPartial)
        {
            UsersWarning.Text = "";
            UsersWarning.Visibility = Visibility.Collapsed;
            return;
        }

        var lines = new List<string> { "ADD INCOMPLETE —" };
        foreach (var (device, _, message) in failed)
            lines.Add($"  {device}: NOT CREATED — {message}");
        if (plan.IsPartial)
            lines.Add(
                $"  {plan.Unreadable.Count} recorder(s) could not be read, so '{plan.Name}' " +
                "does not exist on them.");

        UsersWarning.Text = string.Join("\n", lines);
        UsersWarning.Visibility = Visibility.Visible;
    }
}
