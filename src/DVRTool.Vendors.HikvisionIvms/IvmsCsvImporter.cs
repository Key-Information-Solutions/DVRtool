using DVRTool.Core;

namespace DVRTool.Vendors.HikvisionIvms;

/// <summary>
/// Imports the supported iVMS Person export (CSV) into an <see cref="IdentityMap"/>.
/// </summary>
/// <remarks>
/// This is the complete, recommended fob↔name path (findings §4 option A): the export
/// carries plaintext card numbers, so it needs neither the DB key nor the reverse-engineered
/// card cipher. It is a straight, sanctioned data-out from iVMS's own UI.
/// </remarks>
public static class IvmsCsvImporter
{
    private const string Source = "ivms-csv";

    /// <summary>Column headers that hold a full name, in preference order.</summary>
    private static readonly string[] NameHeaders = ["name", "person name", "full name"];
    private static readonly string[] FirstNameHeaders = ["first name", "firstname", "given name"];
    private static readonly string[] LastNameHeaders = ["last name", "lastname", "surname", "family name"];

    /// <summary>Column headers that hold a card/fob number, in preference order.</summary>
    private static readonly string[] CardHeaders =
        ["card no", "card number", "card no.", "cardno", "card"];

    private static readonly string[] OrgHeaders =
        ["organization", "org", "department", "dept"];

    public static IdentityMap Import(string csvPath)
    {
        using var reader = new StreamReader(csvPath);
        return Parse(reader);
    }

    public static IdentityMap Parse(TextReader reader)
    {
        var rows = ReadRows(reader);
        if (rows.Count == 0)
            return IdentityMap.Build([], Source);

        var header = rows[0];
        var map = BuildColumnMap(header);

        int nameCol = FindColumn(map, NameHeaders);
        int firstCol = FindColumn(map, FirstNameHeaders);
        int lastCol = FindColumn(map, LastNameHeaders);
        int cardCol = FindColumn(map, CardHeaders);
        int orgCol = FindColumn(map, OrgHeaders);

        var identities = new List<CardholderIdentity>();
        foreach (var row in rows.Skip(1))
        {
            string card = At(row, cardCol);
            string name = nameCol >= 0
                ? At(row, nameCol)
                : string.Join(" ", new[] { At(row, firstCol), At(row, lastCol) }
                    .Where(s => s.Length > 0)).Trim();

            if (string.IsNullOrWhiteSpace(card) || string.IsNullOrWhiteSpace(name))
                continue;

            string org = At(row, orgCol);
            identities.Add(new CardholderIdentity
            {
                Fob = card.Trim(),
                Name = name.Trim(),
                Organization = org.Length > 0 ? org.Trim() : null,
                Source = Source,
            });
        }

        return IdentityMap.Build(identities, Source);
    }

    private static Dictionary<string, int> BuildColumnMap(IReadOnlyList<string> header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Count; i++)
        {
            string key = header[i].Trim();
            // First occurrence wins so a duplicated header does not shadow the earlier column.
            if (key.Length > 0 && !map.ContainsKey(key))
                map[key] = i;
        }
        return map;
    }

    private static int FindColumn(Dictionary<string, int> map, string[] candidates)
    {
        foreach (string candidate in candidates)
            if (map.TryGetValue(candidate, out int index))
                return index;
        return -1;
    }

    private static string At(IReadOnlyList<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index] : "";

    /// <summary>
    /// Reads CSV rows, honoring RFC-4180 double-quoted fields (embedded commas, quotes and
    /// newlines) and stripping a UTF-8 BOM off the first cell.
    /// </summary>
    private static List<List<string>> ReadRows(TextReader reader)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        bool inQuotes = false;
        bool fieldStarted = false;
        bool rowStarted = false;

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void EndRow()
        {
            EndField();
            rows.Add(row.ToList());
            row.Clear();
            rowStarted = false;
        }

        int read;
        while ((read = reader.Read()) >= 0)
        {
            char c = (char)read;
            rowStarted = true;

            if (inQuotes)
            {
                if (c == '"')
                {
                    // A doubled quote inside a quoted field is a literal quote.
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"' when !fieldStarted:
                    inQuotes = true;
                    fieldStarted = true;
                    break;
                case ',':
                    EndField();
                    break;
                case '\r':
                    // Swallow a following \n so CRLF ends exactly one row.
                    if (reader.Peek() == '\n')
                        reader.Read();
                    EndRow();
                    break;
                case '\n':
                    EndRow();
                    break;
                default:
                    field.Append(c);
                    fieldStarted = true;
                    break;
            }
        }

        // A file not ending in a newline still has a final row to flush.
        if (rowStarted || field.Length > 0 || row.Count > 0)
            EndRow();

        StripBom(rows);
        return rows;
    }

    private static void StripBom(List<List<string>> rows)
    {
        if (rows.Count > 0 && rows[0].Count > 0 && rows[0][0].StartsWith('﻿'))
            rows[0][0] = rows[0][0].TrimStart('﻿');
    }
}
