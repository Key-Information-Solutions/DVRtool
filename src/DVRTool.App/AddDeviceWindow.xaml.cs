using System.Windows;

namespace DVRTool.App;

public partial class AddDeviceWindow : Window
{
    public SavedDevice? Result { get; private set; }

    public AddDeviceWindow()
    {
        InitializeComponent();
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
        if (!int.TryParse(HttpPortBox.Text.Trim(), out int httpPort) ||
            !int.TryParse(RtspPortBox.Text.Trim(), out int rtspPort))
        {
            error = "Ports must be numbers.";
            return null;
        }

        var device = new SavedDevice
        {
            Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : host,
            Vendor = VendorCombo.SelectedIndex == 1 ? "dahua" : "hikvision",
            Host = host,
            HttpPort = httpPort,
            RtspPort = rtspPort,
            UseTls = TlsCheck.IsChecked == true,
            Username = UserBox.Text.Trim(),
        };
        device.SetPassword(PassBox.Password);
        return device;
    }

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
            TestResult.Text = error;
            return;
        }

        TestResult.Text = "Connecting …";
        try
        {
            using var client = device.CreateClient();
            var info = await client.GetDeviceInfoAsync();
            TestResult.Text = $"OK — {info.Model} (serial {info.SerialNumber}, fw {info.FirmwareVersion})";
        }
        catch (Exception ex)
        {
            TestResult.Text = $"Failed: {ex.Message}";
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var device = BuildDevice(out string? error);
        if (device is null)
        {
            TestResult.Text = error;
            return;
        }
        Result = device;
        DialogResult = true;
    }
}
