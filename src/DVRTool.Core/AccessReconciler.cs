namespace DVRTool.Core;

/// <summary>A card the policy expects to exist on a panel: fob, holder, and the door union.</summary>
public sealed record ExpectedCard(string Fob, string Name, IReadOnlyList<int> Doors);

/// <summary>A fob present on a panel with different doors than the policy expects.</summary>
public sealed record DoorMismatch(
    string Fob, string Name, IReadOnlyList<int> Expected, IReadOnlyList<int> Actual);

/// <summary>How one panel's live card set diverges from what the policy expects.</summary>
public sealed record PanelDrift
{
    public required string PanelIp { get; init; }

    /// <summary>False when the panel could not be read; drift on it cannot be judged.</summary>
    public bool Read { get; init; }

    /// <summary>Expected and active nowhere / revoked on this panel.</summary>
    public IReadOnlyList<ExpectedCard> Missing { get; init; } = [];

    /// <summary>Present, but opening a different door set than expected.</summary>
    public IReadOnlyList<DoorMismatch> DoorMismatches { get; init; } = [];

    /// <summary>Active fobs on the panel the policy does not account for (including fobs whose
    /// holder could not be mapped to a name).</summary>
    public IReadOnlyList<string> ExtraFobs { get; init; } = [];

    public int ExpectedCount { get; init; }
    public int LiveCount { get; init; }

    public bool InSync => Read
        && Missing.Count == 0 && DoorMismatches.Count == 0 && ExtraFobs.Count == 0;
}

/// <summary>The full read-only drift report across the fleet.</summary>
public sealed record ReconcileReport
{
    public required IReadOnlyList<PanelDrift> Panels { get; init; }

    /// <summary>Policy members with no fob in the map, or a name shared by several people — not checked.</summary>
    public required IReadOnlyList<string> UnmappedMembers { get; init; }

    /// <summary>Schedule approximations the report is making (non-24/7 groups treated always-on).</summary>
    public required IReadOnlyList<string> ScheduleWarnings { get; init; }

    /// <summary>True when every read panel matches and nothing was left unchecked.</summary>
    public bool InSync => Panels.All(p => p.InSync) && Panels.All(p => p.Read);
}

/// <summary>
/// Compares the policy's expected per-panel card sets against a live enumeration and reports
/// the difference. Read-only by construction — it takes an already-built <see cref="AccessRoster"/>
/// (produced from <c>GetCardsAsync</c>) and never touches a panel itself. This is the safe way
/// to prove the loader and resolver match production without writing anything.
/// </summary>
public static class AccessReconciler
{
    /// <summary>
    /// Expands the policy into the cards it expects on each panel, resolving every distinct
    /// person to a fob through the identity map. A person is the set of group memberships
    /// sharing a personnel GUID (or a name, when no GUID is present); their doors are the union
    /// across all their groups, projected per panel.
    /// </summary>
    /// <returns>
    /// Expected cards keyed by panel IP, plus the names of members that could not be resolved to a
    /// fob (so the caller can report them as "not checked" rather than as drift). A member is
    /// resolvable when their name is unique among policy members; a person who legitimately holds
    /// several fobs then contributes every one of them.
    /// </returns>
    public static (IReadOnlyDictionary<string, List<ExpectedCard>> ByPanel,
                   IReadOnlyList<string> Unmapped)
        BuildExpectation(AccessPolicy policy, IdentityMap? map)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Collapse members to persons: a person may appear in several groups.
        var persons = new Dictionary<string, (AccessMember Member, HashSet<string> Groups)>(
            StringComparer.Ordinal);
        foreach (var group in policy.Groups)
        {
            foreach (var member in group.Members)
            {
                string key = !string.IsNullOrWhiteSpace(member.PersonnelGuid)
                    ? "guid:" + member.PersonnelGuid
                    : "name:" + IdentityMap.NormalizeName(member.Name);
                if (!persons.TryGetValue(key, out var entry))
                    persons[key] = entry = (member, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                entry.Groups.Add(group.Group);
            }
        }

        // Fobs are attributed by name, so several people sharing a name cannot be told apart by
        // name alone. But when everyone sharing a name needs the identical set of doors (e.g. two
        // people both in one "Employees" group), their fobs are interchangeable: each simply needs
        // that door union, and the fob *set* is fully verifiable even though the fob↔person link is
        // not. Only a shared name whose holders need *different* doors is genuinely unresolvable.
        var byPanel = new Dictionary<string, List<ExpectedCard>>(StringComparer.OrdinalIgnoreCase);
        var unmapped = new List<string>();
        foreach (var sameName in persons.Values.GroupBy(p => IdentityMap.NormalizeName(p.Member.Name)))
        {
            var people = sameName.ToList();
            string name = people[0].Member.Name;
            var hits = map?.FindByName(name) ?? [];
            if (hits.Count == 0)
            {
                foreach (var (member, _) in people)
                    unmapped.Add(member.Name);
                continue;
            }
            if (people.Count > 1 && people.Select(p => GrantKey(policy, p.Groups)).Distinct().Count() > 1)
            {
                foreach (var (member, _) in people)
                    unmapped.Add(member.Name + " (shared name)");
                continue;
            }

            // One person (possibly holding a second card), or several with identical door needs:
            // expect every fob this name maps to, each with that shared door union.
            foreach (var grant in policy.ResolveGrants(people[0].Groups))
            {
                if (!byPanel.TryGetValue(grant.PanelIp, out var list))
                    byPanel[grant.PanelIp] = list = [];
                foreach (var hit in hits)
                    list.Add(new ExpectedCard(hit.Fob, name, grant.Doors));
            }
        }

        return (byPanel, unmapped);
    }

