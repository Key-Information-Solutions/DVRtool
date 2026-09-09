using DVRTool.Core;
using DVRTool.Vendors.Dahua;
using DVRTool.Vendors.Hikvision;
using DVRTool.Vendors.NxWitness;

namespace DVRTool.Cli;

/// <summary>
/// The one place the CLI turns a connection into a vendor client. It exists because
/// <c>config audit</c> opens a client per saved device rather than riding the single client
/// <c>Program.cs</c> builds from the command line.
/// </summary>
internal static class VendorClients
{
    public static INvrClient For(NvrConnection connection, Vendor vendor) => vendor switch
    {
        Vendor.Dahua => new DahuaClient(connection),
        Vendor.NxWitness => new NxWitnessClient(connection),
        _ => new HikvisionClient(connection),
    };

    /// <summary>A saved GUI device as a client — the fleet sweep's entry point.</summary>
    public static INvrClient For(SavedDevice device) =>
        For(device.ToConnection(), device.VendorKind);
}
