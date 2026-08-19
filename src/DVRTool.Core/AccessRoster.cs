namespace DVRTool.Core;

/// <summary>
/// One panel's contribution to a roster: either the cards it reported, or why it didn't.
/// </summary>
/// <remarks>
/// A failed panel is carried explicitly rather than dropped. Silently omitting a panel
/// that could not be read would make every card on it look revoked — the exact wrong
/// answer for an offboarding check, where "no access found" is the conclusion that ends
/// the investigation.
/// </remarks>
public sealed record AccessPanelResult
{
    public required string PanelHost { get; init; }
    public string? Serial { get; init; }
    public IReadOnlyList<AccessCard> Cards { get; init; } = [];

    /// <summary>Null on success; the failure reason otherwise.</summary>
    public string? Error { get; init; }

    public bool Ok => Error is null;

    public static AccessPanelResult Failed(string panelHost, string error) =>
        new() { PanelHost = panelHost, Error = error };
}

/// <summary>Where one credential is provisioned on one panel.</summary>
public sealed record PanelPresence(
    string PanelHost,
    IReadOnlyList<int> Doors,
    bool Valid,
    DateTime? ValidUntil);

/// <summary>One credential, unified across every panel it appears on.</summary>
public sealed record RosterEntry
{
    public required string CardNo { get; init; }

    /// <summary>
    /// Cardholder name if any panel reported one. Null when no panel stores identity
    /// (the normal case on DS-K2604 V2.0 firmware).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Cardholder's organization/department, when a name enrichment supplied one. Always
    /// null from a panel read — the controllers store no identity at all — so this is only
    /// ever populated by <see cref="AccessRoster.EnrichWith"/> from an iVMS import.
    /// </summary>
    public string? Organization { get; init; }

    public required IReadOnlyList<PanelPresence> Presence { get; init; }

    public IEnumerable<string> Panels => Presence.Select(p => p.PanelHost);

    /// <summary>True when every presence is revoked — the card exists but opens nothing.</summary>
    public bool FullyRevoked => Presence.Count > 0 && Presence.All(p => !p.Valid);

    public int TotalDoors => Presence.Where(p => p.Valid).Sum(p => p.Doors.Count);
}

/// <summary>
/// A fleet-wide view of the credentials on a mixture of access panels, built from
/// per-panel reads. Pure aggregation — no I/O — so it is unit-testable and identical
/// whether the reads came from the CLI, the GUI, or a test fixture.
/// </summary>
public sealed record AccessRoster
{
    public required IReadOnlyList<AccessPanelResult> Panels { get; init; }
    public required IReadOnlyList<RosterEntry> Entries { get; init; }

    public IEnumerable<AccessPanelResult> FailedPanels => Panels.Where(p => !p.Ok);

    /// <summary>True when at least one panel could not be read, so the roster is partial.</summary>
    public bool IsPartial => Panels.Any(p => !p.Ok);

