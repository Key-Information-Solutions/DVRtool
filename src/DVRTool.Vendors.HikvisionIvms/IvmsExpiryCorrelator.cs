using DVRTool.Core;

namespace DVRTool.Vendors.HikvisionIvms;

/// <summary>Outcome of a unique-expiry correlation: the map plus honest coverage counts.</summary>
/// <remarks>
/// The counts exist so the CLI can state plainly that this path is PARTIAL. Anything that is
/// not a clean one-to-one expiry match is left unmatched rather than guessed.
/// </remarks>
public sealed record CorrelationResult
{
    public required IdentityMap Map { get; init; }
    public required int Matched { get; init; }
    public required int AmbiguousExpiries { get; init; }
    public required int UnmatchedPersons { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Binds iVMS names to panel fobs by matching validity-expiry timestamps, with no card
/// cipher involved at all.
/// </summary>
/// <remarks>
/// iVMS's <c>PersonnelBasic.ExpireTime</c> and the panel card's <c>ValidUntil</c> are the
/// same per-person value, so a name and a fob can be joined when their expiry is unique on
/// <em>both</em> sides — one person with that timestamp, one card with it. A timestamp shared
/// by two people or two cards is ambiguous and left unmatched; this is explicitly a partial
/// fallback for when the supported CSV export is not available (which gives full coverage).
/// </remarks>
public static class IvmsExpiryCorrelator
{
    private const string Source = "ivms-expiry";

    public static CorrelationResult Correlate(IReadOnlyList<IvmsPerson> persons, AccessRoster roster)
    {
        // Distinct fobs with a known expiry, grouped by that expiry-to-the-second.
        var cardsByExpiry = new Dictionary<DateTime, List<string>>();
        foreach (var entry in roster.Entries)
        {
            var expiry = entry.Presence
                .Select(p => p.ValidUntil)
                .FirstOrDefault(v => v is not null);
            if (expiry is DateTime until)
            {
                DateTime key = Truncate(until);
                if (!cardsByExpiry.TryGetValue(key, out var list))
                    cardsByExpiry[key] = list = [];
                list.Add(entry.CardNo);
            }
        }

        // Persons grouped by expiry too, so a timestamp shared by two people is ambiguous.
        var personsByExpiry = new Dictionary<DateTime, List<IvmsPerson>>();
        int personsWithoutExpiry = 0;
        foreach (var person in persons)
        {
            if (person.ExpireTime is DateTime expire)
            {
                DateTime key = Truncate(expire);
                if (!personsByExpiry.TryGetValue(key, out var list))
                    personsByExpiry[key] = list = [];
                list.Add(person);
            }
            else
            {
                personsWithoutExpiry++;
            }
        }

        var identities = new List<CardholderIdentity>();
        var notes = new List<string>();
        int ambiguous = 0;
        int matchedPersons = 0;

        foreach (var (expiry, people) in personsByExpiry)
        {
            bool cardUnique = cardsByExpiry.TryGetValue(expiry, out var cards) && cards.Count == 1;
            bool personUnique = people.Count == 1;

            if (cardUnique && personUnique)
            {
                var person = people[0];
                identities.Add(new CardholderIdentity
                {
                    Fob = cards![0],
                    Name = person.Name,
                    Organization = person.Organization,
                    ExpiresOn = person.ExpireTime,
                    Source = Source,
                });
                matchedPersons++;
            }
            else if (cardsByExpiry.ContainsKey(expiry))
            {
                // A timestamp that exists on both sides but is not 1:1 either way.
                ambiguous += people.Count;
            }
        }

        int unmatchedPersons = persons.Count - matchedPersons;
        notes.Add($"{matchedPersons} matched on unique expiry, {ambiguous} ambiguous, " +
                  $"{unmatchedPersons} unmatched.");
        if (personsWithoutExpiry > 0)
            notes.Add($"{personsWithoutExpiry} person(s) had no expiry to match on.");

        return new CorrelationResult
        {
            Map = IdentityMap.Build(identities, Source, DateTime.UtcNow),
            Matched = matchedPersons,
            AmbiguousExpiries = ambiguous,
            UnmatchedPersons = unmatchedPersons,
            Notes = notes,
        };
    }

    /// <summary>Compare expiries to the second, dropping any sub-second component.</summary>
    private static DateTime Truncate(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second);
}
