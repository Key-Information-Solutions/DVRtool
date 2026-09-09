using DVRTool.Core;
using DVRTool.Vendors.Dahua;
using DVRTool.Vendors.Hikvision;
using DVRTool.Vendors.NxWitness;

namespace DVRTool.App;

/// <summary>
/// Turns a saved record into a vendor client. It lives here rather than on
/// <see cref="SavedDevice"/> because the record itself is in Core, which knows no vendors —
/// the CLI builds its clients from the same three constructors in <c>Program.cs</c>.
/// </summary>
internal static class SavedDeviceClients
{
    /// <summary>
    /// A recorder's vendor client. Meaningless for a panel — those are driven through
    /// <see cref="SavedDevice.ToPanelConnection"/> — so calling this on one is a caller bug,
    /// not a condition to degrade around.
    /// </summary>
    public static INvrClient CreateClient(this SavedDevice device)
    {
        if (device.IsPanel)
            throw new InvalidOperationException(
                $"'{device.Name}' is a door panel — it has no NVR client.");
        return device.VendorKind switch
        {
            Vendor.Dahua => new DahuaClient(device.ToConnection()),
            Vendor.NxWitness => new NxWitnessClient(device.ToConnection()),
            _ => new HikvisionClient(device.ToConnection()),
        };
    }
}
