using System.Text;
using System.Xml.Linq;
using DVRTool.Core;

namespace DVRTool.Vendors.Hikvision;

/// <summary>
/// The <see cref="IUserAdminClient"/> face of the ISAPI client: the recorder's own login
/// accounts, and creating one.
/// </summary>
/// <remarks>
/// See <c>docs/user-management.md</c> before changing anything here. The two traps:
/// a create sends <c>&lt;id&gt;0&lt;/id&gt;</c> as a placeholder because the device assigns the
/// real id, and a refused password comes back as <b>HTTP 200</b> with a non-1
/// <c>statusCode</c> — so a create that only checks the HTTP status reports success for an
/// account that was never made.
/// </remarks>
public sealed partial class HikvisionClient
{
    public async Task<IReadOnlyList<NvrUser>> GetUsersAsync(CancellationToken ct = default)
    {
        var doc = await GetXmlAsync("/ISAPI/Security/users", ct);
        var users = new List<NvrUser>();
        if (doc.Root is null)
            return users;

        foreach (var user in ElementsNamed(doc.Root, "User"))
        {
            string? name = Child(user, "userName");
            if (string.IsNullOrEmpty(name))
                continue;

            string level = Child(user, "userLevel") ?? "";
            bool reserved =
                string.Equals(name, "admin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Descendant(user, "inherent"), "true", StringComparison.OrdinalIgnoreCase);

            // ISAPI has no memo field, and passwords are never returned.
            users.Add(new NvrUser(Child(user, "id") ?? "", name, MapUserRole(level), level, reserved));
        }

        return users;
    }

    private static UserRole MapUserRole(string userLevel) => userLevel.Trim().ToLowerInvariant() switch
    {
        "administrator" => UserRole.Admin,
        "operator" => UserRole.Operator,
        // Some firmware localizes the third tier as "User" or "Guest".
        "viewer" or "user" or "guest" => UserRole.Viewer,
        _ => UserRole.Custom,
    };

    /// <summary>
    /// Creates an account, then returns it as the device reports it afterwards.
    /// </summary>
    /// <remarks>
    /// Only <c>userLevel</c> is written. The fine-grained rights at
    /// <c>/ISAPI/Security/UserPermission/&lt;id&gt;</c> are deliberately left alone: the level
    /// carries the firmware's own default permission set, and the permission schemas vary by
    /// firmware in ways this tool has not verified — writing a guessed permission document
    /// would be the difference between an account that works and one that silently opens
    /// nothing.
    /// </remarks>
    public async Task<NvrUser> CreateUserAsync(NewUser user, CancellationToken ct = default)
    {
        string name = user.Name.Trim();
        if (name.Length == 0)
            throw new ArgumentException("an account needs a name.", nameof(user));
        if (user.Password.Length == 0)
            throw new ArgumentException("an account needs a password.", nameof(user));

        // <id>0</id> is a placeholder: ISAPI assigns the real id and echoes it back. Omitting
        // the element entirely is refused on some firmware, so it is always sent.
        var body = new XElement("User",
            new XElement("id", 0),
            new XElement("userName", name),
            new XElement("password", user.Password),
            new XElement("userLevel", NativeLevel(user.Role)));

        string text = await PostXmlTextAsync("/ISAPI/Security/users",
            new XDocument(body).ToString(SaveOptions.DisableFormatting),
            $"creating account '{name}' failed", ct);

        // ISAPI answers a ResponseStatus document; statusCode 1 is OK. A password the firmware
        // considers weak is rejected here, under HTTP 200, and the device's own words are the
        // only useful thing to show — so they are carried through verbatim.
        if (TryParseStatusCode(text) is { } status && status != 1)
            throw new NvrException(
                $"the recorder refused to create '{name}': {DescribeRejection(text, status)}", text);

        // Read back: an accepted POST that leaves no account behind is a failure. This is also
        // where the device-assigned id and the level it actually kept come from.
        var users = await GetUsersAsync(ct);
        return users.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new NvrException(
                $"'{name}' was accepted by the recorder but is not in the account list read back " +
                "— treat the account as not created", text);
    }

    /// <summary>The ISAPI spelling of a role. <see cref="UserRole.Custom"/> has none to write.</summary>
    private static string NativeLevel(UserRole role) => role switch
    {
        UserRole.Admin => "Administrator",
        UserRole.Operator => "Operator",
        UserRole.Viewer => "Viewer",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role,
            "an account can only be created at Administrator, Operator or Viewer: a custom " +
            "level is a permission set this tool does not write"),
    };

    /// <summary>
    /// The device's own account of a rejection: <c>subStatusCode</c> (where "riskPassword"
    /// lives) and <c>statusString</c>/<c>errorMsg</c>, kept verbatim. Falls back to the bare
    /// number when the body says nothing more.
    /// </summary>
    private static string DescribeRejection(string responseBody, int status)
    {
        var parts = new List<string>();
        try
        {
            var root = XDocument.Parse(responseBody).Root;
            foreach (string field in new[] { "subStatusCode", "statusString", "errorMsg" })
            {
                string? value = root?.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == field)?.Value;
                if (!string.IsNullOrWhiteSpace(value))
                    parts.Add(value.Trim());
            }
        }
        catch (System.Xml.XmlException)
        {
            // Nothing parseable; the status code alone has to carry it.
        }

        // Duplicates are common (statusString repeats subStatusCode on some firmware).
        var said = parts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return said.Count > 0
            ? $"{string.Join(" / ", said)} (statusCode {status})"
            : $"statusCode {status}";
    }

    /// <summary>
    /// POSTs a body and returns the response text. The twin of <c>PostXmlAsync</c> for the
    /// calls whose answer is a status document rather than data: the caller checks the ISAPI
    /// status itself, because the interesting failures here arrive under HTTP 200.
    /// </summary>
    private async Task<string> PostXmlTextAsync(string path, string body, string failurePrefix,
        CancellationToken ct)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/xml");
        using var resp = await _http.PostAsync(path, content, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new NvrException(
                $"{failurePrefix}: {(int)resp.StatusCode} {resp.ReasonPhrase}",
                text, (int)resp.StatusCode);
        return text;
    }
}
