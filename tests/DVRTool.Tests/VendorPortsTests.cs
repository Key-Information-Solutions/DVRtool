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

    [Fact]
    public void Nx_HasNoSdkPort_AndPutsEverythingOn7001()
    {
        // A Network Optix server multiplexes HTTPS, HTTP and RTSP on one listener, and
        // there is no private-SDK port to forward at all — so the web and RTSP defaults are
        // the same number and the SDK slot is 0, which nothing may dial.
        Assert.False(VendorPorts.HasSdkPort(Vendor.NxWitness));
        Assert.Equal(0, VendorPorts.Sdk(Vendor.NxWitness));
        Assert.Equal(7001, VendorPorts.NxWitnessServer);
        Assert.Equal(7001, VendorPorts.Web(Vendor.NxWitness, tls: true));
        Assert.Equal(7001, VendorPorts.Web(Vendor.NxWitness, tls: false));
        Assert.Equal(7001, VendorPorts.Rtsp(Vendor.NxWitness));
        Assert.True(VendorPorts.DefaultsToTls(Vendor.NxWitness));

        // The appliance vendors keep the numbers they always had.
        Assert.True(VendorPorts.HasSdkPort(Vendor.Hikvision));
        Assert.True(VendorPorts.HasSdkPort(Vendor.Dahua));
        Assert.Equal(80, VendorPorts.Web(Vendor.Dahua, tls: false));
        Assert.Equal(443, VendorPorts.Web(Vendor.Hikvision, tls: true));
        Assert.Equal(554, VendorPorts.Rtsp(Vendor.Dahua));
        Assert.False(VendorPorts.DefaultsToTls(Vendor.Hikvision));
    }

    [Theory]
    [InlineData("hikvision", Vendor.Hikvision)]
    [InlineData("HIK", Vendor.Hikvision)]
    [InlineData("dahua", Vendor.Dahua)]
    [InlineData("amcrest", Vendor.Dahua)]
    [InlineData("nx", Vendor.NxWitness)]
    [InlineData("dwspectrum", Vendor.NxWitness)]
    [InlineData("dw", Vendor.NxWitness)]
    [InlineData(" NxWitness ", Vendor.NxWitness)]
    public void VendorNames_ParseEverySpelling(string text, Vendor expected)
    {
        Assert.True(VendorNames.TryParse(text, out var vendor));
        Assert.Equal(expected, vendor);
    }

    [Fact]
    public void VendorNames_RoundTripTheirKeys_AndRejectNonsense()
    {
        foreach (var vendor in Enum.GetValues<Vendor>())
        {
            Assert.True(VendorNames.TryParse(VendorNames.Key(vendor), out var parsed));
            Assert.Equal(vendor, parsed);
            Assert.Contains(VendorNames.Key(vendor), VendorNames.CliChoices.Split('|'));
        }
        Assert.False(VendorNames.TryParse("axis", out _));
        Assert.False(VendorNames.TryParse(null, out _));
        Assert.Equal("DW Spectrum / Nx Witness", VendorNames.Display(Vendor.NxWitness));
    }
}
