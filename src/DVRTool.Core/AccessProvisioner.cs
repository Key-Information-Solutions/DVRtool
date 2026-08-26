namespace DVRTool.Core;

/// <summary>One panel write an onboard would perform: the card to upsert on that panel.</summary>
public sealed record PlannedCardWrite(string PanelIp, AccessCard Card);

/// <summary>
/// The exact set of per-panel writes an onboard resolves to, plus the validity window and any
/// schedule approximations. Computed purely from the policy and the inputs — no panel contact —
/// so it is what <c>--dry-run</c> prints and what the unit tests assert against.
/// </summary>
public sealed record OnboardPlan
{
    public required string Name { get; init; }
    public required string Fob { get; init; }
    public required IReadOnlyList<string> Groups { get; init; }
    public required DateTime ValidFrom { get; init; }
    public required DateTime ValidUntil { get; init; }
    public required IReadOnlyList<PlannedCardWrite> Writes { get; init; }

    /// <summary>Warnings for groups whose schedule is not 24/7 and is being approximated always-on.</summary>
    public required IReadOnlyList<string> ScheduleWarnings { get; init; }

    public IEnumerable<string> Panels => Writes.Select(w => w.PanelIp);
}

/// <summary>The set of revokes an offboard resolves to: one fob, removed from every panel.</summary>
public sealed record OffboardPlan
{
    public required string Name { get; init; }
    public required string Fob { get; init; }

    /// <summary>Every panel to revoke on — the whole fleet, since a stale fob can linger anywhere.</summary>
    public required IReadOnlyList<string> PanelRevokes { get; init; }
}

/// <summary>
/// Turns an onboard/offboard request into the concrete panel operations it implies, using the
/// policy resolver and the fob↔name map. Pure: it plans, it does not write. The CLI executes a
/// plan (gated on <c>--force</c>); the tests assert the plan.
/// </summary>
public static class AccessProvisioner
{
    /// <summary>
    /// Default validity window when the operator gives no <c>--valid-until</c>. iVMS provisioned
    /// ~10-year windows; a bounded-but-long window is deliberate — an unbounded fob is exactly
    /// what offboarding cannot rely on cleaning up.
    /// </summary>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(3653); // ~10 years

    /// <summary>
    /// Plans an onboard: resolve the group union per panel, and build one active
    /// <see cref="AccessCard"/> per panel carrying that panel's door union and a bounded validity
    /// window. Grants are always plan-1 (24/7); non-24/7 groups surface a warning rather than a
    /// silent approximation.
    /// </summary>
    /// <param name="validFrom">Window start; defaults to <see cref="DateTime.Now"/> (panel-local).</param>
    /// <param name="validUntil">Window end; defaults to <paramref name="validFrom"/> + <see cref="DefaultValidity"/>.</param>
    public static OnboardPlan PlanOnboard(AccessPolicy policy, string name, string fob,
        IReadOnlyList<string> groups, DateTime? validFrom = null, DateTime? validUntil = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("onboard needs a name");
        if (string.IsNullOrWhiteSpace(fob))
            throw new ArgumentException("onboard needs a physical fob number (--card)");
        if (groups is null || groups.Count == 0)
            throw new ArgumentException("onboard needs at least one --group");

        // Resolve (validates unknown group names) and compute the per-panel union.
        var resolvedGroups = policy.ResolveGroups(groups);
        var grants = policy.ResolveGrants(groups);

        DateTime from = validFrom ?? DateTime.Now;
        DateTime until = validUntil ?? from + DefaultValidity;
        if (until <= from)
            throw new ArgumentException("the validity window must end after it starts");

        string trimmedFob = fob.Trim();
        var writes = grants.Select(grant => new PlannedCardWrite(grant.PanelIp, new AccessCard
        {
            CardNo = trimmedFob,
            Valid = true,
            Type = AccessCardType.Normal,
            Doors = grant.Doors,
            ValidFrom = from,
            ValidUntil = until,
            PanelHost = grant.PanelIp,
        })).ToList();

        var warnings = resolvedGroups
            .Where(g => !g.Is24x7)
            .Select(g =>
                $"group '{g.Group}' schedule \"{g.Schedule}\" is NOT 24/7 — its time restriction " +
                "is approximated as always-on (right-plan 1). A faithful schedule-template writer " +
                "is a separate task; see the provisioning handoff §4.3.")
            .ToList();

        return new OnboardPlan
        {
            Name = name.Trim(),
            Fob = trimmedFob,
            Groups = resolvedGroups.Select(g => g.Group).ToList(),
            ValidFrom = from,
            ValidUntil = until,
            Writes = writes,
            ScheduleWarnings = warnings,
        };
    }

    /// <summary>
    /// Plans an offboard: resolve the person's name to a fob through the identity map, then
    /// revoke that fob on every panel supplied. Resolution is by name because the panels store
    /// none themselves — the fob is the only handle a departed employee leaves behind.
    /// </summary>
    /// <exception cref="NvrException">
    /// No map, no fob for the name, or the name maps to more than one fob (revoke each explicitly).
    /// </exception>
    public static OffboardPlan PlanOffboard(string name, IdentityMap? map,
        IReadOnlyList<string> panelIps)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("offboard needs a name");
        if (panelIps is null || panelIps.Count == 0)
            throw new ArgumentException("offboard needs the panels to revoke across");

        if (map is null)
            throw new NvrException(
                "no cardholder-name map — the panels store no names, so an offboard by name " +
                "needs one imported first (`dvrtool access identity --import-ivms`). Otherwise " +
                "offboard by fob with `dvrtool access revoke --card <fob>`.");

        var hits = map.FindByName(name);
        if (hits.Count == 0)
            throw new NvrException(
                $"no fob is mapped to '{name}'. Check the name against the imported map " +
                "(`access identity --where`), or revoke by fob with `access revoke --card <fob>`.");
        if (hits.Count > 1)
            throw new NvrException(
                $"'{name}' maps to {hits.Count} fobs ({string.Join(", ", hits.Select(h => h.Fob))}) — " +
                "offboard each one explicitly with `access revoke --card <fob>`.");

        return new OffboardPlan
        {
            Name = name.Trim(),
            Fob = hits[0].Fob,
            PanelRevokes = panelIps.ToList(),
        };
    }
}
