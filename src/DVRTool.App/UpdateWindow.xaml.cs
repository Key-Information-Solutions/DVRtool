using System.IO;
using System.Net.Http;
using System.Windows;
using DVRTool.Core.Updates;

namespace DVRTool.App;

/// <summary>
/// Downloads one release's installer with progress, verifies it against the signed manifest,
/// and — only on the operator's click — hands it to msiexec. <c>DialogResult</c> true means
/// the installer was launched and the main window must now close.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateCheck.Available _update;
    private readonly string? _relaunchExe;
    private readonly CancellationTokenSource _cts = new();
    private string? _verifiedPath;
    private bool _launched;

    public UpdateWindow(UpdateCheck.Available update, string? relaunchExe)
    {
        InitializeComponent();
        _update = update;
        _relaunchExe = relaunchExe;
        Headline.Text = $"DVRTool {ProductVersion.Format(update.Manifest.Version)}";
        Detail.Text = $"Replacing {ProductVersion.Display}. {update.Manifest.FileName}, " +
                      $"{update.Manifest.Size / (1024.0 * 1024):F1} MB, from github.com.";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        long total = _update.Manifest.Size;
        var progress = new Progress<long>(done =>
        {
            Progress.Value = done * 100.0 / Math.Max(total, 1);
            ProgressText.Text = $"{done / (1024.0 * 1024):F1} / {total / (1024.0 * 1024):F1} MB";
        });
        try
        {
            using var client = new UpdateClient(ProductVersion.Current);
            ProgressText.Text = "Connecting…";
            _verifiedPath = await client.DownloadAsync(_update, progress, _cts.Token);
            Progress.Value = 100;
            ProgressText.Text = "Downloaded and verified (size + SHA-256).";
            Ready.Visibility = Visibility.Visible;
            InstallButton.IsEnabled = true;
            InstallButton.Focus();
        }
        catch (OperationCanceledException)
        {
            // Cancel button: the window is already closing.
        }
        catch (UpdateVerificationException ex)
        {
            ProgressText.Text = "REJECTED: " + ex.Message;
            ProgressText.Foreground = System.Windows.Media.Brushes.DarkRed;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            ProgressText.Text = "Download failed: " + ex.Message;
            ProgressText.Foreground = System.Windows.Media.Brushes.DarkRed;
        }
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_verifiedPath is null)
            return;
        try
        {
            UpdateInstaller.Launch(_verifiedPath, _relaunchExe);
            _launched = true;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            ProgressText.Text = "Could not start the installer: " + ex.Message;
            ProgressText.Foreground = System.Windows.Media.Brushes.DarkRed;
        }
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_launched)
            _cts.Cancel();
    }
}
