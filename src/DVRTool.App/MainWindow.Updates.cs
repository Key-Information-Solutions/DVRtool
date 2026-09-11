using System.Diagnostics;
using System.Windows;
using DVRTool.Core.Updates;

namespace DVRTool.App;

/// <summary>
/// The GUI face of the updater: a quiet daily check, a banner when a signed newer release
/// exists, and the About box with a manual check. Nothing downloads or installs without a
/// click; a machine installed with <c>UPDATES=0</c> shows none of this.
/// </summary>
public partial class MainWindow
{
    private UpdateCheck.Available? _availableUpdate;
    private CancellationTokenSource? _updateCts;
    private DateTimeOffset? _lastUpdateCheck;

    private void StartAutomaticUpdateCheck()
    {
        var settings = UpdateSettings.Load();
        _lastUpdateCheck = settings.LastCheck;
        if (!settings.IsCheckDue(DateTimeOffset.Now))
            return;
        if (UpdatePolicy.Read().DisabledReason is not null)
            return;
        _ = RunUpdateCheckAsync(manual: false);
    }

    /// <summary>Runs one check; on a manual check every outcome reaches the status bar.</summary>
    private async Task RunUpdateCheckAsync(bool manual)
    {
        _updateCts?.Cancel();
        var cts = _updateCts = new CancellationTokenSource();
        var policy = UpdatePolicy.Read();
        var settings = UpdateSettings.Load();

        UpdateCheck result;
        if (policy.DisabledReason is { } reason)
        {
            result = new UpdateCheck.Disabled(reason);
        }
        else
        {
            if (manual)
                SetStatus("Checking for updates…");
            try
            {
                using var client = new UpdateClient(ProductVersion.Current);
                result = await client.CheckAsync(settings.SkippedVersion, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (cts.IsCancellationRequested || _closePending)
                return;
            settings.LastCheck = DateTimeOffset.Now;
            _lastUpdateCheck = settings.LastCheck;
            if (result is UpdateCheck.Available a)
                settings.LastSeenVersionText = ProductVersion.Format(a.Manifest.Version);
            else if (result is UpdateCheck.UpToDate u)
                settings.LastSeenVersionText = ProductVersion.Format(u.Latest);
            settings.TrySave();
        }

        switch (result)
        {
            case UpdateCheck.Available available:
                _availableUpdate = available;
                UpdateBannerText.Text =
                    $"DVRTool {ProductVersion.Format(available.Manifest.Version)} is available " +
                    $"(this is {ProductVersion.Display}).";
                UpdateBanner.Visibility = Visibility.Visible;
                if (manual)
                    SetStatus($"Update available: DVRTool {ProductVersion.Format(available.Manifest.Version)}.");
                break;
            case UpdateCheck.Failed { SignatureRejected: true } bad:
                // The one failure that is never quiet: something on the channel is not ours.
                SetStatus(bad.Reason);
                if (manual)
                    MessageBox.Show(this, bad.Reason, "Update refused", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                break;
            case UpdateCheck.UpToDate when manual:
                SetStatus($"DVRTool {ProductVersion.Display} is up to date.");
                break;
            case UpdateCheck.Skipped skipped when manual:
                SetStatus($"DVRTool {ProductVersion.Format(skipped.Latest)} is available but skipped on this machine.");
                break;
            case UpdateCheck.Disabled disabled when manual:
                SetStatus(disabled.Reason);
                break;
            case UpdateCheck.Failed failed when manual:
                SetStatus($"Could not check for updates: {failed.Reason}");
                break;
        }
    }

    private void OnUpdateDismiss(object sender, RoutedEventArgs e) =>
        UpdateBanner.Visibility = Visibility.Collapsed;

    private void OnUpdateSkip(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
            return;
        var settings = UpdateSettings.Load();
        settings.SkippedVersionText = ProductVersion.Format(_availableUpdate.Manifest.Version);
        if (!settings.TrySave())
            SetStatus("Could not save the skipped version; the banner will return next time.");
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void OnUpdateNotes(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
            return;
        string url = _availableUpdate.Manifest.NotesUrl ?? _availableUpdate.ReleaseUrl.ToString();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    private void OnUpdateInstall(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
            return;
        var policy = UpdatePolicy.Read();
        var window = new UpdateWindow(_availableUpdate, policy.GuiExe) { Owner = this };
        bool? install = window.ShowDialog();
        if (install == true)
        {
            // msiexec is already running (UAC prompt up or progress bar showing). Exit before
            // the upgrade reaches its files-in-use check; OnClosing runs the normal cleanup.
            SetStatus("Installing update — DVRTool will close and reopen.");
            Close();
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var policy = UpdatePolicy.Read();
        string install = policy.IsInstalled
            ? policy.InstallDir ?? "?"
            : $"not an installed copy (running from {Environment.ProcessPath})";
        string last = _lastUpdateCheck is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never";
        string updates = policy.DisabledReason ?? "on (checked once a day; nothing installs without a click)";
        var result = MessageBox.Show(this,
            $"DVRTool {ProductVersion.Display}\nKey Information Solutions\n\n" +
            $"Installed at: {install}\n" +
            $"Updates: {updates}\n" +
            $"Last checked: {last}\n" +
            $"Release key: {UpdateSigning.KeyId}\n\n" +
            (policy.DisabledReason is null ? "Check for updates now?" : ""),
            "About DVRTool",
            policy.DisabledReason is null ? MessageBoxButton.YesNo : MessageBoxButton.OK,
            MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
            _ = RunUpdateCheckAsync(manual: true);
    }
}
