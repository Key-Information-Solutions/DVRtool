namespace DVRTool.Core;

/// <summary>One panel a revoke would actually write to, and what it takes away there.</summary>
/// <param name="Doors">
/// The doors the fob opens on this panel today — the thing the operator is about to lose, and
/// so the thing a confirmation has to name. Rights are per-panel, so this differs panel by panel.
/// </param>
public sealed record RevokeTarget(string PanelHost, IReadOnlyList<int> Doors)
{
    public string DoorSummary => Doors.Count == 0 ? "(none)" : string.Join(",", Doors);
}

/// <summary>
/// What revoking one fob across a fleet would do: the panels to write, the panels that already
/// hold it revoked, and the panels nobody could read. Pure — built from an
/// <see cref="AccessRoster"/> and no I/O — so the CLI's <c>--force</c> prompt and the GUI's
/// confirmation dialog describe the same write from the same arithmetic.
/// </summary>
/// <remarks>
/// The unreadable panels are the point of the type. A revoke is asked for because somebody must
/// stop being able to open a door, and a panel that did not answer is a panel where the fob may
/// still work — so "revoked everywhere" is a conclusion that can only be drawn from a complete
/// read, and this plan refuses to let a caller state it otherwise.
/// </remarks>
public sealed record CardRevokePlan
{
    /// <summary>The fob as the operator asked for it.</summary>
    public required string CardNo { get; init; }

    /// <summary>The cardholder's name, when the roster knew one. Never read from a panel.</summary>
    public string? Name { get; init; }

    /// <summary>Panels holding the fob active: every one of these is a write.</summary>
    public required IReadOnlyList<RevokeTarget> Revokes { get; init; }

    /// <summary>
    /// Panels that hold the fob but already have it revoked — nothing to do there.
    /// </summary>
    /// <remarks>
    /// Near-dead on DS-K firmware, where a revoke deletes: the record vanishes from the
    /// enumeration rather than lingering as invalid (see
    /// <c>docs/hikvision-access-control-findings.md</c> §5a). It is kept for the panels that
    /// do deactivate in place, and because a card read back as present-and-invalid is still
    /// a card that opens nothing.
    /// </remarks>
    public required IReadOnlyList<string> AlreadyRevoked { get; init; }

    /// <summary>Panels that could not be read, with the reason. The fob may still be live on these.</summary>
    public required IReadOnlyList<AccessPanelResult> Unreadable { get; init; }

    /// <summary>True when the fob is not on any panel that answered — readable or not.</summary>
    public bool NotFound => Revokes.Count == 0 && AlreadyRevoked.Count == 0;

    /// <summary>True when there is a write to make.</summary>
    public bool HasWork => Revokes.Count > 0;

    /// <summary>True when at least one panel did not answer, so this plan is not the whole fleet.</summary>
    public bool IsPartial => Unreadable.Count > 0;

    /// <summary>
    /// Works out what revoking <paramref name="cardNo"/> would touch, given a roster that has
    /// just been read.
    /// </summary>
    /// <remarks>
    /// Matched through <see cref="AccessRoster.NormalizeCardNo"/>, so an operator who types
    /// "0123" for the fob a panel spells "123" revokes the card they mean — the devices treat
    /// those as one card and refuse to hold both.
    /// </remarks>
    public static CardRevokePlan For(AccessRoster roster, string cardNo)
    {
        string wanted = cardNo.Trim();
        if (wanted.Length == 0)
            throw new ArgumentException("a revoke needs a fob number.", nameof(cardNo));

        var entry = roster.FindCard(wanted);
        var revokes = new List<RevokeTarget>();
        var already = new List<string>();
        if (entry is not null)
        {
            foreach (var presence in entry.Presence)
            {
                if (presence.Valid)
                    revokes.Add(new RevokeTarget(presence.PanelHost, presence.Doors));
                else
                    already.Add(presence.PanelHost);
            }
        }

        return new CardRevokePlan
        {
            // The device's own spelling when it is known, so what is reported back is what the
            // panel will be asked for.
            CardNo = entry?.CardNo ?? wanted,
            Name = entry?.Name,
            Revokes = revokes,
            AlreadyRevoked = already,
            Unreadable = roster.FailedPanels.ToList(),
        };
    }
}
