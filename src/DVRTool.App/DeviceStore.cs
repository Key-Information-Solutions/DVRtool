using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DVRTool.Core;
using DVRTool.Vendors.Dahua;
using DVRTool.Vendors.Hikvision;
using DVRTool.Vendors.NxWitness;

namespace DVRTool.App;

/// <summary>A saved device. The password is DPAPI-protected per Windows user.</summary>
public sealed class SavedDevice
{
    public string Name { get; set; } = "";

    /// <summary>
    /// What kind of hardware this record means: <c>"recorder"</c> (NVR/DVR) or
    /// <c>"panel"</c> (door-access controller). Stored as a string so records written by an
    /// older build — which carry no kind at all — deserialize as recorders, which is what
    /// every record was before panels could be saved.
    /// </summary>
    public string Kind { get; set; } = "recorder";

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
        VendorNames.TryParse(Vendor, out var vendor) ? vendor : DVRTool.Core.Vendor.Hikvision;

    /// <summary>True when this record is a door-access controller rather than a recorder.</summary>
    [JsonIgnore]
    public bool IsPanel => Kind.Equals("panel", StringComparison.OrdinalIgnoreCase);

    /// <summary>How the record reads in the device list, where both kinds sit together.</summary>
    [JsonIgnore]
    public string DisplayLabel => IsPanel ? $"{DisplayName}  (panel)" : DisplayName;

    [JsonIgnore]
    private string DisplayName => Name.Length > 0 ? Name : Host;

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

    /// <summary>
    /// A recorder's vendor client. Meaningless for a panel — those are driven through
    /// <see cref="ToPanelConnection"/> — so calling this on one is a caller bug, not a
    /// condition to degrade around.
    /// </summary>
    public INvrClient CreateClient()
    {
        if (IsPanel)
            throw new InvalidOperationException(
                $"'{Name}' is a door panel — it has no NVR client.");
        return VendorKind switch
        {
            DVRTool.Core.Vendor.Dahua => new DahuaClient(ToConnection()),
            DVRTool.Core.Vendor.NxWitness => new NxWitnessClient(ToConnection()),
            _ => new HikvisionClient(ToConnection()),
        };
    }

    /// <summary>This record as the Access engine's connection type. Panels only.</summary>
    public AccessPanelConnection ToPanelConnection() => new()
    {
        Host = Host,
        SdkPort = SdkPort,
        Username = Username,
        Password = Password,
    };

    /// <summary>
    /// The identity-pin key for this record: the port it authenticates on. For a recorder
    /// that is the web port; a panel's only port is the SDK one.
    /// </summary>
    [JsonIgnore]
    public string Address => DeviceAddress.Format(Host, AuthPort);

    /// <summary>This record as the fleet audit sees it.</summary>
    public FleetRecord ToFleetRecord() =>
        new(Name.Length > 0 ? Name : Address, Host, AuthPort,
            ExpectedSerial.Length > 0 ? ExpectedSerial : null);

    [JsonIgnore]
    private int AuthPort => IsPanel ? SdkPort : HttpPort;
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
