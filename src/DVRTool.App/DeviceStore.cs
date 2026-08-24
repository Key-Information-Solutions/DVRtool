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
    /// Vendor SDK port. The default is Hikvision's; Dahua's is
    /// <see cref="VendorPorts.DahuaSdk"/>, and <see cref="DeviceStore.Load"/> corrects saved
    /// Dahua entries that predate this being vendor-aware.
    /// </summary>
    public int SdkPort { get; set; } = VendorPorts.HikvisionSdk;

    public bool UseTls { get; set; }
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";

    /// <summary>
    /// <see cref="Vendor"/> as the enum. The stored form stays a string so an unknown value
    /// in <c>devices.json</c> degrades to Hikvision instead of failing to deserialize.
    /// </summary>
    [JsonIgnore]
    public DVRTool.Core.Vendor VendorKind =>
        Vendor.Equals("dahua", StringComparison.OrdinalIgnoreCase)
            ? DVRTool.Core.Vendor.Dahua
            : DVRTool.Core.Vendor.Hikvision;

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

    public INvrClient CreateClient() => VendorKind == DVRTool.Core.Vendor.Dahua
        ? new DahuaClient(ToConnection())
        : new HikvisionClient(ToConnection());
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
            var devices = JsonSerializer.Deserialize<List<SavedDevice>>(File.ReadAllText(FilePath))
                ?? [];
            foreach (var device in devices)
                MigrateDahuaSdkPort(device);
            return devices;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Repoints a Dahua entry still carrying Hikvision's SDK port at Dahua's own.
    /// </summary>
    /// <remarks>
    /// The SDK port used to default to 8000 regardless of vendor, so every Dahua unit added
    /// before that was fixed carries a number that means nothing on Dahua hardware — the port
    /// check reports it dead and the operator is sent chasing a firewall rule for a port the
    /// recorder never opened. Rewriting it is safe in a way most silent migrations are not:
    /// nothing dials this port, so a wrong guess costs one misleading line in
    /// <b>Test connection</b> rather than a failed connection, and 8000 is not a port Dahua
    /// firmware puts anything on. A port the operator actually moved is left alone, since only
    /// the exact old default is touched.
    /// </remarks>
    private static void MigrateDahuaSdkPort(SavedDevice device)
    {
        if (device.VendorKind == DVRTool.Core.Vendor.Dahua &&
            device.SdkPort == VendorPorts.HikvisionSdk)
            device.SdkPort = VendorPorts.DahuaSdk;
    }

    public static void Save(IEnumerable<SavedDevice> devices)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(devices.ToList(),
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
