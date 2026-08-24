using System.Globalization;
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

    // The rest of the saved fleet, minus the record being edited. Held so this dialog can
    // answer the question a single record cannot: whether the address just typed already
    // belongs to another system — the mistake that a site with several recorders behind one
    // IP and one shared password makes invisible.
    private readonly List<SavedDevice> _fleet;

    // The serial this record is bound to, carried across a save. Kept when the address is
    // edited: the record means a particular recorder, not a particular port, and a port typo
    // must not silently re-bind it. "Unbind" is the deliberate way to release it.
    private string _expectedSerial = "";

    public SavedDevice? Result { get; private set; }

    public AddDeviceWindow(IEnumerable<SavedDevice>? fleet = null)
    {
        InitializeComponent();
        _fleet = fleet?.ToList() ?? [];
        ApplyVendorToSdkPort();
    }

    public AddDeviceWindow(SavedDevice device, IEnumerable<SavedDevice>? fleet = null)
    {
        InitializeComponent();
        // Excluded by reference: two records may share a name, and the one being edited must
        // not be reported as colliding with itself.
        _fleet = fleet?.Where(d => !ReferenceEquals(d, device)).ToList() ?? [];
        Title = "Edit NVR";
        _existingProtectedPassword = device.ProtectedPassword;
        _expectedSerial = device.ExpectedSerial;
        ShowIdentity();

        NameBox.Text = device.Name;
        VendorCombo.SelectedIndex = device.Vendor.Equals("dahua", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        HostBox.Text = device.Host;
        // TLS and vendor before the ports: checking the box runs OnTlsChecked, which rewrites
        // an HTTP port of 80 to 443, and picking the vendor runs OnVendorChanged, which can
        // rewrite the SDK port. Prefilling the stored ports afterwards lets them win — this
        // dialog must never quietly renumber a port an operator already saved.
        TlsCheck.IsChecked = device.UseTls;
        HttpPortBox.Text = device.HttpPort.ToString();
        RtspPortBox.Text = device.RtspPort.ToString();
        SdkPortBox.Text = device.SdkPort.ToString();
        UserBox.Text = device.Username;
        PasswordHint.Visibility = Visibility.Visible;
    }

    /// <summary>The vendor the combo is currently on.</summary>
    private Vendor SelectedVendor =>
        VendorCombo.SelectedIndex == 1 ? Vendor.Dahua : Vendor.Hikvision;

    private void OnVendorChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires while XAML is still applying SelectedIndex="0", before the rest of the dialog
        // is built. The hint is the last of the three controls this touches to be declared, so
        // it is the one worth testing. Nothing to paint yet — the add-mode constructor calls
        // ApplyVendorToSdkPort itself once everything exists.
        if (SdkPortHint is null)
            return;
        ApplyVendorToSdkPort();
    }

    /// <summary>
    /// Retitles and re-defaults the SDK-port row for the selected vendor. The two vendors
    /// disagree on this port by a wide margin — 8000 for Hikvision's HCNetSDK, 37777 for
    /// Dahua's DHNetSDK — so a Dahua recorder left on the Hikvision default gets reported as
    /// having a dead port by <b>Test connection</b> when nothing is wrong with it.
    /// </summary>
    private void ApplyVendorToSdkPort()
    {
        var vendor = SelectedVendor;
        int mine = VendorPorts.Sdk(vendor);
        int theirs = VendorPorts.Sdk(vendor == Vendor.Dahua ? Vendor.Hikvision : Vendor.Dahua);

        // Only overwrite a box still holding the *other* vendor's factory number (or nothing
        // at all). A port the operator read off the device is theirs to keep: flipping the
        // vendor combo must not silently discard it.
        string current = SdkPortBox.Text.Trim();
        if (current.Length == 0 || current == theirs.ToString(CultureInfo.InvariantCulture))
            SdkPortBox.Text = mine.ToString(CultureInfo.InvariantCulture);

        // Name the port the way the device's own web UI names it, so an installer reading
        // values off a recorder is matching labels rather than translating them. The two
        // hints differ on how much the number matters, because the answer differs: on
        // Hikvision the Live tab streams over it, on Dahua nothing in DVRTool dials it.
        (SdkPortLabel.Text, SdkPortHint.Text) = vendor == Vendor.Dahua
            ? ("TCP port",
               "Dahua's \"TCP Port\" (DHNetSDK) — 37777 from the factory. DVRTool drives Dahua " +
               "over HTTP + RTSP and never dials it; Test connection just reports whether " +
               "SmartPSS / DSS could reach it from here.")
            : ("SDK port",
               "Hikvision's \"Server Port\" (HCNetSDK) — 8000 from the factory. The Live tab's " +
               "SDK transport streams over it, which is the route that works when RTSP is " +
               "closed — so on Hikvision this one is worth getting right.");
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
            Vendor = SelectedVendor == Vendor.Dahua ? "dahua" : "hikvision",
            Host = host,
            HttpPort = httpPort,
            RtspPort = rtspPort,
            SdkPort = sdkPort,
            UseTls = TlsCheck.IsChecked == true,
            Username = UserBox.Text.Trim(),
        };
        device.ExpectedSerial = _expectedSerial;
        if (PassBox.Password.Length == 0 && _existingProtectedPassword is not null)
            device.ProtectedPassword = _existingProtectedPassword;
        else
            device.SetPassword(PassBox.Password);
        return device;
    }

    /// <summary>Shows (or hides) which recorder this record is bound to.</summary>
    private void ShowIdentity()
    {
        if (_expectedSerial.Length == 0)
        {
            IdentityRow.Visibility = Visibility.Collapsed;
            return;
        }
        IdentityRow.Visibility = Visibility.Visible;
        IdentityText.Text = $"Bound to serial {_expectedSerial} — a system answering with any " +
            "other serial on this address is refused.";
    }

    /// <summary>
    /// Releases the binding, for the one case where a serial mismatch is legitimate: the
    /// recorder was physically replaced. The next connection binds whatever answers.
    /// </summary>
    private void OnUnbind(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                $"This record is bound to serial {_expectedSerial}.\n\nUnbind it only if the " +
                "recorder itself was replaced. If instead a port or address is wrong, fix " +
                "that — unbinding here would let DVRTool accept whichever system answers, " +
                "including another site's.",
                "DVRTool — release the identity binding",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        // The address pin goes with it: leaving that behind would refuse the replacement on
        // the next connection for the same reason, from a file the operator cannot see.
        if (BuildDevice(out _) is { } current)
            DeviceIdentityStore.Default.Forget(current.Address);
        _expectedSerial = "";
        ShowIdentity();
        ShowMessage("Identity binding released — the next connection will bind whatever answers.",
            Brushes.DarkGoldenrod);
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
        // Named the way the device names it, matching the dialog's own row label above. Taken
        // from the snapshot in `device`, not the live combo: the operator is free to keep
        // editing while the probes run, and every line of this report has to describe the
        // system that was actually tested.
        string sdkLabel = device.VendorKind == Vendor.Dahua
            ? $"TCP {conn.SdkPort}"
            : $"SDK {conn.SdkPort}";

        TestResults.Children.Clear();
        var webRow = AddRow(webLabel);
        var rtspRow = AddRow(rtspLabel);
        var sdkRow = AddRow(sdkLabel);

        TestButton.IsEnabled = false;
        try
        {
            var (web, rtsp, sdk) = ConnectivityProbe.StartAll(
                conn, device.CreateClient, _probes.Token,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null,
                device.Name);
            var results = await Task.WhenAll(
                RenderWhenDone(webRow, webLabel, web),
                RenderWhenDone(rtspRow, rtspLabel, rtsp),
                RenderWhenDone(sdkRow, sdkLabel, sdk));
            AddSummary(results, device.VendorKind);

            // A test that reached the device is the natural moment to bind the record to it —
            // the operator is looking at the serial it just reported.
            var seen = results[0].Identity?.Seen;
            if (_expectedSerial.Length == 0 && seen is { IsUsable: true } &&
                results[0].Status != ProbeStatus.WrongDevice)
            {
                _expectedSerial = seen.Serial.Trim();
                ShowIdentity();
            }
            ReportFleetIssues(device);
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
    /// feature breaks: web is fatal, RTSP kills playback-by-time, and on Hikvision the SDK
    /// port kills the Live tab's other transport.
    /// </summary>
    private void AddSummary(ProbeResult[] results, Vendor vendor)
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
            notes.Add("no answer on " + string.Join(" and ", dead.Select(t => PortName(t, vendor))) +
                " — " + string.Join("; ", dead.Select(t => Consequence(t, vendor))));
        if (iffy.Count > 0)
            notes.Add(string.Join(" and ", iffy.Select(t => PortName(t, vendor))) +
                " answered but not as expected — the port is open; check the device side");

        ShowLine("Saving is still allowed. " + string.Join(". ", notes) + ".",
            worst == ProbeSeverity.Fail ? Brushes.Firebrick : Brushes.DarkGoldenrod, italic: true);
    }

    private static string PortName(ProbeTarget target, Vendor vendor) => target switch
    {
        ProbeTarget.Web => "the web port",
        ProbeTarget.RtspPort => "RTSP",
        _ => vendor == Vendor.Dahua ? "the TCP port" : "the SDK port",
    };

    /// <summary>
    /// What a failed port actually costs. The SDK row is the one that differs by vendor: on
    /// Hikvision the Live tab can stream over it, and does so on the many sites that never
    /// forwarded RTSP; on Dahua nothing in DVRTool dials it, and the Access tab is not the
    /// answer either — that talks to door panels, on its own address list and its own port.
    /// </summary>
    private static string Consequence(ProbeTarget target, Vendor vendor) => target switch
    {
        ProbeTarget.Web => "nothing works without it",
        ProbeTarget.RtspPort when vendor == Vendor.Hikvision =>
            "playback and export need it, and so does RTSP live view — but the Live tab's " +
            "SDK transport does not",
        ProbeTarget.RtspPort => "playback and export need it",
        _ when vendor == Vendor.Hikvision =>
            "the Live tab's SDK transport needs it — the one that works when RTSP is closed — " +
            "and iVMS-4200 / HikCentral cannot reach this recorder from here either",
        _ => "no DVRTool feature needs it on Dahua, so this only means SmartPSS / DSS cannot " +
             "reach this recorder from here",
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

    /// <summary>
    /// Reports what this record looks like alongside the rest of the fleet: the same address
    /// twice, one recorder saved twice, or simply that it shares a host with others and is
    /// told apart by port.
    /// </summary>
    private void ReportFleetIssues(SavedDevice device)
    {
        foreach (var issue in FleetAudit.InspectFor(
            device.ToFleetRecord(), _fleet.Select(d => d.ToFleetRecord())))
        {
            ShowLine(issue.Severity switch
            {
                FleetIssueSeverity.Error => "Conflict: " + issue.Message,
                FleetIssueSeverity.Warning => "Warning: " + issue.Message,
                _ => issue.Message,
            }, issue.Severity switch
            {
                FleetIssueSeverity.Error => Brushes.Firebrick,
                FleetIssueSeverity.Warning => Brushes.DarkGoldenrod,
                _ => Brushes.Gray,
            }, italic: true);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var device = BuildDevice(out string? error);
        if (device is null)
        {
            ShowMessage(error!, Brushes.Firebrick);
            return;
        }

        // Two records on one address cannot both be right, and with a shared account both
        // would connect happily — so this one is refused rather than warned about. The
        // legitimate version of it (several systems on one host) differs by port, and passes.
        var conflict = FleetAudit
            .InspectFor(device.ToFleetRecord(), _fleet.Select(d => d.ToFleetRecord()))
            .FirstOrDefault(i => i.Kind == FleetIssueKind.DuplicateAddress);
        if (conflict is not null)
        {
            ShowMessage(conflict.Message, Brushes.Firebrick);
            return;
        }

        Result = device;
        DialogResult = true;
    }
}
