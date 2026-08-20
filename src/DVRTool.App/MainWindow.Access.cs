using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Windows;
using DVRTool.Core;
using DVRTool.Vendors.HikvisionAccess;
using DVRTool.Vendors.HikvisionIvms;
using Microsoft.Win32;

namespace DVRTool.App;

/// <summary>
/// The Access tab: door-access panels rather than video recorders.
/// </summary>
/// <remarks>
/// <para>
/// Read-only toward the panels on purpose. Granting or revoking changes physical door
/// access, and the CLI gates those behind an explicit <c>--force</c> plus a read-back
/// verification — a button in a tab is the wrong place for a write nobody has to confirm.
/// </para>
/// <para>
/// Cardholder names are imported one-way from iVMS (iVMS → DVRTool). The panels store no
/// identity at all — a credential there is a fob number, its doors and a validity window —
/// so every name in this tab comes from the imported map, and nothing here writes back
/// toward iVMS. See <c>docs/hikvision-access-control-findings.md</c> §4 and
/// <c>docs/ivms-integration-findings.md</c> §5.
/// </para>
/// </remarks>
public partial class MainWindow
{
    // Same stale-completion guard the Users tab uses: the Access tab talks to devices that
    // have nothing to do with the selected NVR, so it cannot ride on _selectionGen. Its own
    // CTS/task because the panel clients it opens are its own, and OnClosing waits for them.
    private int _accessGen;
    private CancellationTokenSource? _accessCts;
    private Task? _accessTask;

    /// <summary>
    /// One (panel, card) presence, flattened for the grid.
    /// </summary>
    /// <remarks>
    /// Flattened rather than a column per panel: the fleet is whatever the operator typed,
    /// so a per-panel column set would have to be rebuilt at runtime, and a fob's rights
    /// differ per panel anyway — one row per presence is what an offboarding check reads.
    /// </remarks>
    private sealed record AccessRow(
        string Card, string Name, string Panel, string Doors, string Valid, string ValidUntil);

    /// <summary>
    /// Everything needed to open a panel connection, snapshotted off the controls before any
    /// work starts.
    /// </summary>
    /// <remarks>
    /// The CLI's <c>AccessSettings</c> cannot be reused — it resolves an option dictionary and
    /// falls back to a console password prompt — so the GUI resolves the same inputs itself and
    /// constructs <see cref="AccessPanelConnection"/> directly.
    /// </remarks>
    private sealed record PanelConnectionSettings(
        IReadOnlyList<string> Panels, string Username, string Password, int SdkPort,
        string? SdkDirectory)
    {
        internal IAccessControlClient Connect(string host) =>
            new HikvisionAccessClient(
                new AccessPanelConnection
                {
                    Host = host,
                    SdkPort = SdkPort,
                    Username = Username,
                    Password = Password,
                },
                SdkDirectory);
    }

    private void InitializeAccessTab()
    {
        // Prefilled from the same OCB_* variables the CLI reads, so an operator who already
        // has the fleet configured for `dvrtool access` does not retype it here.
        if (Environment.GetEnvironmentVariable("OCB_PANELS") is { Length: > 0 } panels)
            AccessPanelsBox.Text = panels;
        if (Environment.GetEnvironmentVariable("OCB_USER") is { Length: > 0 } user)
            AccessUserBox.Text = user;
        if (Environment.GetEnvironmentVariable("OCB_PASS") is { Length: > 0 } pass)
            AccessPassBox.Password = pass;
        if (Environment.GetEnvironmentVariable("OCB_SDK_PORT") is { Length: > 0 } port)
            AccessPortBox.Text = port;
        if (Environment.GetEnvironmentVariable("OCB_SDK_DIR") is { Length: > 0 } sdkDir)
            AccessSdkDirBox.Text = sdkDir;

        RefreshIdentityStatus();
    }

    // ----- roster -----

    private async void OnLoadRoster(object sender, RoutedEventArgs e) =>
        await RunAccessWorkAsync(LoadRosterAsync);

    private async Task LoadRosterAsync(CancellationToken ct)
    {
        int gen = ++_accessGen;
        if (!TryGetPanelSettings(out var settings))
            return;

        SetStatus($"Reading {settings.Panels.Count} panel(s) …");
        try
        {
            var roster = await ReadRosterAsync(settings, ct);
            if (gen != _accessGen)
                return;

            var map = TryLoadIdentityMap();
            ShowRoster(map is null ? roster : roster.EnrichWith(map));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (gen != _accessGen)
                return;
            SetStatus($"Panel read failed: {Shorten(ex.Message)}");
        }
    }

