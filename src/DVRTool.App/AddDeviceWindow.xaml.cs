using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DVRTool.Core;

namespace DVRTool.App;

public partial class AddDeviceWindow : Window
{
    // Edit mode only: the DPAPI blob the device already carried. A blank PasswordBox
    // means "unchanged", so it is copied across verbatim — re-protecting "" would
    // silently wipe the credential. Null means add mode.
    private readonly string? _existingProtectedPassword;

    // Abandons in-flight probes when the dialog closes, so a dropped host's 5-second
    // timeouts are not still ticking after the operator has moved on.
    private readonly CancellationTokenSource _probes = new();

    public SavedDevice? Result { get; private set; }

    public AddDeviceWindow()
    {
        InitializeComponent();
    }

    public AddDeviceWindow(SavedDevice device)
    {
        InitializeComponent();
        Title = "Edit NVR";
        _existingProtectedPassword = device.ProtectedPassword;

        NameBox.Text = device.Name;
        VendorCombo.SelectedIndex = device.Vendor.Equals("dahua", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        HostBox.Text = device.Host;
        // TLS before the ports: checking the box runs OnTlsChecked, which rewrites an
        // HTTP port of 80 to 443. Prefilling the stored port afterwards lets it win.
        TlsCheck.IsChecked = device.UseTls;
        HttpPortBox.Text = device.HttpPort.ToString();
        RtspPortBox.Text = device.RtspPort.ToString();
        SdkPortBox.Text = device.SdkPort.ToString();
        UserBox.Text = device.Username;
        PasswordHint.Visibility = Visibility.Visible;
    }

    private SavedDevice? BuildDevice(out string? error)
    {
        error = null;
        string host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            error = "Host is required.";
            return null;
        }
        if (!TryPort(HttpPortBox.Text, out int httpPort) ||
            !TryPort(RtspPortBox.Text, out int rtspPort) ||
            !TryPort(SdkPortBox.Text, out int sdkPort))
        {
            error = "Ports must be numbers between 1 and 65535.";
            return null;
        }

        var device = new SavedDevice
        {
            Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : host,
            Vendor = VendorCombo.SelectedIndex == 1 ? "dahua" : "hikvision",
            Host = host,
            HttpPort = httpPort,
            RtspPort = rtspPort,
            SdkPort = sdkPort,
            UseTls = TlsCheck.IsChecked == true,
            Username = UserBox.Text.Trim(),
        };
        if (PassBox.Password.Length == 0 && _existingProtectedPassword is not null)
            device.ProtectedPassword = _existingProtectedPassword;
        else
            device.SetPassword(PassBox.Password);
        return device;
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text.Trim(), out port) && port is > 0 and <= 65535;

    private void OnTlsChecked(object sender, RoutedEventArgs e)
    {
        if (HttpPortBox.Text.Trim() == "80")
            HttpPortBox.Text = "443";
    }

