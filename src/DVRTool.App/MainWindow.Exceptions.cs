using System.Text;
using System.Windows;
using DVRTool.Core;

namespace DVRTool.App;

/// <summary>
/// The Config tab's second fleet audit: <b>who hears about an illegal login</b> — and, by the
/// dropdown, about a full disk, a dead NIC or any other fault the recorder raises.
/// </summary>
/// <remarks>
/// <para>
/// It lives beside the clock audit because it answers the same shape of question about the
/// same fleet in the same two requests per recorder, and because the thing it checks cannot
/// be checked from a recorder's own web UI at all: the e-mail tick is on the Exception page
/// and the SMTP server is two menus away, so a box ticked above blank mail settings reads as
/// configured on every screen an operator would think to look at. The first live sweep of the
/// fleet (2026-09-18, 17 Hikvision recorders) found exactly one recorder mailing on illegal
/// login and three with no mail settings at all.
/// </para>
/// <para>
/// Read-only. The fix is a tick on the recorder's own Exception page; DVRTool reports it
/// because a write here would be a fleet-wide change with no undo in this app.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private int _exceptionGen;

    private sealed record ExceptionFleetRow(
        string Device, string HasIt, string Email, string MailServer, string Verdict,
        string Detail);

    private async void OnAuditFleetExceptions(object sender, RoutedEventArgs e) =>
        await RunConfigWorkAsync(AuditFleetExceptionsAsync);

    /// <summary>The exception the audit is about — the dropdown, or illegal login.</summary>
    private string SelectedExceptionType =>
        (ExceptionTypeCombo?.SelectedItem as System.Windows.Controls.ComboBoxItem)?
            .Tag as string ?? ExceptionTypes.IllegalLogin;

    private async Task AuditFleetExceptionsAsync(CancellationToken ct)
    {
        int gen = ++_exceptionGen;
        var recorders = _devices.Where(d => !d.IsPanel).ToList();
        if (recorders.Count == 0)
        {
            SetStatus("No recorders saved yet — Add Device….");
            return;
        }

        string type = SelectedExceptionType;
        ConfigWarning.Visibility = Visibility.Collapsed;
        SetStatus($"Reading {ExceptionTypes.Label(type).ToLowerInvariant()} alerting on " +
            $"{recorders.Count} recorder(s) …");

        // Ephemeral clients, all recorders at once — the clock sweep's shape, for the same
        // reason: one slow site must not serialize the rest, and each read carries its own
        // failure instead of faulting the sweep.
        var rows = await Task.WhenAll(recorders.Select(d => SweepExceptionsAsync(d, type, ct)));
        if (gen != _exceptionGen)
            return;

        var audit = ExceptionAudit.Build(type, rows);

        // Render first, then speak: ShowFleetMatrix's trap applies to any grid whose repaint
        // can set its own status — a status set before the grid is overwritten by it.
        ExceptionFleetGrid.ItemsSource = audit.Rows.Select(r => new ExceptionFleetRow(
            r.DeviceName,
            r.Ok ? (r.State is ExceptionEmailState.Unsupported ? "no" : "yes") : "?",
            r.Ok
                ? r.State switch
                {
                    ExceptionEmailState.Emails => "on",
                    ExceptionEmailState.GoesNowhere => "on, goes nowhere",
                    ExceptionEmailState.NotEnabled => "off",
                    _ => "—",
                }
                : "?",
            r.Ok ? r.Email.ShortSummary : "?",
            r.Verdict,
            ExceptionRowDetail(r))).ToList();

        ExceptionFleetSummary.Text = audit.Summary;

        // The loud case is the one that looks fine on the device: ticked, and undeliverable.
        var goesNowhere = audit.Rows
            .Where(r => r.State is ExceptionEmailState.GoesNowhere)
            .ToList();
        if (goesNowhere.Count > 0)
            ShowConfigWarning("E-mail is ticked and would not arrive: " +
                string.Join("; ", goesNowhere.Select(r => $"{r.DeviceName} — {r.Email.Gap}")));
        else if (audit.Unreachable.Any())
            ShowConfigWarning(
                "Some recorders could not be read — their alerting is unknown, not fine.");

        SetStatus(audit.Summary);
    }

    private static string ExceptionRowDetail(ExceptionAuditRow row)
    {
        if (!row.Ok)
            return row.Error ?? "";
        var text = new StringBuilder();
        text.AppendLine($"When {row.Trigger?.Label ?? ExceptionTypes.Label(row.EventType)} " +
            $"fires, this recorder does: {row.OtherNotifications}");
        text.AppendLine($"Mail settings: {row.Email.Summary}");
        if (row.Email.SenderAddress.Length > 0)
            text.AppendLine($"From: {row.Email.SenderName} <{row.Email.SenderAddress}>");
        if (row.State is ExceptionEmailState.NotEnabled)
            text.AppendLine(
                "Fix it on the recorder: Configuration → System → Event → Exception, pick " +
                $"{ExceptionTypes.Label(row.EventType)}, tick Send Email.");
        return text.ToString().TrimEnd();
    }

    private async Task<ExceptionAuditRow> SweepExceptionsAsync(SavedDevice device, string type,
        CancellationToken ct)
    {
        INvrClient? client = null;
        try
        {
            client = SavedDeviceClients.CreateClient(device);
            if (client is not IExceptionNotificationClient exceptions)
                return ExceptionAuditRow.NotImplemented(device.Name, type,
                    device.VendorKind.ToString());

            var check = await DeviceIdentityGuard.CheckAsync(client,
                device.ExpectedSerial.Length > 0 ? device.ExpectedSerial : null, device.Name,
                ct: ct);
            if (check.Verdict == IdentityVerdict.Mismatch)
                return ExceptionAuditRow.Failed(device.Name, type, $"WRONG DEVICE — {check.Message}");

            return await ExceptionSweep.ReadAsync(device.Name, exceptions, type, ct: ct);
        }
        catch (Exception ex) when (NvrException.IsPerDeviceFailure(ex, ct))
        {
            return ExceptionAuditRow.Failed(device.Name, type, Shorten(ex.Message));
        }
        finally
        {
            client?.Dispose();
        }
    }
}