    /// <summary>
    /// Reads every panel, keeping per-panel failures instead of dropping them, and folds the
    /// results into a roster.
    /// </summary>
    /// <remarks>
    /// The whole loop runs on a worker thread: only the client's read methods do their own
    /// <see cref="Task.Run(Action)"/>, while <c>HikvisionAccessClient</c>'s constructor logs in
    /// synchronously and its Dispose logs out — both block, and both would freeze the UI here.
    /// </remarks>
    private static Task<AccessRoster> ReadRosterAsync(PanelConnectionSettings settings,
        CancellationToken ct) =>
        Task.Run(async () =>
        {
            var results = new List<AccessPanelResult>();
            foreach (string host in settings.Panels)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var client = settings.Connect(host);
                    var info = await client.GetDeviceInfoAsync(ct);
                    var cards = await client.GetCardsAsync(ct);
                    results.Add(new AccessPanelResult
                    {
                        PanelHost = host,
                        Serial = info.SerialNumber,
                        Cards = cards,
                    });
                }
                catch (Exception ex) when (ex is NvrException or ArgumentException)
                {
                    // A panel that could not be read is carried as a failure, never dropped:
                    // omitting it would make every fob on it look revoked, which is the exact
                    // wrong answer for the question this tab exists to answer.
                    results.Add(AccessPanelResult.Failed(host, ex.Message));
                }
            }
            return AccessRoster.Build(results);
        }, ct);

    /// <summary>
    /// Fills the grid, and states plainly when the view is partial.
    /// </summary>
    /// <remarks>
    /// "No access" is not a safe conclusion from a partial read — a fob can still be active on
    /// a panel that did not answer — so an unreadable panel gets a visible warning of its own
    /// rather than only a line in the status bar the operator may have scrolled past.
    /// </remarks>
    private void ShowRoster(AccessRoster roster)
    {
        var rows = new List<AccessRow>();
        foreach (var entry in roster.Entries)
        {
            foreach (var presence in entry.Presence)
            {
                rows.Add(new AccessRow(
                    entry.CardNo,
                    entry.Name ?? "",
                    presence.PanelHost,
                    presence.Doors.Count == 0 ? "(none)" : string.Join(",", presence.Doors),
                    presence.Valid ? "yes" : "REVOKED",
                    presence.ValidUntil?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""));
            }
        }
        AccessGrid.ItemsSource = rows;

        var failed = roster.FailedPanels.ToList();
        int ok = roster.Panels.Count - failed.Count;
        if (failed.Count == 0)
        {
            AccessWarning.Text = "";
            AccessWarning.Visibility = Visibility.Collapsed;
            SetStatus($"{roster.Entries.Count} fob(s), {rows.Count} presence row(s) " +
                      $"across {ok} panel(s).");
            return;
        }

        AccessWarning.Text =
            "PARTIAL — " +
            string.Join("; ", failed.Select(p => $"{p.PanelHost}: {Shorten(p.Error ?? "unreadable")}")) +
            ". A fob could still be active on a panel that could not be read, so “no access” " +
            "is not a safe conclusion from this view.";
        AccessWarning.Visibility = Visibility.Visible;
        SetStatus($"PARTIAL — {roster.Entries.Count} fob(s) from {ok} of " +
                  $"{roster.Panels.Count} panel(s); see the warning above.");
    }

    // ----- cardholder names (one-way iVMS → DVRTool) -----

    private void OnImportIdentityCsv(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import an iVMS Person export",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var imported = IvmsCsvImporter.Import(dialog.FileName);
            var merged = MergeIntoIdentityStore(imported);
            SetStatus($"Imported {imported.Count} name(s) from " +
                      $"{Path.GetFileName(dialog.FileName)}; the map now holds {merged.Count}.");
        }
        catch (Exception ex)
        {
            SetStatus($"CSV import failed: {Shorten(ex.Message)}");
        }
        RefreshIdentityStatus();
    }

    private async void OnImportFromIvms(object sender, RoutedEventArgs e) =>
        await RunAccessWorkAsync(ImportFromIvmsAsync);

    /// <summary>
    /// Reads the live iVMS person roster and binds names to fobs by unique expiry.
    /// </summary>
    /// <remarks>
    /// The panels are read as part of this: expiry correlation joins iVMS's per-person expiry
    /// against the panel card's validity window, so it needs both sides at once. Deliberately
    /// partial — the supported CSV export is the complete path — and the operator is told so
    /// every time rather than left to infer it from the counts.
    /// </remarks>
    private async Task ImportFromIvmsAsync(CancellationToken ct)
    {
        int gen = ++_accessGen;
        if (!TryGetPanelSettings(out var settings))
            return;

        IvmsInstall install;
        byte[] key;
        try
        {
            install = ResolveIvmsInstall();
            // A blank box resolves the same way the CLI does: IVMS_DB_KEY, then the key
            // cached for this install. The typed value goes straight to the key store and is
            // never echoed to the status bar, a tooltip, or an exception message.
            string typed = IvmsKeyBox.Password;
            key = IvmsKeyStore.Resolve(typed.Length > 0 ? typed : null, install.Id);
        }
        catch (Exception ex)
        {
            SetStatus($"iVMS import failed: {Shorten(ex.Message)}");
            return;
        }

        try
        {
            SetStatus($"Reading the {install.Product} person roster …");
            var persons = await Task.Run(
                () => IvmsPersonReader.ReadPersons(install.PersonDbPath, key), ct);
            if (gen != _accessGen)
                return;

            SetStatus($"Read {persons.Count} person(s) from {install.Product}; " +
                      $"reading {settings.Panels.Count} panel(s) to correlate …");
            var roster = await ReadRosterAsync(settings, ct);
            if (gen != _accessGen)
                return;

            var result = IvmsExpiryCorrelator.Correlate(persons, roster);
            var merged = MergeIntoIdentityStore(result.Map);
            RefreshIdentityStatus();
            ShowRoster(roster.EnrichWith(merged));

            // Modal only while the window is staying open: OnClosing awaits this task, and a
            // dialog raised from inside it would hold the close until somebody clicked OK.
            if (_cleanupStarted)
                return;
            MessageBox.Show(this,
                $"Correlated by unique expiry: {result.Matched} matched, " +
                $"{result.AmbiguousExpiries} ambiguous, {result.UnmatchedPersons} unmatched.\n\n" +
                string.Join("\n", result.Notes) +
                $"\n\nThe name map now holds {merged.Count} name(s).\n\n" +
                "This path is PARTIAL: a name is bound only where the expiry is unique on " +
                "both sides, so fobs sharing a timestamp stay unnamed. For complete " +
                "coverage, export the Person list from iVMS and use “Import CSV…”.",
                "Imported from iVMS — partial", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (gen != _accessGen)
                return;
            SetStatus($"iVMS import failed: {Shorten(ex.Message)}");
        }
        finally
        {
            // Raw cardholder-database key material: drop it as soon as the read is done rather
            // than leaving it in a live buffer for the rest of the session.
            Array.Clear(key);
        }
    }

    private void OnBrowseIvmsDb(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the iVMS person database (PersonalManagement)",
            // iVMS's stores are extensionless, so there is no useful pattern to filter on.
            Filter = "All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            IvmsDbBox.Text = dialog.FileName;
    }

    private void OnCacheIvmsKey(object sender, RoutedEventArgs e)
    {
        string typed = IvmsKeyBox.Password;
        if (typed.Length == 0)
        {
            SetStatus("Enter the iVMS database key to cache it.");
            return;
        }

        try
        {
            var install = ResolveIvmsInstall();
            // Validate at the point of entry so a mistyped key fails here, plainly, instead of
            // as an opaque SQLCipher rejection on the next import.
            byte[] raw = IvmsKeyStore.Decode(typed);
            Array.Clear(raw);
            IvmsKeyStore.Store(install.Id, typed);
            SetStatus($"Cached the iVMS database key for {install.Product} " +
                      $"(install '{install.Id}').");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not cache the iVMS database key: {Shorten(ex.Message)}");
        }
        RefreshIdentityStatus();
    }

    private void OnClearIdentityMap(object sender, RoutedEventArgs e)
    {
        string path = IdentityMapStore.DefaultPath;
        if (MessageBox.Show(this,
                $"Delete the cardholder name map?\n\n{path}\n\nThe roster goes back to fob " +
                "numbers only until names are imported again.",
                "DVRTool", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                SetStatus($"Deleted the name map at {path}.");
            }
            else
            {
                SetStatus($"No name map to delete (none at {path}).");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not delete the name map: {Shorten(ex.Message)}");
        }
        RefreshIdentityStatus();
    }

    /// <summary>Shows where the name map lives and what it currently holds.</summary>
    private void RefreshIdentityStatus()
    {
        string path = IdentityMapStore.DefaultPath;
        var map = TryLoadIdentityMap();
        if (map is not null)
        {
            IdentityStatus.Text =
                $"{map.Count} name(s), source “{map.Source}”" +
                (map.CapturedAtUtc is DateTime at ? $", captured {at:yyyy-MM-dd HH:mm:ss}Z" : "") +
                $" — {path}";
        }
        else if (!File.Exists(path))
        {
            IdentityStatus.Text = $"No name map yet. It will be created at {path}.";
        }
        // A map that exists but would not load has already been reported by TryLoadIdentityMap.
    }

    /// <summary>
    /// The cached name map, or null when there is none (or it cannot be read).
    /// </summary>
    /// <remarks>
    /// A broken map is reported on this group's own status line rather than as a panel-read
    /// failure: the roster itself is still valid, it is just fob numbers only.
    /// </remarks>
    private IdentityMap? TryLoadIdentityMap()
    {
        try
        {
            return IdentityMapStore.Load();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            IdentityStatus.Text =
                $"Could not read the name map at {IdentityMapStore.DefaultPath}: " +
                Shorten(ex.Message);
            return null;
        }
    }

    /// <summary>Merges an imported map on top of the cached one (import wins) and saves it.</summary>
    private static IdentityMap MergeIntoIdentityStore(IdentityMap imported)
    {
        var existing = IdentityMapStore.Load();
        var merged = existing is null ? imported : existing.Merge(imported);
        IdentityMapStore.Save(merged);
        return merged;
    }

    /// <summary>
    /// Resolves which iVMS install to act on: the path in the database box (a person DB file or
    /// a UserData root), otherwise the single auto-discovered install.
    /// </summary>
    private IvmsInstall ResolveIvmsInstall()
    {
        string path = IvmsDbBox.Text.Trim();
        if (path.Length > 0)
        {
            if (Directory.Exists(path))
                return IvmsInstall.FromUserData(path);
            // A file path points at the DB itself; the UserData root is two levels up
            // (…\UserData\PersonalManagement.S\PersonalManagement).
            string? personalMgmtDir = Path.GetDirectoryName(path);
            string? userData = personalMgmtDir is null ? null : Path.GetDirectoryName(personalMgmtDir);
            if (userData is { Length: > 0 })
                return IvmsInstall.FromUserData(userData) with { PersonDbPath = path };
            throw new NvrException($"could not derive an iVMS install from '{path}'.");
        }

        var installs = IvmsInstall.Discover().ToList();
        if (installs.Count == 0)
            throw new NvrException(
                "no iVMS/NVMS install found on this machine — point the iVMS database box at " +
                "the person database or its UserData root.");
        if (installs.Count > 1)
            throw new NvrException(
                $"multiple iVMS installs found ({string.Join(", ", installs.Select(i => i.Product))}) " +
                "— name one in the iVMS database box.");
        return installs[0];
    }

    // ----- shared plumbing -----

    /// <summary>
    /// Runs one piece of Access-tab work, superseding whatever was running before it.
    /// </summary>
    /// <remarks>
    /// Mirrors the Users tab: the task is tracked so shutdown can wait for the panel clients it
    /// holds to be released, and so no continuation of it ever runs against a closed window.
    /// </remarks>
    private async Task RunAccessWorkAsync(Func<CancellationToken, Task> work)
    {
        if (_cleanupStarted)
            return; // window is closing; don't open clients OnClosing will not see

        _accessCts?.Cancel();
        _accessCts?.Dispose();
        _accessCts = new CancellationTokenSource();
        var task = work(_accessCts.Token);
        _accessTask = task;
        try { await task; }
        catch { /* already reported by the work itself */ }
        finally
        {
            if (ReferenceEquals(task, _accessTask))
                _accessTask = null;
        }
    }

    private bool TryGetPanelSettings([NotNullWhen(true)] out PanelConnectionSettings? settings)
    {
        settings = null;

        var panels = AccessPanelsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (panels.Count == 0)
        {
            SetStatus("Enter at least one panel address.");
            return false;
        }

        string user = AccessUserBox.Text.Trim();
        if (user.Length == 0)
        {
            SetStatus("Enter the panel username.");
            return false;
        }

        // Never attempt a login without one: these panels lock out the calling source IP
        // after a handful of failures, and iVMS-4200 usually shares that IP.
        if (AccessPassBox.Password.Length == 0)
        {
            SetStatus("Enter the panel password.");
            return false;
        }

        string portText = AccessPortBox.Text.Trim();
        if (!int.TryParse(portText, out int port) || port is < 1 or > 65535)
        {
            SetStatus($"Invalid SDK port '{portText}'.");
            return false;
        }

        string sdkDir = AccessSdkDirBox.Text.Trim();
        settings = new PanelConnectionSettings(
            panels, user, AccessPassBox.Password, port, sdkDir.Length > 0 ? sdkDir : null);
        return true;
    }
}
