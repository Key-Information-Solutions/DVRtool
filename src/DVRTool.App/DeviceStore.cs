using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DVRTool.Core;
using DVRTool.Vendors.Dahua;
using DVRTool.Vendors.Hikvision;

namespace DVRTool.App;

/// <summary>A saved NVR. The password is DPAPI-protected per Windows user.</summary>
public sealed class SavedDevice
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "hikvision";
    public string Host { get; set; } = "";
    public int HttpPort { get; set; } = 80;
    public int RtspPort { get; set; } = 554;

    /// <summary>
    /// Vendor SDK port. Devices saved before this field existed deserialize to 8000, which
    /// is the factory default — an operator who moved it has to say so here.
    /// </summary>
    public int SdkPort { get; set; } = 8000;

    public bool UseTls { get; set; }
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";

    public void SetPassword(string plain) =>
        ProtectedPassword = Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    [JsonIgnore]
    public string Password => ProtectedPassword.Length == 0
        ? ""
        : Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(ProtectedPassword), null, DataProtectionScope.CurrentUser));

    public NvrConnection ToConnection() => new()
    {
        Host = Host,
        HttpPort = HttpPort,
        RtspPort = RtspPort,
        SdkPort = SdkPort,
        Username = Username,
        Password = Password,
        UseTls = UseTls,
    };

    public INvrClient CreateClient() => Vendor.ToLowerInvariant() switch
    {
        "dahua" => new DahuaClient(ToConnection()),
        _ => new HikvisionClient(ToConnection()),
    };
}

public static class DeviceStore
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DVRTool");

    private static string FilePath => Path.Combine(Dir, "devices.json");

    public static List<SavedDevice> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];
            return JsonSerializer.Deserialize<List<SavedDevice>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public static void Save(IEnumerable<SavedDevice> devices)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(devices.ToList(),
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
