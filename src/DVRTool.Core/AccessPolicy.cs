using System.Text.Json;
using System.Text.Json.Serialization;

namespace DVRTool.Core;

/// <summary>
/// One door on one panel, exactly as the iVMS-extracted policy names it.
/// </summary>
/// <remarks>
/// The join key is <see cref="PanelIp"/>, never <see cref="PanelName"/>. iVMS panel names
/// are not in IP order on the live fleet (ocb2 = .223, ocb3 = .222), so anything that infers
/// an address from the name is wrong. The policy carries the IP per door precisely so the
/// writer can drive off it.
/// </remarks>
public sealed record AccessDoor
{
    [JsonPropertyName("panelName")] public string PanelName { get; init; } = "";
    [JsonPropertyName("panelIp")] public required string PanelIp { get; init; }
    [JsonPropertyName("doorNo")] public required int DoorNo { get; init; }
    [JsonPropertyName("doorName")] public string DoorName { get; init; } = "";
}

/// <summary>
/// One member of an access group. <see cref="Name"/> is cardholder PII — it lives in the
/// gitignored policy artifact and must never be committed or logged into the repo.
/// </summary>
public sealed record AccessMember
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("employeeNo")] public string? EmployeeNo { get; init; }
    [JsonPropertyName("personnelGuid")] public string? PersonnelGuid { get; init; }
}

/// <summary>
/// One access group as extracted from iVMS: the doors it opens, its schedule descriptor, and
/// its members. This is the unit DVRTool reproduces by writing directly to the panels.
/// </summary>
public sealed record AccessGroupPolicy
{
    [JsonPropertyName("group")] public required string Group { get; init; }
    [JsonPropertyName("groupGuid")] public string? GroupGuid { get; init; }
    [JsonPropertyName("scheduleGuid")] public string? ScheduleGuid { get; init; }

    /// <summary>
    /// Raw schedule descriptor, e.g. <c>"(default) = 00:00:00;24:00:00;FFFF;FFFF"</c>. Not
    /// interpreted beyond the 24/7 test — see <see cref="Is24x7"/> and the schedule note in
    /// <c>docs/hikvision-access-provisioning-handoff.md</c> §4.3.
    /// </summary>
    [JsonPropertyName("schedule")] public string? Schedule { get; init; }

    [JsonPropertyName("memberCount")] public int MemberCount { get; init; }
    [JsonPropertyName("doors")] public IReadOnlyList<AccessDoor> Doors { get; init; } = [];
    [JsonPropertyName("members")] public IReadOnlyList<AccessMember> Members { get; init; } = [];

    /// <summary>
    /// True when the schedule is 24/7 — the only case this build can reproduce faithfully,
    /// because every Site A card carries the panels' 24/7 template (<c>DefaultRightPlan = 1</c>).
    /// A group that is not 24/7 is written always-on with a warning, never silently.
    /// </summary>
    [JsonIgnore] public bool Is24x7 => AccessSchedule.Is24x7(Schedule);
}

/// <summary>The per-panel door union a set of groups resolves to.</summary>
public sealed record PanelGrant(string PanelIp, IReadOnlyList<int> Doors);

