using System.Text.Json;
using System.Text.Json.Serialization;

namespace DVRTool.Core;

/// <summary>
/// One cardholder's identity, imported from iVMS and keyed back to the panel roster by
/// fob number.
/// </summary>
/// <remarks>
/// The panels store no names (verified on DS-K2604 V2.0), so identity is enrichment that
/// rides alongside the fob roster rather than living on it. The fob number is the only
/// join key iVMS and the panels share — iVMS does not push its person/employee id down to
/// these controllers. <see cref="Source"/> records where the mapping came from (e.g. a
/// supported CSV export vs. a partial expiry correlation) so a thin, best-effort match is
/// never mistaken for a complete one.
/// </remarks>
public sealed record CardholderIdentity
{
    public required string Fob { get; init; }
    public required string Name { get; init; }
    public string? Organization { get; init; }

    /// <summary>Cardholder's iVMS expiry, when known — the anchor the expiry join uses.</summary>
    public DateTime? ExpiresOn { get; init; }

    /// <summary>How this mapping was obtained (e.g. <c>ivms-csv</c>, <c>ivms-expiry</c>).</summary>
    public required string Source { get; init; }
}

/// <summary>
/// A fob → cardholder-name mapping imported one-way from iVMS. Pure and immutable so it can
/// be built, merged and looked up identically from the CLI, the GUI, or a test.
/// </summary>
/// <remarks>
/// Deduped on the same normalized fob key the roster joins on
/// (<see cref="AccessRoster.NormalizeCardNo"/>), so "0123" and "123" resolve to one person
/// exactly as the device treats them as one card.
/// </remarks>
public sealed record IdentityMap
{
    public required IReadOnlyList<CardholderIdentity> Identities { get; init; }
    public required string Source { get; init; }
    public DateTime? CapturedAtUtc { get; init; }

    public int Count => Identities.Count;

    /// <summary>
    /// Builds a map, dropping blank fobs/names and collapsing duplicate fobs (later entry
    /// wins). A later duplicate winning matches how a re-import supersedes an earlier row.
    /// </summary>
    public static IdentityMap Build(IEnumerable<CardholderIdentity> identities, string source,
        DateTime? capturedAtUtc = null)
    {
        var byFob = new Dictionary<string, CardholderIdentity>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            if (string.IsNullOrWhiteSpace(identity.Fob) || string.IsNullOrWhiteSpace(identity.Name))
                continue;
            byFob[AccessRoster.NormalizeCardNo(identity.Fob)] = identity;
        }

        return new IdentityMap
        {
            Identities = byFob.Values.ToList(),
            Source = source,
            CapturedAtUtc = capturedAtUtc,
        };
    }

    /// <summary>The identity for a fob, matching leading-zero variants; null when unknown.</summary>
    public CardholderIdentity? Lookup(string fob)
    {
        string key = AccessRoster.NormalizeCardNo(fob);
        return Identities.FirstOrDefault(i => AccessRoster.NormalizeCardNo(i.Fob) == key);
    }

    /// <summary>
    /// Merges another map on top of this one; <paramref name="other"/> wins on fob conflicts,
    /// so the freshly imported map supersedes the cached one. The combined label keeps both
    /// provenances visible.
    /// </summary>
    public IdentityMap Merge(IdentityMap other)
    {
        var byFob = new Dictionary<string, CardholderIdentity>(StringComparer.Ordinal);
        foreach (var identity in Identities)
            byFob[AccessRoster.NormalizeCardNo(identity.Fob)] = identity;
        foreach (var identity in other.Identities)
            byFob[AccessRoster.NormalizeCardNo(identity.Fob)] = identity;

        string source = Source == other.Source ? Source : $"{Source}+{other.Source}";
        DateTime? captured = (this.CapturedAtUtc, other.CapturedAtUtc) switch
        {
            (DateTime a, DateTime b) => a > b ? a : b,
            (DateTime a, null) => a,
            (null, DateTime b) => b,
            _ => null,
        };

        return new IdentityMap
        {
            Identities = byFob.Values.ToList(),
            Source = source,
            CapturedAtUtc = captured,
        };
    }
}

/// <summary>
/// Persists an <see cref="IdentityMap"/> to a DVRTool-owned JSON file. DVRTool holds the
/// mapping natively (findings §4 option B) so name enrichment does not depend on iVMS being
/// present or unlocked at every run — the import happens once, the store answers thereafter.
/// </summary>
public static class IdentityMapStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary><c>%LOCALAPPDATA%\DVRTool\identity-map.json</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DVRTool", "identity-map.json");

    /// <summary>Loads the stored map, or null when no file exists yet.</summary>
    public static IdentityMap? Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return null;
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<IdentityMap>(json, JsonOptions);
    }

    /// <summary>Writes the map, creating the containing directory if needed.</summary>
    public static void Save(IdentityMap map, string? path = null)
    {
        path ??= DefaultPath;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(map, JsonOptions));
    }
}
