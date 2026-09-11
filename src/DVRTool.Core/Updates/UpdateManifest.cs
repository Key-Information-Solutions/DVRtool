using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DVRTool.Core.Updates;

/// <summary>
/// The signed description of one release: what the installer is, how big it is, what it
/// hashes to, and where the notes are. Published as <c>update.json</c> next to the MSI on a
/// GitHub Release, with its Ed25519 signature in <c>update.json.sig</c>.
/// </summary>
/// <remarks>
/// The signature covers the exact bytes of the file, so the manifest is verified as bytes
/// before it is parsed, and <see cref="Parse(ReadOnlySpan{byte})"/> is only ever called on
/// bytes that verified. <see cref="MinimumVersion"/> is reserved: a release that cannot
/// upgrade a very old install would name the oldest version it accepts. It is empty today and
/// the check treats empty as "any".
/// </remarks>
public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string VersionText,
    [property: JsonPropertyName("file")] string FileName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("notesUrl")] string? NotesUrl,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("minimumVersion")] string? MinimumVersion,
    [property: JsonPropertyName("keyId")] string? KeyId)
{
    public const string FileNameOnRelease = "update.json";
    public const string SignatureFileName = "update.json.sig";

    [JsonIgnore]
    public Version Version =>
        ProductVersion.TryParse(VersionText, out var v)
            ? v
            : throw new FormatException($"Manifest version '{VersionText}' is not numeric x.y.z.");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Parses and validates; throws <see cref="FormatException"/> on anything off.</summary>
    public static UpdateManifest Parse(ReadOnlySpan<byte> json)
    {
        UpdateManifest? m;
        try
        {
            m = JsonSerializer.Deserialize<UpdateManifest>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Manifest is not valid JSON: {ex.Message}", ex);
        }
        if (m is null)
            throw new FormatException("Manifest is empty.");
        _ = m.Version;
        if (string.IsNullOrWhiteSpace(m.FileName) || m.FileName.Contains('/') ||
            m.FileName.Contains('\\') ||
            !m.FileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Manifest file name '{m.FileName}' is not a bare .msi name.");
        if (m.Size <= 0)
            throw new FormatException("Manifest size must be positive.");
        if (m.Sha256 is not { Length: 64 } || !m.Sha256.All(Uri.IsHexDigit))
            throw new FormatException("Manifest sha256 must be 64 hex digits.");
        if (m.MinimumVersion is { Length: > 0 } && !ProductVersion.TryParse(m.MinimumVersion, out _))
            throw new FormatException(
                $"Manifest minimumVersion '{m.MinimumVersion}' is not numeric x.y.z.");
        return m;
    }

    public static UpdateManifest Parse(string json) => Parse(Encoding.UTF8.GetBytes(json));

    /// <summary>The bytes the release tool signs and uploads.</summary>
    public byte[] ToJsonBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    /// <summary>True when this release accepts an upgrade from <paramref name="installed"/>.</summary>
    public bool Accepts(Version installed) =>
        string.IsNullOrEmpty(MinimumVersion) ||
        !ProductVersion.TryParse(MinimumVersion, out var min) || installed >= min;
}
