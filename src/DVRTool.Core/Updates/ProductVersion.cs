using System.Reflection;

namespace DVRTool.Core.Updates;

/// <summary>The version of the DVRTool build that is running.</summary>
/// <remarks>
/// Every assembly is stamped from the single <c>&lt;Version&gt;</c> in
/// <c>Directory.Build.props</c> (or the <c>-p:Version</c> the installer passes at publish),
/// so the entry assembly's version is the product's — and it is the same number the MSI
/// carries as its ProductVersion, which is what makes comparing it against a release manifest
/// meaningful. Versions are numeric <c>x.y.z</c> and compared as <see cref="Version"/>, never
/// as strings.
/// </remarks>
public static class ProductVersion
{
    private static readonly Lazy<Version> Lazy = new(Read);

    /// <summary>The running build's version, three components.</summary>
    public static Version Current => Lazy.Value;

    /// <summary>"1.2.3" — what the GUI, the CLI and the User-Agent show.</summary>
    public static string Display => Format(Current);

    public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    /// <summary>
    /// Parses a manifest or tag version: "1.2.3" or "v1.2.3", numeric x.y[.z] only; a fourth
    /// field or any "+sha" / "-pre" suffix is refused rather than dropped, since the MSI would
    /// refuse it too.
    /// </summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];
        if (!Version.TryParse(text, out var parsed) || parsed.Revision >= 0)
            return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    private static Version Read()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(ProductVersion).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is not null)
        {
            int plus = informational.IndexOf('+');
            if (plus >= 0)
                informational = informational[..plus];
            if (TryParse(informational, out var v))
                return v;
        }
        var name = assembly.GetName().Version ?? new Version(0, 0, 0);
        return new Version(name.Major, name.Minor, Math.Max(name.Build, 0));
    }
}
