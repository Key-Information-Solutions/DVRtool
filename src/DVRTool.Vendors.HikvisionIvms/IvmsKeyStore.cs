using DVRTool.Core;

namespace DVRTool.Vendors.HikvisionIvms;

/// <summary>
/// Resolves and caches the per-install SQLCipher key iVMS uses for its person database.
/// </summary>
/// <remarks>
/// <para>
/// The key is a per-install random secret, base64, decoding to 32 raw bytes. DVRTool never
/// derives it: the operator captures it once from a live iVMS process with an external,
/// out-of-tree step and hands it to DVRTool (via <c>access identity --capture-key</c> or
/// <c>IVMS_DB_KEY</c>). There is no hardcoded key here and there must never be one — a
/// committed key would decrypt real cardholder data for anyone with the repo.
/// </para>
/// <para>
/// Resolution order is explicit &gt; environment &gt; cache file, so a one-off flag or a
/// shell-scoped env var always overrides a stale cached key without editing files.
/// </para>
/// </remarks>
public static class IvmsKeyStore
{
    /// <summary>iVMS keys decode to a 32-byte (256-bit) raw key.</summary>
    private const int ExpectedKeyBytes = 32;

    /// <summary>Environment variable an operator can set instead of caching a key file.</summary>
    public const string EnvVarName = "IVMS_DB_KEY";

    /// <summary><c>%LOCALAPPDATA%\DVRTool\ivms-keys</c>.</summary>
    private static string KeyDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DVRTool", "ivms-keys");

    private static string KeyPath(string installId) =>
        Path.Combine(KeyDirectory, $"{installId}.key");

    /// <summary>
    /// Decodes a base64 iVMS key and confirms it is 32 raw bytes. Throws
    /// <see cref="NvrException"/> on malformed input so a mistyped capture fails loudly at
    /// the point of entry rather than as an opaque SQLCipher rejection later.
    /// </summary>
    public static byte[] Decode(string base64)
    {
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException ex)
        {
            throw new NvrException(
                "the iVMS DB key is not valid base64. Re-capture it and pass the exact string.",
                inner: ex);
        }

        if (raw.Length != ExpectedKeyBytes)
            throw new NvrException(
                $"the iVMS DB key decoded to {raw.Length} bytes, expected {ExpectedKeyBytes} " +
                "(a 44-character base64 string). This does not look like an iVMS SQLCipher key.");

        return raw;
    }

    /// <summary>
    /// Resolves the raw key for an install: explicit flag &gt; <c>IVMS_DB_KEY</c> &gt; cache
    /// file. Throws <see cref="NvrException"/> with capture guidance when none is available.
    /// </summary>
    public static byte[] Resolve(string? explicitB64, string installId)
    {
        if (!string.IsNullOrWhiteSpace(explicitB64))
            return Decode(explicitB64);

        if (Environment.GetEnvironmentVariable(EnvVarName) is { Length: > 0 } env)
            return Decode(env);

        string path = KeyPath(installId);
        if (File.Exists(path))
            return Decode(File.ReadAllText(path));

        throw new NvrException(
            $"no iVMS DB key for install '{installId}'. Capture it once with " +
            "`dvrtool access identity --capture-key <base64>`, set it in the " +
            $"{EnvVarName} environment variable, or pass --db-key <base64>.");
    }

    /// <summary>
    /// Caches a base64 key for an install after validating it decodes to 32 bytes. The file
    /// is written under <c>%LOCALAPPDATA%</c>, gitignored, never in the repo.
    /// </summary>
    public static void Store(string installId, string base64Key)
    {
        _ = Decode(base64Key); // validate before persisting
        Directory.CreateDirectory(KeyDirectory);
        File.WriteAllText(KeyPath(installId), base64Key.Trim());
    }
}
