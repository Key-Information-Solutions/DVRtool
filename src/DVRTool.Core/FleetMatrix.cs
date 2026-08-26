namespace DVRTool.Core;

/// <summary>
/// One device's contribution to a fleet user view: either the accounts it reported, or
/// why it didn't. The same carried-failure rule as <see cref="AccessPanelResult"/>:
/// silently omitting an unreadable recorder would make every account on it look deleted.
/// </summary>
public sealed record DeviceUsersResult
{
    public required string DeviceName { get; init; }
    public IReadOnlyList<NvrUser> Users { get; init; } = [];

    /// <summary>Null on success; the failure reason otherwise.</summary>
    public string? Error { get; init; }

    public bool Ok => Error is null;

    public static DeviceUsersResult Failed(string deviceName, string error) =>
        new() { DeviceName = deviceName, Error = error };
}

/// <summary>One account, spread across the devices it was looked for on.</summary>
/// <remarks>
/// <see cref="Cells"/> aligns index-for-index with <see cref="UserMatrix.Devices"/>: the
/// device's native level where the account exists, null where it does not — and null under
/// an unreadable device too, which is why <see cref="Status"/> is computed only over the
/// devices that actually answered.
/// </remarks>
public sealed record UserMatrixRow
{
    public required string User { get; init; }
    public required IReadOnlyList<string?> Cells { get; init; }
    public required string Status { get; init; }
}

/// <summary>
/// A fleet-wide view of NVR login accounts: one row per account name, one column per
/// device. Pure aggregation — no I/O — like <see cref="AccessRoster"/>, and for the same
/// reason: identical whether the reads came from the GUI, the CLI, or a test fixture.
/// </summary>
public sealed record UserMatrix
{
    public required IReadOnlyList<DeviceUsersResult> Devices { get; init; }
    public required IReadOnlyList<UserMatrixRow> Rows { get; init; }

    public IEnumerable<DeviceUsersResult> FailedDevices => Devices.Where(d => !d.Ok);

    /// <summary>True when at least one device could not be read, so the view is partial.</summary>
    public bool IsPartial => Devices.Any(d => !d.Ok);

    public static UserMatrix Build(IEnumerable<DeviceUsersResult> results)
    {
        var devices = results.ToList();

        // Accounts are paired by name, not by vendor-native id: the ids are numeric on
        // Hikvision and the login name on Dahua, so only the name compares across vendors.
        var cellsByUser = new Dictionary<string, string?[]>(StringComparer.OrdinalIgnoreCase);
        var displayName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < devices.Count; i++)
        {
            if (!devices[i].Ok)
                continue;
            foreach (var user in devices[i].Users)
            {
                if (!cellsByUser.TryGetValue(user.Name, out var cells))
                {
                    cellsByUser[user.Name] = cells = new string?[devices.Count];
                    displayName[user.Name] = user.Name;
                }
                cells[i] ??= user.NativeLevel;
            }
        }

        int readable = devices.Count(d => d.Ok);
        var rows = cellsByUser
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new UserMatrixRow
            {
                User = displayName[pair.Key],
                Cells = pair.Value,
                Status = StatusOf(devices, pair.Value, readable),
            })
            .ToList();

        return new UserMatrix { Devices = devices, Rows = rows };
    }

    /// <summary>
    /// The comparison verdict, over the devices that answered. A device that could not be
    /// read contributes nothing: "missing on X" from a failed read would be the exact wrong
    /// answer, since the account may sit there untouched.
    /// </summary>
    private static string StatusOf(
        IReadOnlyList<DeviceUsersResult> devices, string?[] cells, int readable)
    {
        // One readable device is a listing, not a comparison — the cells say everything.
        if (readable <= 1)
            return "";

        var missing = new List<string>();
        var levels = new List<string>();
        for (int i = 0; i < devices.Count; i++)
        {
            if (!devices[i].Ok)
                continue;
            if (cells[i] is { } level)
                levels.Add(level);
            else
                missing.Add(devices[i].DeviceName);
        }

        if (missing.Count > 0)
            return $"Missing on {string.Join(", ", missing)}";
        return levels.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
            ? "Level differs"
            : $"On all ({readable})";
    }
}