    /// <summary>A canonical, comparable signature of a person's per-panel door grants.</summary>
    private static string GrantKey(AccessPolicy policy, HashSet<string> groups) =>
        string.Join("|", policy.ResolveGrants(groups)
            .Select(g => $"{g.PanelIp}:{string.Join(",", g.Doors.OrderBy(d => d))}")
            .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>
    /// Diffs the expected per-panel card sets against the live roster, for the given panels.
    /// </summary>
    public static ReconcileReport Compare(AccessPolicy policy, IdentityMap? map,
        AccessRoster roster, IReadOnlyList<string> panelIps)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var (expectedByPanel, unmapped) = BuildExpectation(policy, map);

        var drifts = new List<PanelDrift>();
        foreach (var panelIp in panelIps)
        {
            var expected = expectedByPanel.TryGetValue(panelIp, out var e) ? e : [];
            var panel = roster.Panels.FirstOrDefault(p => HostEquals(p.PanelHost, panelIp));

            if (panel is null || !panel.Ok)
            {
                drifts.Add(new PanelDrift
                {
                    PanelIp = panelIp,
                    Read = false,
                    ExpectedCount = expected.Count,
                });
                continue;
            }

            // Only active cards count as "present": a revoked fob is not access.
            var live = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
            foreach (var card in panel.Cards.Where(c => c.Valid))
                live[AccessRoster.NormalizeCardNo(card.CardNo)] = card.Doors;

            var missing = new List<ExpectedCard>();
            var mismatches = new List<DoorMismatch>();
            var expectedKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var exp in expected)
            {
                string key = AccessRoster.NormalizeCardNo(exp.Fob);
                expectedKeys.Add(key);
                if (!live.TryGetValue(key, out var actualDoors))
                {
                    missing.Add(exp);
                    continue;
                }
                if (!DoorsEqual(exp.Doors, actualDoors))
                    mismatches.Add(new DoorMismatch(
                        exp.Fob, exp.Name,
                        exp.Doors.OrderBy(d => d).ToList(),
                        actualDoors.OrderBy(d => d).ToList()));
            }

            var extra = live.Keys.Where(k => !expectedKeys.Contains(k)).ToList();

            drifts.Add(new PanelDrift
            {
                PanelIp = panelIp,
                Read = true,
                Missing = missing,
                DoorMismatches = mismatches,
                ExtraFobs = extra,
                ExpectedCount = expected.Count,
                LiveCount = live.Count,
            });
        }

        var warnings = policy.Groups
            .Where(g => !g.Is24x7)
            .Select(g =>
                $"group '{g.Group}' schedule \"{g.Schedule}\" is not 24/7 — reconcile treats its " +
                "doors as always-on, so it cannot flag a time-window drift on them.")
            .ToList();

        return new ReconcileReport
        {
            Panels = drifts,
            UnmappedMembers = unmapped,
            ScheduleWarnings = warnings,
        };
    }

    private static bool DoorsEqual(IEnumerable<int> a, IEnumerable<int> b) =>
        new HashSet<int>(a).SetEquals(b);

    /// <summary>
    /// Matches a policy panel IP against a roster panel label, tolerating a <c>:port</c> suffix
    /// on the label (the roster keys by the port-qualified label; the policy uses bare IPs).
    /// </summary>
    private static bool HostEquals(string panelHost, string panelIp)
    {
        if (string.Equals(panelHost, panelIp, StringComparison.OrdinalIgnoreCase))
            return true;
        int colon = panelHost.IndexOf(':');
        return colon > 0 &&
            string.Equals(panelHost[..colon], panelIp, StringComparison.OrdinalIgnoreCase);
    }
}
