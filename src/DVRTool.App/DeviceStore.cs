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
    /// The serial number this record is bound to, learned on the first successful connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The record's name, not its address, is what an export ends up filed under — so the
    /// record is what has to be tied to a specific piece of hardware. Host and port cannot do
    /// that: a site with several systems behind one IP tells them apart by forwarded port
    /// alone, and a fleet on one shared account authenticates just as happily against the
    /// wrong one. Every connection therefore checks the serial against this, and a system
    /// that answers with a different one is refused rather than used.
    /// </para>
    /// <para>
    /// Empty means "not yet bound" — a record added before this existed, or a device that
    /// reports no serial at all. The first verified connection fills it in.
    /// </para>
    /// </remarks>
    public string ExpectedSerial { get; set; } = "";

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

    /// <summary>The identity-pin key for this record: the port it authenticates on.</summary>
    [JsonIgnore]
    public string Address => DeviceAddress.Format(Host, HttpPort);

    /// <summary>This record as the fleet audit sees it.</summary>
    public FleetRecord ToFleetRecord() =>
        new(Name.Length > 0 ? Name : Address, Host, HttpPort,
            ExpectedSerial.Length > 0 ? ExpectedSerial : null);
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
