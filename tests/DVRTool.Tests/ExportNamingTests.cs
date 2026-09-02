using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

public class ExportNamingTests
{
    private const string Base = "ch3_20260721_080000";

    [Fact]
    public void SuggestedFileName_RawHikvisionExport_IsNotCalledMp4()
    {
        // The defect this exists to prevent: the GUI used to suggest ".mp4" for every
        // Hikvision export, and the device sends an MPEG program stream. The operator
        // hands a client a file Windows Photos refuses to open.
        Assert.Equal($"{Base}.mpg", ExportNaming.SuggestedFileName(Base, Vendor.Hikvision, remux: false));
    }

    [Fact]
    public void SuggestedFileName_RawDahuaExport_KeepsTheDavName()
    {
        Assert.Equal($"{Base}.dav", ExportNaming.SuggestedFileName(Base, Vendor.Dahua, remux: false));
    }

    [Fact]
    public void SuggestedFileName_Remuxed_IsMp4ForEveryVendor()
    {
        // After a remux the container is ours, not the device's, so the vendor stops
        // mattering — and MP4 is the copy most likely to play on an unprepared machine.
        Assert.Equal($"{Base}.mp4", ExportNaming.SuggestedFileName(Base, Vendor.Hikvision, remux: true));
        Assert.Equal($"{Base}.mp4", ExportNaming.SuggestedFileName(Base, Vendor.Dahua, remux: true));
    }

    [Fact]
    public void SuggestedName_AgreesWithTheContainerResolvedFromIt()
    {
        // The suggestion and the plan have to agree: the dialog appends the suggested
        // extension, and DownloadPaths then re-derives the container from that name.
        // If these ever disagreed, a "remux to MP4" would silently write Matroska.
        foreach (var vendor in new[] { Vendor.Hikvision, Vendor.Dahua })
        {
            string suggested = ExportNaming.SuggestedFileName(Base, vendor, remux: true);
            Assert.Equal(RemuxContainer.Mp4, DownloadPaths.ResolveContainer(null, suggested));
        }
    }

    [Fact]
    public void SuggestedName_DoesNotContradictWhatTheRemuxWrites()
    {
        string suggested = ExportNaming.SuggestedFileName(Base, Vendor.Hikvision, remux: true);
        var container = DownloadPaths.ResolveContainer(null, suggested);
        Assert.False(ContainerSniffer.ExtensionContradicts(
            suggested, DownloadPaths.MediaContainerFor(container)));
    }

    [Fact]
    public void RawSuggestedName_MatchesWhatTheDeviceActuallySends()
    {
        // Nothing warns after a raw export whose name was right all along: a Hikvision
        // download really is an MPEG program stream, so ".mpg" is not a contradiction.
        string hik = ExportNaming.SuggestedFileName(Base, Vendor.Hikvision, remux: false);
        Assert.False(ContainerSniffer.ExtensionContradicts(hik, MediaContainer.MpegProgramStream));

        string dahua = ExportNaming.SuggestedFileName(Base, Vendor.Dahua, remux: false);
        Assert.False(ContainerSniffer.ExtensionContradicts(dahua, MediaContainer.Dhav));
    }

    [Fact]
    public void RawContainerFor_KnowsWhatEachVendorSends()
    {
        Assert.Equal(MediaContainer.MpegProgramStream, ExportNaming.RawContainerFor(Vendor.Hikvision));
        Assert.Equal(MediaContainer.Dhav, ExportNaming.RawContainerFor(Vendor.Dahua));
        // Nx's /media/ export is requested as Matroska, so the raw file is honestly .mkv.
        Assert.Equal(MediaContainer.Matroska, ExportNaming.RawContainerFor(Vendor.NxWitness));
        Assert.Equal($"{Base}.mkv", ExportNaming.SuggestedFileName(Base, Vendor.NxWitness, remux: false));
    }

    [Fact]
    public void SaveFilter_OffersTheExtensionTheExportWillActuallyHave()
    {
        // A Save-As filter appends its own extension when the operator types none, so a
        // filter that led with the wrong one would re-introduce the mislabeling.
        Assert.StartsWith("MP4 video|*.mp4", ExportNaming.SaveFilter(Vendor.Hikvision, remux: true));
        Assert.Contains("*.mpg", ExportNaming.SaveFilter(Vendor.Hikvision, remux: false));
        Assert.Contains("*.dav", ExportNaming.SaveFilter(Vendor.Dahua, remux: false));
    }

    [Fact]
    public void SaveFilter_AlwaysKeepsAnAllFilesEscapeHatch()
    {
        foreach (bool remux in new[] { true, false })
            foreach (var vendor in Enum.GetValues<Vendor>())
                Assert.EndsWith("All files|*.*", ExportNaming.SaveFilter(vendor, remux));
    }

    [Fact]
    public void SaveFilter_IsWellFormed()
    {
        // WPF throws on a filter whose "label|pattern" pairs do not balance, and it
        // throws when the dialog opens — in front of the operator, mid-export.
        foreach (bool remux in new[] { true, false })
            foreach (var vendor in Enum.GetValues<Vendor>())
                Assert.Equal(0, ExportNaming.SaveFilter(vendor, remux).Split('|').Length % 2);
    }
}