    public static AccessRoster Build(IEnumerable<AccessPanelResult> results)
    {
        var panels = results.ToList();

        // Card numbers are the join key. Compare them numerically when both sides are
        // plain integers so "0123" and "123" unify — the device treats those as the same
        // card (it refuses to hold both), and a textual join would report a phantom
        // "missing from panel B".
        var groups = new Dictionary<string, List<AccessCard>>(StringComparer.Ordinal);
        foreach (var panel in panels)
        {
            foreach (var card in panel.Cards)
            {
                string key = NormalizeCardNo(card.CardNo);
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = [];
                list.Add(card with { PanelHost = card.PanelHost ?? panel.PanelHost });
            }
        }

        var entries = new List<RosterEntry>(groups.Count);
        foreach (var (_, cards) in groups)
        {
            entries.Add(new RosterEntry
            {
                // Report the number as the device spells it, not the normalized key.
                CardNo = cards[0].CardNo,
                Name = cards.Select(c => c.Name)
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                Presence = cards
                    .Select(c => new PanelPresence(c.PanelHost!, c.Doors, c.Valid, c.ValidUntil))
                    .OrderBy(p => p.PanelHost, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            });
        }

        return new AccessRoster
        {
            Panels = panels,
            Entries = entries
                .OrderBy(e => SortKey(e.CardNo))
                .ThenBy(e => e.CardNo, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    /// <summary>Look one fob up across the fleet.</summary>
    public RosterEntry? FindCard(string cardNo)
    {
        string key = NormalizeCardNo(cardNo);
        return Entries.FirstOrDefault(e => NormalizeCardNo(e.CardNo) == key);
    }

    /// <summary>
    /// Substring, case-insensitive search over cardholder names. Returns nothing when no
    /// panel stores names — callers must distinguish that from "this person has no access"
    /// via <see cref="AnyPanelStoresNames"/>.
    /// </summary>
    public IReadOnlyList<RosterEntry> FindByName(string query) =>
        Entries.Where(e => e.Name is not null &&
                           e.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public bool AnyPanelStoresNames => Entries.Any(e => e.Name is not null);

    /// <summary>
    /// True once any entry carries a name, whether a panel reported it or an iVMS import
    /// filled it. This is the post-enrichment signal the CLI gates <c>find --name</c> on;
    /// <see cref="AnyPanelStoresNames"/> stays the pre-enrichment "did a *panel* know a name"
    /// question and is computed identically — the distinction is which roster you ask.
    /// </summary>
    public bool HasNames => Entries.Any(e => e.Name is not null);

    /// <summary>
    /// Returns a new roster with cardholder names (and organizations) filled in from an
    /// iVMS import, joined on fob number. Pure — no I/O, panels untouched.
    /// </summary>
    /// <remarks>
    /// A panel-supplied name always wins: enrichment only fills entries a panel left blank,
    /// so an authoritative on-device identity is never overwritten by an imported guess. The
    /// map is one-way (iVMS → DVRTool); nothing here writes back toward iVMS or a panel.
    /// </remarks>
    public AccessRoster EnrichWith(IdentityMap map)
    {
        var enriched = Entries.Select(entry =>
        {
            if (!string.IsNullOrWhiteSpace(entry.Name))
                return entry;
            var identity = map.Lookup(entry.CardNo);
            if (identity is null)
                return entry;
            return entry with { Name = identity.Name, Organization = identity.Organization };
        }).ToList();

        return this with { Entries = enriched };
    }

    /// <summary>Cards on <paramref name="panelHost"/> that are absent from every other panel.</summary>
    public IReadOnlyList<RosterEntry> OnlyOn(string panelHost) =>
        Entries.Where(e => e.Presence.Count == 1 &&
                           string.Equals(e.Presence[0].PanelHost, panelHost,
                               StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Cards present on <paramref name="expectedOn"/> but missing from
    /// <paramref name="panelHost"/> — the drift view behind a fleet compare.
    /// </summary>
    public IReadOnlyList<RosterEntry> MissingFrom(string panelHost, string expectedOn) =>
        Entries.Where(e => e.Panels.Contains(expectedOn, StringComparer.OrdinalIgnoreCase) &&
                           !e.Panels.Contains(panelHost, StringComparer.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Card numbers are compared as integers when both are purely numeric, because the
    /// device does: it rejects a batch containing both "1" and "01".
    /// </summary>
    public static string NormalizeCardNo(string cardNo)
    {
        string trimmed = cardNo.Trim();
        if (trimmed.Length == 0)
            return trimmed;
        if (!trimmed.All(char.IsAsciiDigit))
            return trimmed.ToUpperInvariant();
        string stripped = trimmed.TrimStart('0');
        return stripped.Length == 0 ? "0" : stripped;
    }

    // Numeric fobs sort numerically; anything else sorts after them.
    private static (int Rank, long Value) SortKey(string cardNo)
    {
        string key = NormalizeCardNo(cardNo);
        return key.Length > 0 && key.All(char.IsAsciiDigit) && long.TryParse(key, out long v)
            ? (0, v)
            : (1, 0);
    }
}
