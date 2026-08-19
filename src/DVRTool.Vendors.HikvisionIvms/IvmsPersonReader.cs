using System.Globalization;
using DVRTool.Core;
using Microsoft.Data.Sqlite;

namespace DVRTool.Vendors.HikvisionIvms;

/// <summary>One person as stored in iVMS's <c>PersonnelBasic</c> (⋈ <c>Card</c>) tables.</summary>
/// <remarks>
/// <see cref="ExpireTime"/> is the anchor the expiry correlator joins on when no CSV export
/// is available. The card number is deliberately absent: <c>Card.CardNo</c> is
/// vendor-encrypted and DVRTool does not decode it — fob↔name is correlated only via the
/// supported CSV export or a unique-expiry join, never by reversing the field cipher.
/// </remarks>
public sealed record IvmsPerson
{
    public required string PersonnelGuid { get; init; }
    public required string Name { get; init; }
    public string? Organization { get; init; }
    public DateTime? ExpireTime { get; init; }
}

/// <summary>
/// Reads the person roster out of an iVMS SQLCipher database. One-way and read-only: it
/// opens the client/server's own encrypted copy with a raw key the operator captured, and
/// never writes to it.
/// </summary>
public static class IvmsPersonReader
{
    private static readonly object InitGate = new();
    private static bool _initialized;

    /// <summary>SQLCipher's "not a database" code — what a wrong/missing key surfaces as.</summary>
    private const int SqliteNotADb = 26;

    /// <summary>iVMS expiry timestamps, most-specific format first.</summary>
    private static readonly string[] ExpireFormats =
    [
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss", "yyyy/MM/dd HH:mm:ss",
    ];

    /// <summary>
    /// Reads every person (LEFT JOIN Card, so name-only people survive) from the DB at
    /// <paramref name="dbPath"/>, decrypting with <paramref name="rawKey"/>.
    /// </summary>
    /// <exception cref="NvrException">
    /// When the key is wrong or not for this install (SQLCipher rejects it).
    /// </exception>
    public static IReadOnlyList<IvmsPerson> ReadPersons(string dbPath, byte[] rawKey)
    {
        EnsureBatteries();

        if (!File.Exists(dbPath))
            throw new NvrException($"iVMS person database not found: {dbPath}");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };

        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        // Raw-key form: SQLCipher takes x'<HEX>' as the literal 32-byte key (no KDF over a
        // passphrase), then compat mode 3 selects iVMS's SQLCipher-3 KDF/page params.
        string hexKey = Convert.ToHexString(rawKey);
        Execute(connection, $"PRAGMA key = \"x'{hexKey}'\";");
        Execute(connection, "PRAGMA cipher_compatibility = 3;");

        try
        {
            bool hasCard = TableExists(connection, "Card");
            bool hasOrg = TableExists(connection, "Organization");
            if (!TableExists(connection, "PersonnelBasic"))
                throw new NvrException(
                    "opened the iVMS database but it has no PersonnelBasic table — this may " +
                    "be the wrong database file for the person roster.");

            return QueryPersons(connection, hasCard, hasOrg);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteNotADb)
        {
            throw new NvrException(
                "could not open the iVMS database — the key is wrong or not for this install " +
                "(SQLCipher rejected it). Capture the key for this install and retry.", inner: ex);
        }
    }

    private static IReadOnlyList<IvmsPerson> QueryPersons(SqliteConnection connection,
        bool hasCard, bool hasOrg)
    {
        // Only names/org/expiry are selected — never Card.CardNo (encrypted, not decoded here).
        string orgSelect = hasOrg ? "o.Name" : "NULL";
        string orgJoin = hasOrg ? "LEFT JOIN Organization o ON o.OrgGUID = p.OrgGUID" : "";
        // A person can hold more than one card; group so each person yields one row.
        string sql = $"""
            SELECT p.PersonnelGUID, p.Name, {orgSelect} AS OrgName, p.ExpireTime
            FROM PersonnelBasic p
            {orgJoin}
            GROUP BY p.PersonnelGUID
            """;

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var persons = new List<IvmsPerson>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? name = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            persons.Add(new IvmsPerson
            {
                PersonnelGuid = reader.GetString(0),
                Name = name,
                Organization = reader.IsDBNull(2) ? null : reader.GetString(2),
                ExpireTime = ParseExpire(reader.IsDBNull(3) ? null : reader.GetString(3)),
            });
        }

        _ = hasCard; // Card is intentionally not read; its number is encrypted.
        return persons;
    }

    /// <summary>Parses an iVMS expiry timestamp; null when blank or in an unknown format.</summary>
    internal static DateTime? ParseExpire(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateTime.TryParseExact(value.Trim(), ExpireFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var t))
            return DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
        // Last resort: a locale-independent general parse, still unspecified-kind.
        return DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var g)
            ? DateTime.SpecifyKind(g, DateTimeKind.Unspecified)
            : null;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Initializes SQLitePCLRaw's SQLCipher provider once per process. Required before any
    /// connection, and safe to call repeatedly.
    /// </summary>
    private static void EnsureBatteries()
    {
        if (_initialized)
            return;
        lock (InitGate)
        {
            if (_initialized)
                return;
            SQLitePCL.Batteries_V2.Init();
            _initialized = true;
        }
    }
}
