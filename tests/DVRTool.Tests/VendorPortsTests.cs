using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// Pins the SDK port numbers. They look like trivia, but they are the one connection value
/// the two vendors do not agree on, and getting one wrong does not fail loudly — it makes
/// the connectivity check call a healthy recorder dead, which sends an installer after a
/// firewall rule for a port the device never opened.
/// </summary>
public class VendorPortsTests
{
    [Fact]
    public void Sdk_IsHikvisionsServerPort_ForHikvision()
    {
        Assert.Equal(8000, VendorPorts.Sdk(Vendor.Hikvision));
        Assert.Equal(8000, VendorPorts.HikvisionSdk);
    }

    [Fact]
    public void Sdk_IsDahuasTcpPort_ForDahua()
    {
        // 37777, not 8000: Dahua puts DHNetSDK on its own "TCP Port" and nothing answers on
        // Hikvision's number. The companion UDP port (37778) is deliberately not modelled —
        // see docs/device-ports.md.
        Assert.Equal(37777, VendorPorts.Sdk(Vendor.Dahua));
        Assert.Equal(37777, VendorPorts.DahuaSdk);
    }

    [Fact]
    public void Sdk_DiffersBetweenVendors()
    {
        // The guard that matters: a single shared default cannot be right for both, so any
        // code path that picks one without consulting the vendor is wrong by construction.
        Assert.NotEqual(VendorPorts.Sdk(Vendor.Hikvision), VendorPorts.Sdk(Vendor.Dahua));
    }

    [Fact]
    public void NvrConnection_DefaultSdkPort_IsHikvisions()
    {
        // Documented as Hikvision's, because the record carries no vendor and cannot choose.
        // Anything building a Dahua connection has to set it from VendorPorts.Sdk.
        var conn = new NvrConnection { Host = "10.0.0.5", Username = "admin", Password = "x" };
        Assert.Equal(VendorPorts.HikvisionSdk, conn.SdkPort);
    }
}