    private void OnTlsUnchecked(object sender, RoutedEventArgs e)
    {
        if (HttpPortBox.Text.Trim() == "443")
            HttpPortBox.Text = "80";
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var device = BuildDevice(out string? error);
        if (device is null)
        {
            ShowMessage(error!, Brushes.Firebrick);
            return;
        }

        // All three ports at once: an installer chasing a missing port forward wants the
        // whole picture, and a firewalled port costs a full timeout to discover.
        var conn = device.ToConnection();
        string webLabel = device.UseTls ? $"HTTPS {conn.HttpPort}" : $"HTTP {conn.HttpPort}";
        string rtspLabel = $"RTSP {conn.RtspPort}";
        string sdkLabel = $"SDK {conn.SdkPort}";

        TestResults.Children.Clear();
        var webRow = AddRow(webLabel);
        var rtspRow = AddRow(rtspLabel);
        var sdkRow = AddRow(sdkLabel);

        TestButton.IsEnabled = false;
        try
        {
            var (web, rtsp, sdk) = ConnectivityProbe.StartAll(
                conn, device.CreateClient, _probes.Token);
            var results = await Task.WhenAll(
                RenderWhenDone(webRow, webLabel, web),
                RenderWhenDone(rtspRow, rtspLabel, rtsp),
                RenderWhenDone(sdkRow, sdkLabel, sdk));
            AddSummary(results);
        }
        catch (OperationCanceledException)
        {
            // Dialog closed mid-probe; nothing left to report to.
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private static async Task<ProbeResult> RenderWhenDone(
        TextBlock row, string label, Task<ProbeResult> probe)
    {
        var result = await probe;
        // Green / amber / red, where amber means "something answered, just not the whole
        // answer" — an error reply still proves the port is open and forwarded.
        (row.Foreground, string glyph) = result.Severity switch
        {
            ProbeSeverity.Pass => (Brushes.DarkGreen, "✓"),
            ProbeSeverity.Caution => (Brushes.DarkGoldenrod, "⚠"),
            _ => (Brushes.Firebrick, "✕"),
        };
        row.Text = $"{glyph} {label} — {result.Detail} ({result.Elapsed.TotalSeconds:0.0}s)";
        return result;
    }

    /// <summary>
    /// Spells out what a partial pass actually costs, because the failing port decides which
    /// feature breaks: web is fatal, RTSP kills playback and export, SDK kills only Access.
    /// </summary>
    private void AddSummary(ProbeResult[] results)
    {
        var worst = results.Max(r => r.Severity);
        if (worst == ProbeSeverity.Pass)
        {
            ShowLine("All three ports reachable.", Brushes.DarkGreen, italic: true);
            return;
        }

        // Which port fell short decides which feature breaks, so name the consequence rather
        // than just the colour. Cautions are listed separately from dead ports: a port that
        // answered is reachable, and the fix is on the device, not the firewall.
        var dead = results.Where(r => r.Severity == ProbeSeverity.Fail).Select(r => r.Target).ToList();
        var iffy = results.Where(r => r.Severity == ProbeSeverity.Caution).Select(r => r.Target).ToList();

        var notes = new List<string>();
        if (dead.Count > 0)
            notes.Add("no answer on " + string.Join(" and ", dead.Select(PortName)) +
                " — " + string.Join("; ", dead.Select(Consequence)));
        if (iffy.Count > 0)
            notes.Add(string.Join(" and ", iffy.Select(PortName)) +
                " answered but not as expected — the port is open; check the device side");

        ShowLine("Saving is still allowed. " + string.Join(". ", notes) + ".",
            worst == ProbeSeverity.Fail ? Brushes.Firebrick : Brushes.DarkGoldenrod, italic: true);
    }

    private static string PortName(ProbeTarget target) => target switch
    {
        ProbeTarget.Web => "the web port",
        ProbeTarget.RtspPort => "RTSP",
        _ => "the SDK port",
    };

    private static string Consequence(ProbeTarget target) => target switch
    {
        ProbeTarget.Web => "nothing works without it",
        ProbeTarget.RtspPort => "playback and export need it",
        _ => "the Access tab needs it",
    };

    private TextBlock AddRow(string pending)
    {
        var row = new TextBlock
        {
            Text = $"… {pending}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 2),
            Foreground = Brushes.Gray,
        };
        TestResults.Children.Add(row);
        return row;
    }

    private void ShowMessage(string text, Brush color)
    {
        TestResults.Children.Clear();
        ShowLine(text, color, italic: false);
    }

    private void ShowLine(string text, Brush color, bool italic)
    {
        TestResults.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, italic ? 4 : 0, 0, 2),
            Foreground = color,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _probes.Cancel();
        _probes.Dispose();
        base.OnClosed(e);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var device = BuildDevice(out string? error);
        if (device is null)
        {
            ShowMessage(error!, Brushes.Firebrick);
            return;
        }
        Result = device;
        DialogResult = true;
    }
}
