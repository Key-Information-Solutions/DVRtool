using System.Globalization;
using DVRTool.Core;
using Microsoft.Data.Sqlite;

namespace DVRTool.Vendors.HikvisionIvms;

/// <summary>One person as stored in iVMS's <c>PersonnelBasic</c> (⋈ <c>Card</c>) tables.</summary>
/// <remarks>
/// <see cref="ExpireTime"/> is the anchor the expiry correlator joins on. This shape carries
/// no card number; use <see cref="IvmsCardRow"/> (via <see cref="IvmsPersonReader.ReadCardholders"/>)
/// when the encoded card is needed for the direct decode.
/// </remarks>
public sealed record IvmsPerson
{
    public required string PersonnelGuid { get; init; }
    public required string Name { get; init; }
    public string? Organization { get; init; }
    public DateTime? ExpireTime { get; init; }
}

/// <summary>
/// One (person, card) row from iVMS: <c>PersonnelBasic</c> LEFT JOIN <c>Card</c>. A person
/// with two cards yields two rows; a person with none yields one row with a null
/// <see cref="CardNoEncoded"/>.
/// </summary>
/// <remarks>
/// <see cref="CardNoEncoded"/> is the vendor-encoded (base64) card field. It is decoded to a
/// plaintext fob by <see cref="IvmsCardCipher"/> — a sanctioned, operator-approved read whose
/// only join key back to the panels is the fob number (iVMS does not push its person id down
/// to these controllers). See <c>docs/ivms-integration-findings.md</c>.
/// </remarks>
public sealed record IvmsCardRow
{
    public required string PersonnelGuid { get; init; }
    public required string Name { get; init; }
    public string? Organization { get; init; }
    public DateTime? ExpireTime { get; init; }

    /// <summary>The encoded card number as stored (base64), or null if the person holds no card.</summary>
    public string? CardNoEncoded { get; init; }
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
    /// Reads every person (one row each, no card) from the DB at <paramref name="dbPath"/>,
    /// decrypting with <paramref name="rawKey"/>.
    /// </summary>
    /// <exception cref="NvrException">
    /// When the key is wrong or not for this install (SQLCipher rejects it).
    /// </exception>
    public static IReadOnlyList<IvmsPerson> ReadPersons(string dbPath, byte[] rawKey)
    {
        using var connection = Open(dbPath, rawKey, out bool _, out bool hasOrg);
        return Guarded(() => QueryPersons(connection, hasOrg));
    }

    /// <summary>
    /// Reads every (person, card) row (LEFT JOIN Card, so name-only people survive) from the
    /// DB at <paramref name="dbPath"/>, decrypting with <paramref name="rawKey"/>. The card
    /// number comes back encoded; decode it with <see cref="IvmsCardCipher"/>.
    /// </summary>
    /// <exception cref="NvrException">
    /// When the key is wrong or not for this install (SQLCipher rejects it).
    /// </exception>
    public static IReadOnlyList<IvmsCardRow> ReadCardholders(string dbPath, byte[] rawKey)
    {
        using var connection = Open(dbPath, rawKey, out bool hasCard, out bool hasOrg);
        return Guarded(() => QueryCardholders(connection, hasCard, hasOrg));
    }

    /// <summary>
    /// Opens the SQLCipher DB read-only, applies the key + iVMS's compat params, and confirms
    /// the person table is present. Throws <see cref="NvrException"/> on a missing file or a
    /// database that has no <c>PersonnelBasic</c> (wrong file).
    /// </summary>
    private static SqliteConnection Open(string dbPath, byte[] rawKey, out bool hasCard, out bool hasOrg)
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

        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        // iVMS passes the DB key to sqlite3_key as the *base64 text itself* — a passphrase
        // SQLCipher runs through its KDF — not as the 32 raw bytes (an x'<HEX>' literal key
        // fails with "file is not a database"; verified against the live store). The key is
        // standard base64 (alphabet A–Za–z0–9+/=, no quote char), so it embeds safely. Then
        // compat mode 3 selects iVMS's SQLCipher-3 KDF/page params.
        string passphrase = Convert.ToBase64String(rawKey);
        Execute(connection, $"PRAGMA key = '{passphrase}';");
        Execute(connection, "PRAGMA cipher_compatibility = 3;");

        try
        {
            hasCard = TableExists(connection, "Card");
            hasOrg = TableExists(connection, "Organization");
            if (!TableExists(connection, "PersonnelBasic"))
                throw new NvrException(
                    "opened the iVMS database but it has no PersonnelBasic table — this may " +
                    "be the wrong database file for the person roster.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteNotADb)
        {
            connection.Dispose();
            throw WrongKey(ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }

    /// <summary>Runs a query, translating SQLCipher's "not a database" into a clear key error.</summary>
    private static T Guarded<T>(Func<T> query)
    {
        try
        {
            return query();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteNotADb)
        {
            throw WrongKey(ex);
        }
    }

    private static NvrException WrongKey(Exception inner) => new(
        "could not open the iVMS database — the key is wrong or not for this install " +
        "(SQLCipher rejected it). Capture the key for this install and retry.", inner: inner);

    private static IReadOnlyList<IvmsPerson> QueryPersons(SqliteConnection connection, bool hasOrg)
    {
        string orgSelect = hasOrg ? "o.OrgName" : "NULL";
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

        return persons;
    }

    private static IReadOnlyList<IvmsCardRow> QueryCardholders(SqliteConnection connection,
        bool hasCard, bool hasOrg)
    {
        string orgSelect = hasOrg ? "o.OrgName" : "NULL";
        string orgJoin = hasOrg ? "LEFT JOIN Organization o ON o.OrgGUID = p.OrgGUID" : "";
        string cardSelect = hasCard ? "c.CardNo" : "NULL";
        string cardJoin = hasCard ? "LEFT JOIN Card c ON c.PersonnelGUID = p.PersonnelGUID" : "";
        // No GROUP BY: one row per card so a person with two fobs yields both.
        string sql = $"""
            SELECT p.PersonnelGUID, p.Name, {orgSelect} AS OrgName, p.ExpireTime, {cardSelect} AS CardNo
            FROM PersonnelBasic p
            {orgJoin}
            {cardJoin}
            """;

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var rows = new List<IvmsCardRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? name = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            rows.Add(new IvmsCardRow
            {
                PersonnelGuid = reader.GetString(0),
                Name = name,
                Organization = reader.IsDBNull(2) ? null : reader.GetString(2),
                ExpireTime = ParseExpire(reader.IsDBNull(3) ? null : reader.GetString(3)),
                CardNoEncoded = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return rows;
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
