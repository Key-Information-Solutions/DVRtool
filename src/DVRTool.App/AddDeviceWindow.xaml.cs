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
        ApplyVendorToPorts();
    }

    public AddDeviceWindow(SavedDevice device, IEnumerable<SavedDevice>? fleet = null)
    {
        InitializeComponent();
        // Excluded by reference: two records may share a name, and the one being edited must
        // not be reported as colliding with itself.
        _fleet = fleet?.Where(d => !ReferenceEquals(d, device)).ToList() ?? [];
        Title = "Edit device";
        _existingProtectedPassword = device.ProtectedPassword;
        _expectedSerial = device.ExpectedSerial;
        ShowIdentity();

        // Kind first — it decides which rows even exist — and then locked: a record means
        // one particular piece of hardware, and a recorder does not become a door panel by
        // editing. The wrong-kind record gets removed and re-added, deliberately.
        KindCombo.SelectedIndex = device.IsPanel ? 1 : 0;
        KindCombo.IsEnabled = false;

        NameBox.Text = device.Name;
        VendorCombo.SelectedIndex = VendorIndex(device.VendorKind);
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

    /// <summary>The vendor the combo is currently on — the combo lists them in enum order.</summary>
    private Vendor SelectedVendor => VendorCombo.SelectedIndex switch
    {
        1 => Vendor.Dahua,
        2 => Vendor.NxWitness,
        _ => Vendor.Hikvision,
    };

    private static int VendorIndex(Vendor vendor) => vendor switch
    {
        Vendor.Dahua => 1,
        Vendor.NxWitness => 2,
        _ => 0,
    };

    /// <summary>True when the dialog is describing a door panel rather than a recorder.</summary>
    private bool IsPanelKind => KindCombo.SelectedIndex == 1;

    private void OnVendorChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires while XAML is still applying SelectedIndex="0", before the rest of the dialog
        // is built. The TLS box is the last of the controls this touches to be declared, so
        // it is the one worth testing. Nothing to paint yet — the add-mode constructor calls
        // ApplyVendorToPorts itself once everything exists.
        if (SdkPortHint is null || TlsCheck is null)
            return;
        ApplyVendorToPorts();
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        // Same construction-order guard as OnVendorChanged.
        if (SdkPortHint is null || TlsCheck is null)
            return;
        ApplyKind();
    }

    /// <summary>
    /// Shows the rows the selected kind actually has. A DS-K controller exposes only the
    /// SDK protocol — no web server, no RTSP — so the recorder-only rows are removed rather
    /// than left as inputs that would configure nothing.
    /// </summary>
    private void ApplyKind()
    {
        bool panel = IsPanelKind;
        var recorderRows = panel ? Visibility.Collapsed : Visibility.Visible;
        HttpPortLabel.Visibility = recorderRows;
        HttpPortBox.Visibility = recorderRows;
        RtspPortLabel.Visibility = recorderRows;
        RtspPortBox.Visibility = recorderRows;
        TlsCheck.Visibility = recorderRows;

        if (panel)
        {
            // The DS-K family is the only panel hardware DVRTool speaks, and it is driven
            // over HCNetSDK — forcing the vendor keeps ApplyVendorToPorts from leaving a
            // Dahua 37777 behind when the operator flips a half-filled recorder to a panel.
            VendorCombo.SelectedIndex = 0;
            VendorCombo.IsEnabled = false;
            VendorCombo.ToolTip = "Door panels are Hikvision/OEM DS-K controllers — " +
                "the only panel family DVRTool speaks.";
            ShowSdkPortRow(true);
            (SdkPortLabel.Text, SdkPortHint.Text) = ("SDK port",
                "The panel's \"Server Port\" (HCNetSDK) — 8000 from the factory, and the " +
                "only port these controllers have. Roster reads, identity and the CLI's " +
                "gated writes all ride it.");
        }
        else
        {
            VendorCombo.IsEnabled = true;
            VendorCombo.ToolTip = null;
            ApplyVendorToPorts();
        }
    }

    /// <summary>
    /// Re-defaults the port rows for the selected vendor. The two appliance vendors disagree
    /// on the SDK port by a wide margin — 8000 for Hikvision's HCNetSDK, 37777 for Dahua's
    /// DHNetSDK — so a Dahua recorder left on the Hikvision default gets reported as having a
    /// dead port by <b>Test connection</b> when nothing is wrong with it. Nx is different
    /// again: HTTPS and RTSP share 7001 and there is no SDK port, so that row disappears.
    /// </summary>
    private void ApplyVendorToPorts()
    {
        var vendor = SelectedVendor;

        // Only overwrite a box still holding *another* vendor's factory number (or nothing at
        // all). A port the operator read off the device is theirs to keep: flipping the vendor
        // combo must not silently discard it.
        var others = Enum.GetValues<Vendor>().Where(v => v != vendor).ToList();
        void Redefault(TextBox box, int mine, IEnumerable<int> theirs)
        {
            string current = box.Text.Trim();
            if (current.Length == 0 ||
                theirs.Any(t => current == t.ToString(CultureInfo.InvariantCulture)))
                box.Text = mine.ToString(CultureInfo.InvariantCulture);
        }

        // Nx serves HTTPS from the factory; the appliance vendors serve HTTP. Set the box
        // before the ports so OnTlsChecked's 80↔443 swap has already run.
        if (VendorPorts.DefaultsToTls(vendor) && TlsCheck.IsChecked != true)
            TlsCheck.IsChecked = true;
        else if (!VendorPorts.DefaultsToTls(vendor) && TlsCheck.IsChecked == true &&
                 HttpPortBox.Text.Trim() == VendorPorts.NxWitnessServer.ToString(CultureInfo.InvariantCulture))
            TlsCheck.IsChecked = false;

        bool tls = TlsCheck.IsChecked == true;
        Redefault(HttpPortBox, VendorPorts.Web(vendor, tls),
            others.SelectMany(v => new[] { VendorPorts.Web(v, true), VendorPorts.Web(v, false) }));
        Redefault(RtspPortBox, VendorPorts.Rtsp(vendor), others.Select(VendorPorts.Rtsp));

        if (!VendorPorts.HasSdkPort(vendor))
        {
            ShowSdkPortRow(false);
            return;
        }
        ShowSdkPortRow(true);
        Redefault(SdkPortBox, VendorPorts.Sdk(vendor),
            others.Where(VendorPorts.HasSdkPort).Select(VendorPorts.Sdk));

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

    /// <summary>
    /// The SDK-port row exists only for vendors that have one. Hidden rather than disabled
    /// for Nx: a greyed box with a number in it would still read as a port to forward.
    /// </summary>
    private void ShowSdkPortRow(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SdkPortLabel.Visibility = visibility;
        SdkPortBox.Visibility = visibility;
        SdkPortHint.Visibility = visibility;
    }

    private SavedDevice? BuildDevice(out string? error)
    {
        error = null;
        bool panel = IsPanelKind;
        string host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            error = "Host is required.";
            return null;
        }
        var vendor = panel ? Vendor.Hikvision : SelectedVendor;
        // A vendor with no SDK port (Nx) has the row hidden, and the record carries 0: there
        // is nothing for the number to mean, and the port check must not dial it.
        int sdkPort = 0;
        if ((panel || VendorPorts.HasSdkPort(vendor)) && !TryPort(SdkPortBox.Text, out sdkPort))
        {
            error = "Ports must be numbers between 1 and 65535.";
            return null;
        }
        // A panel's HTTP/RTSP boxes are hidden, not consulted: whatever a half-filled
        // recorder left in them must not fail a record that has no such ports.
        int httpPort = 80, rtspPort = 554;
        if (!panel &&
            (!TryPort(HttpPortBox.Text, out httpPort) || !TryPort(RtspPortBox.Text, out rtspPort)))
        {
            error = "Ports must be numbers between 1 and 65535.";
            return null;
        }

        var device = new SavedDevice
        {
            Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : host,
            Kind = panel ? "panel" : "recorder",
            Vendor = VendorNames.Key(vendor),
            Host = host,
            HttpPort = httpPort,
            RtspPort = rtspPort,
            SdkPort = sdkPort,
            UseTls = !panel && TlsCheck.IsChecked == true,
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

        if (device.IsPanel)
        {
            await TestPanelAsync(device);
            return;
        }

        // All the ports at once: an installer chasing a missing port forward wants the
        // whole picture, and a firewalled port costs a full timeout to discover. A vendor
        // with no SDK port (Nx) gets two rows, not a third one probing nothing.
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
        bool hasSdkPort = VendorPorts.HasSdkPort(device.VendorKind);

        TestResults.Children.Clear();
        var webRow = AddRow(webLabel);
        var rtspRow = AddRow(rtspLabel);
        var sdkRow = hasSdkPort ? AddRow(sdkLabel) : null;

        TestButton.IsEnabled = false;
        try
        {
            var (web, rtsp, sdk) = ConnectivityProbe.StartAll(
                conn, device.CreateClient, _probes.Token,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null,
                device.Name);
            var renders = new List<Task<ProbeResult>>
            {
                RenderWhenDone(webRow, webLabel, web),
                RenderWhenDone(rtspRow, rtspLabel, rtsp),
            };
            if (sdkRow is not null)
                renders.Add(RenderWhenDone(sdkRow, sdkLabel, sdk));
            var results = await Task.WhenAll(renders);
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

    /// <summary>
    /// The panel version of the test: one TCP probe of the one port these controllers
    /// have. A login is deliberately not attempted — DS-K firmware locks out the calling
    /// IP after a handful of failures, and iVMS-4200 usually shares that IP — so identity
    /// binds on the first roster read instead of here.
    /// </summary>
    private async Task TestPanelAsync(SavedDevice device)
    {
        var conn = device.ToConnection();
        string sdkLabel = $"SDK {conn.SdkPort}";
        TestResults.Children.Clear();
        var row = AddRow(sdkLabel);

        TestButton.IsEnabled = false;
        try
        {
            var result = await RenderWhenDone(row, sdkLabel,
                ConnectivityProbe.ProbeSdkAsync(conn, _probes.Token));
            ShowLine(result.IsGood
                    ? "The panel's only port answered (a login is not attempted here). The " +
                      "controller identifies itself by serial on the first roster read, " +
                      "which binds this record to it."
                    : "This is the only port a DS-K controller speaks — no Access roster " +
                      "and no Users-tab read works without it.",
                result.IsGood ? Brushes.DarkGreen : Brushes.Firebrick, italic: true);
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
            ShowLine(results.Length == 3 ? "All three ports reachable." : "Both ports reachable.",
                Brushes.DarkGreen, italic: true);
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