/// <summary>
/// The access-provisioning policy: which panels/doors each group opens, extracted once from
/// iVMS so DVRTool can reproduce iVMS's decisions without iVMS being in the runtime flow
/// (handoff Path B). Pure and immutable — no I/O beyond <see cref="Load"/>.
/// </summary>
public sealed record AccessPolicy
{
    public required IReadOnlyList<AccessGroupPolicy> Groups { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads and validates the policy JSON at <paramref name="path"/>.</summary>
    /// <exception cref="ArgumentException">The path is blank.</exception>
    /// <exception cref="NvrException">The file is missing, unreadable, or malformed.</exception>
    public static AccessPolicy Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("policy path is required");
        if (!File.Exists(path))
            throw new NvrException(
                $"access policy not found: {path}. Point --policy at " +
                "access-control-policy.json (see the provisioning handoff §2).");

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new NvrException($"could not read access policy {path}: {ex.Message}", inner: ex);
        }
        return Parse(json);
    }

    /// <summary>Parses and validates a policy from a JSON string (the array-of-groups shape).</summary>
    public static AccessPolicy Parse(string json)
    {
        List<AccessGroupPolicy>? groups;
        try
        {
            groups = JsonSerializer.Deserialize<List<AccessGroupPolicy>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new NvrException($"access policy is not valid JSON: {ex.Message}", inner: ex);
        }

        if (groups is null || groups.Count == 0)
            throw new NvrException("access policy is empty — expected an array of groups.");

        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.Group))
                throw new NvrException("access policy has a group with no name.");
            foreach (var door in group.Doors)
            {
                if (string.IsNullOrWhiteSpace(door.PanelIp))
                    throw new NvrException(
                        $"group '{group.Group}' has a door with no panelIp — the writer drives " +
                        "off the IP, so it cannot be inferred.");
                if (door.DoorNo < 1)
                    throw new NvrException(
                        $"group '{group.Group}' has an invalid door number {door.DoorNo}.");
            }
        }

        return new AccessPolicy { Groups = groups };
    }

    /// <summary>The distinct panels the policy references, as (ip, name) pairs, ip-ordered.</summary>
    public IReadOnlyList<(string PanelIp, string PanelName)> Panels =>
        Groups.SelectMany(g => g.Doors)
            .GroupBy(d => d.PanelIp, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.First().PanelName))
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The group with this name (case-insensitive, trimmed), or null.</summary>
    public AccessGroupPolicy? FindGroup(string name) =>
        Groups.FirstOrDefault(g =>
            string.Equals(g.Group.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a set of group names to the matched group policies, erroring on any unknown
    /// name. The lookup is case-insensitive; the error names the groups that do exist.
    /// </summary>
    /// <exception cref="ArgumentException">No group names were supplied.</exception>
    /// <exception cref="NvrException">A name matches no group.</exception>
    public IReadOnlyList<AccessGroupPolicy> ResolveGroups(IEnumerable<string> groupNames)
    {
        var names = (groupNames ?? throw new ArgumentException("at least one group is required"))
            .Select(n => n?.Trim() ?? "")
            .Where(n => n.Length > 0)
            .ToList();
        if (names.Count == 0)
            throw new ArgumentException("at least one group is required");

        var resolved = new List<AccessGroupPolicy>();
        foreach (var name in names)
        {
            var group = FindGroup(name)
                ?? throw new NvrException(
                    $"unknown access group '{name}'. Known groups: " +
                    $"{string.Join(", ", Groups.Select(g => g.Group))}.");
            if (!resolved.Contains(group))
                resolved.Add(group);
        }
        return resolved;
    }

    /// <summary>
    /// The per-panel door UNION for a set of groups. This is the heart of provisioning: a
    /// person in several groups gets, on each panel, every door any of their groups opens
    /// there. Driven off <see cref="AccessDoor.PanelIp"/> because the panel names are not in
    /// IP order.
    /// </summary>
    /// <returns>One <see cref="PanelGrant"/> per panel, ip-ordered, doors ascending.</returns>
    public IReadOnlyList<PanelGrant> ResolveGrants(IEnumerable<string> groupNames)
    {
        var byPanel = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in ResolveGroups(groupNames))
        {
            foreach (var door in group.Doors)
            {
                if (!byPanel.TryGetValue(door.PanelIp, out var doors))
                    byPanel[door.PanelIp] = doors = [];
                doors.Add(door.DoorNo);
            }
        }

        return byPanel
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new PanelGrant(kv.Key, kv.Value.ToList()))
            .ToList();
    }
}

/// <summary>
/// Interprets an iVMS schedule descriptor just far enough to answer "is this 24/7?".
/// </summary>
/// <remarks>
/// The descriptor looks like <c>"(default) = 00:00:00;24:00:00;FFFF;FFFF"</c>: a label, then
/// begin;end;weekMask;weekMask. 24/7 is a full day (00:00:00–24:00:00) on every day (both
/// masks all-F). Anything else — the live "Group C" group begins 00:02:00 — is not 24/7,
/// which is all this build needs to know to warn that it is approximating the restriction.
/// </remarks>
public static class AccessSchedule
{
    public static bool Is24x7(string? schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule))
            return false;

        int eq = schedule.IndexOf('=');
        string body = (eq >= 0 ? schedule[(eq + 1)..] : schedule).Trim();
        var parts = body.Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
            return false;

        return parts[0] == "00:00:00"
            && parts[1] == "24:00:00"
            && IsAllF(parts[2])
            && IsAllF(parts[3]);
    }

    private static bool IsAllF(string mask) =>
        mask.Length > 0 && mask.All(c => c is 'F' or 'f');
}
