namespace DVRTool.Vendors.HikvisionIvms;

/// <summary>
/// One discovered iVMS-4200 / NVMS V3 "Site" installation on this machine, and the path to
/// its encrypted person database.
/// </summary>
/// <remarks>
/// NVMS V3 is iVMS-4200 rebranded — identical layout — so both are discovered the same way.
/// The person roster (names, org, card numbers) lives in the SQLCipher-encrypted
/// <c>PersonalManagement.S\PersonalManagement</c> store under the site's <c>UserData</c>
/// root. <see cref="Id"/> is a filesystem-safe slug used to name this install's cached key,
/// because a machine can host more than one product/site each with its own DB key.
/// </remarks>
public sealed record IvmsInstall
{
    public required string Product { get; init; }
    public required string UserDataPath { get; init; }
    public required string PersonDbPath { get; init; }
    public required string Id { get; init; }

    /// <summary>The two known "Site" UserData roots, paired with a product label.</summary>
    private static readonly (string Product, string Root)[] KnownRoots =
    [
        ("iVMS-4200", @"C:\Users\Public\iVMS-4200 Site\UserData"),
        ("NVMS V3", @"C:\Users\Public\NVMS V3 Site\UserData"),
    ];

    /// <summary>The person DB's path relative to a UserData root.</summary>
    private const string PersonDbRelative = @"PersonalManagement.S\PersonalManagement";

    /// <summary>Installs whose UserData root actually exists on this machine.</summary>
    public static IEnumerable<IvmsInstall> Discover()
    {
        foreach (var (product, root) in KnownRoots)
        {
            if (Directory.Exists(root))
                yield return FromUserData(root, product);
        }
    }

    /// <summary>Builds an install descriptor from a UserData root path.</summary>
    public static IvmsInstall FromUserData(string userDataPath) =>
        FromUserData(userDataPath, ProductFor(userDataPath));

    private static IvmsInstall FromUserData(string userDataPath, string product) => new()
    {
        Product = product,
        UserDataPath = userDataPath,
        PersonDbPath = Path.Combine(userDataPath, PersonDbRelative),
        Id = Slug(product),
    };

    /// <summary>Best-effort product name from a path, defaulting to a generic label.</summary>
    private static string ProductFor(string userDataPath)
    {
        foreach (var (product, root) in KnownRoots)
        {
            if (string.Equals(Path.GetFullPath(userDataPath).TrimEnd('\\'),
                    Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return product;
        }
        // Fall back to the site folder name (the parent of UserData), e.g. "iVMS-4200 Site".
        return Path.GetFileName(Path.GetDirectoryName(userDataPath.TrimEnd('\\')) ?? "") is { Length: > 0 } site
            ? site
            : "ivms";
    }

    /// <summary>A stable, filesystem-safe slug for the cached-key filename.</summary>
    private static string Slug(string product)
    {
        var chars = product.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        string slug = new string(chars).Trim('-');
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");
        return slug.Length > 0 ? slug : "ivms";
    }
}
