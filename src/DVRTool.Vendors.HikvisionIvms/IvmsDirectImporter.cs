using DVRTool.Core;

namespace DVRTool.Vendors.HikvisionIvms;

/// <summary>Outcome of a direct iVMS import: the map plus honest coverage counts.</summary>
public sealed record DirectImportResult
{
    public required IdentityMap Map { get; init; }

    /// <summary>Distinct persons seen.</summary>
    public required int Persons { get; init; }

    /// <summary>Card rows with an encoded number (a person may hold more than one).</summary>
    public required int CardsSeen { get; init; }

    /// <summary>Fobs decoded and bound to a name.</summary>
    public required int Decoded { get; init; }

    /// <summary>Persons who held ≥1 card but whose card(s) could not be decoded.</summary>
    public required IReadOnlyList<IvmsPerson> Undecodable { get; init; }

    /// <summary>Persons with no card row at all — they simply hold no fob.</summary>
    public required int WithoutCard { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Builds a complete fob↔name map by decoding iVMS's card field directly with
/// <see cref="IvmsCardCipher"/> — the panel-free, collision-free path.
/// </summary>
/// <remarks>
/// Unlike <see cref="IvmsExpiryCorrelator"/> (which needs the live panels and only binds fobs
/// whose expiry is unique on both sides), this reads name↔fob straight from the person DB. A
/// card the decoder cannot resolve exactly is never guessed: its person is reported as
/// undecodable so the caller can fall back to expiry correlation or resolve it by hand.
/// </remarks>
public static class IvmsDirectImporter
{
    private const string Source = "ivms-db";

    public static DirectImportResult Build(IReadOnlyList<IvmsCardRow> rows)
    {
        var byPerson = rows
            .GroupBy(r => r.PersonnelGuid, StringComparer.Ordinal)
            .ToList();

        var identities = new List<CardholderIdentity>();
        var undecodable = new List<IvmsPerson>();
        int cardsSeen = 0;
        int withoutCard = 0;

        foreach (var group in byPerson)
        {
            var cards = group.Where(r => !string.IsNullOrWhiteSpace(r.CardNoEncoded)).ToList();
            if (cards.Count == 0)
            {
                withoutCard++;
                continue;
            }

            bool anyDecoded = false;
            foreach (var row in cards)
            {
                cardsSeen++;
                if (!IvmsCardCipher.TryDecode(row.CardNoEncoded, out string fob))
                    continue;
                anyDecoded = true;
                identities.Add(new CardholderIdentity
                {
                    Fob = fob,
                    Name = row.Name,
                    Organization = row.Organization,
                    ExpiresOn = row.ExpireTime,
                    Source = Source,
                });
            }

            if (!anyDecoded)
            {
                var first = cards[0];
                undecodable.Add(new IvmsPerson
                {
                    PersonnelGuid = first.PersonnelGuid,
                    Name = first.Name,
                    Organization = first.Organization,
                    ExpireTime = first.ExpireTime,
                });
            }
        }

        var map = IdentityMap.Build(identities, Source, DateTime.UtcNow);

        var notes = new List<string>
        {
            $"{map.Count} fob(s) decoded for {byPerson.Count} person(s); " +
            $"{undecodable.Count} undecodable, {withoutCard} with no card.",
        };

        return new DirectImportResult
        {
            Map = map,
            Persons = byPerson.Count,
            CardsSeen = cardsSeen,
            Decoded = map.Count,
            Undecodable = undecodable,
            WithoutCard = withoutCard,
            Notes = notes,
        };
    }
}