/// <summary>One credential, spread across the panels it was looked for on.</summary>
/// <remarks>
/// <see cref="Cells"/> aligns index-for-index with <see cref="AccessMatrix.Panels"/>: the
/// door list where the card is provisioned ("REVOKED" where it exists but opens nothing),
/// null where the panel has no such card — and null under an unreadable panel, excluded
/// from <see cref="Status"/> for the same reason as in <see cref="UserMatrixRow"/>.
/// </remarks>
public sealed record AccessMatrixRow
{
    public required string CardNo { get; init; }
    public string? Name { get; init; }
    public required IReadOnlyList<string?> Cells { get; init; }
    public required string Status { get; init; }
}

/// <summary>
/// <see cref="AccessRoster"/> pivoted into a fleet matrix: one row per fob, one column per
/// panel. The roster's flattened per-presence rows answer "what exactly does this fob open
/// here"; this answers "who exists where" across a site's controllers at a glance.
/// </summary>
public sealed record AccessMatrix
{
    public required IReadOnlyList<AccessPanelResult> Panels { get; init; }
    public required IReadOnlyList<AccessMatrixRow> Rows { get; init; }

    public IEnumerable<AccessPanelResult> FailedPanels => Panels.Where(p => !p.Ok);

    public bool IsPartial => Panels.Any(p => !p.Ok);

    /// <param name="displayName">
    /// Optional rename for status prose (a saved panel's record name instead of its
    /// <c>ip[:port]</c> label). Display only — joining stays on the port-qualified label,
    /// which is what every card is stamped with.
    /// </param>
    public static AccessMatrix Build(AccessRoster roster, Func<string, string>? displayName = null)
    {
        displayName ??= label => label;
        var panels = roster.Panels;
        int readable = panels.Count(p => p.Ok);

        var rows = new List<AccessMatrixRow>(roster.Entries.Count);
        foreach (var entry in roster.Entries)
        {
            var presence = new Dictionary<string, PanelPresence>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in entry.Presence)
                presence.TryAdd(p.PanelHost, p);

            var cells = new string?[panels.Count];
            var missing = new List<string>();
            var revoked = new List<string>();
            for (int i = 0; i < panels.Count; i++)
            {
                if (!panels[i].Ok)
                    continue;
                if (!presence.TryGetValue(panels[i].PanelHost, out var at))
                {
                    missing.Add(displayName(panels[i].PanelHost));
                    continue;
                }
                if (!at.Valid)
                {
                    cells[i] = "REVOKED";
                    revoked.Add(displayName(panels[i].PanelHost));
                    continue;
                }
                cells[i] = at.Doors.Count == 0 ? "(none)" : string.Join(",", at.Doors);
            }

            rows.Add(new AccessMatrixRow
            {
                CardNo = entry.CardNo,
                Name = entry.Name,
                Cells = cells,
                Status = StatusOf(missing, revoked, readable),
            });
        }

        return new AccessMatrix { Panels = panels, Rows = rows };
    }

    private static string StatusOf(List<string> missing, List<string> revoked, int readable)
    {
        // One readable panel is a listing, not a comparison.
        if (readable <= 1)
            return "";
        if (missing.Count == 0 && revoked.Count == readable)
            return "Revoked everywhere";

        var parts = new List<string>();
        if (missing.Count > 0)
            parts.Add($"Missing on {string.Join(", ", missing)}");
        if (revoked.Count > 0)
            parts.Add($"REVOKED on {string.Join(", ", revoked)}");
        return parts.Count == 0 ? $"On all ({readable})" : string.Join("; ", parts);
    }
}
