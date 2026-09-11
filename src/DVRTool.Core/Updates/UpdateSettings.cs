using System.Text.Json;
using System.Text.Json.Serialization;

namespace DVRTool.Core.Updates;

/// <summary>
/// Per-user update state: when the last automatic check ran, which version the operator
/// skipped, and whether automatic checks are on. <c>%APPDATA%\DVRTool\updates.json</c>, a
/// sibling of the device and pin files.
/// </summary>
/// <remarks>
/// Losing this file costs nothing worse than one extra check and one re-shown banner, so it
/// loads quietly (a corrupt file reads as defaults) — the opposite of the pin stores, whose
/// contents guard writes to recorders.
/// </remarks>
public sealed class UpdateSettings
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    [JsonPropertyName("autoCheck")] public bool AutoCheck { get; set; } = true;
    [JsonPropertyName("lastCheck")] public DateTimeOffset? LastCheck { get; set; }
    [JsonPropertyName("skippedVersion")] public string? SkippedVersionText { get; set; }
    /// <summary>The newest version the last check saw, so a front end can say so offline.</summary>
    [JsonPropertyName("lastSeenVersion")] public string? LastSeenVersionText { get; set; }

    [JsonIgnore]
    public Version? SkippedVersion =>
        ProductVersion.TryParse(SkippedVersionText, out var v) ? v : null;

    /// <summary>True when an automatic check is due: never checked, the interval passed, or
    /// the last check is stamped in the future (a clock that moved).</summary>
    public bool IsCheckDue(DateTimeOffset now) =>
        AutoCheck && (LastCheck is null || now - LastCheck.Value >= CheckInterval ||
                      LastCheck.Value > now + TimeSpan.FromHours(1));

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DVRTool",
        "updates.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static UpdateSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
                return new UpdateSettings();
            return JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path), Options)
                   ?? new UpdateSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UpdateSettings();
        }
    }

    /// <summary>Saves; a failure is a false return, never a throw, because nothing that
    /// matters depends on this file.</summary>
    public bool TrySave(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
