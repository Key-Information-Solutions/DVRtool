using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

public class ContainerSnifferTests
{
    /// <summary>
    /// The real first 80 bytes of a DS-7716NI-I4/16P(B) (V4.61.030) ISAPI download:
    /// a 64-byte private header (magic "IMKH" at 0x18) ahead of the MPEG-PS pack
    /// header at 0x40 — and the device calls this an ".mp4".
    /// </summary>
    private static byte[] HikvisionDownloadHeader() =>
    [
        0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x13, 0x00, 0xDA, 0xA1, 0xD0, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x49, 0x4D, 0x4B, 0x48, 0x02, 0x01, 0x00, 0x00,
        0x02, 0x00, 0x05, 0x00, 0x00, 0x00, 0x01, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x81, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x01, 0xBA, 0x46, 0xD2, 0x1D, 0xE5, 0x34, 0x01, 0x02, 0x8F, 0x63, 0xFE, 0xFF, 0xFF,
    ];

    [Fact]
    public void Sniff_HikvisionWrappedDownload_IsProgramStream()
    {
        Assert.Equal(MediaContainer.MpegProgramStream, ContainerSniffer.Sniff(HikvisionDownloadHeader()));
    }

    [Fact]
    public void Sniff_BarePackHeader_IsProgramStream()
    {
        byte[] header = [0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00, 0x04, 0x01];
        Assert.Equal(MediaContainer.MpegProgramStream, ContainerSniffer.Sniff(header));
    }

    [Fact]
    public void Sniff_RealMp4_IsMp4()
    {
        // Firmware that genuinely returns MP4 must not be mislabeled by vendor guesswork.
        byte[] header = [0x00, 0x00, 0x00, 0x20, .. "ftypisom"u8, 0x00, 0x00, 0x02, 0x00];
        Assert.Equal(MediaContainer.Mp4, ContainerSniffer.Sniff(header));
    }

    [Fact]
    public void Sniff_MatroskaAndDhav()
    {
        Assert.Equal(MediaContainer.Matroska,
            ContainerSniffer.Sniff([0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x00, 0x00, 0x00]));
        Assert.Equal(MediaContainer.Dhav,
            ContainerSniffer.Sniff([.. "DHAV"u8, 0xFD, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void Sniff_UnknownBytes_IsUnknown()
    {
        Assert.Equal(MediaContainer.Unknown,
            ContainerSniffer.Sniff([.. "<html><body>login"u8]));
        Assert.Equal(MediaContainer.Unknown, ContainerSniffer.Sniff([]));
    }

    [Fact]
    public void SniffFile_ReadsShortFiles()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(path, HikvisionDownloadHeader());
            Assert.Equal(MediaContainer.MpegProgramStream, ContainerSniffer.SniffFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(MediaContainer.MpegProgramStream, ".mpg")]
    [InlineData(MediaContainer.Mp4, ".mp4")]
    [InlineData(MediaContainer.Matroska, ".mkv")]
    [InlineData(MediaContainer.Dhav, ".dav")]
    [InlineData(MediaContainer.Unknown, null)]
    public void ExtensionFor_MapsContainers(MediaContainer container, string? expected)
    {
        Assert.Equal(expected, ContainerSniffer.ExtensionFor(container));
    }

    [Fact]
    public void ExtensionContradicts_FlagsTheHikvisionMp4Lie()
    {
        // The bug this whole change exists for: a program stream named .mp4.
        Assert.True(ContainerSniffer.ExtensionContradicts("clip.mp4", MediaContainer.MpegProgramStream));
        Assert.True(ContainerSniffer.ExtensionContradicts("clip.MKV", MediaContainer.Dhav));

        Assert.False(ContainerSniffer.ExtensionContradicts("clip.mpg", MediaContainer.MpegProgramStream));
        Assert.False(ContainerSniffer.ExtensionContradicts("clip.mp4", MediaContainer.Mp4));
        // An extension we have no opinion about, or bytes we could not identify,
        // is not something to nag about.
        Assert.False(ContainerSniffer.ExtensionContradicts("clip.evidence", MediaContainer.MpegProgramStream));
        Assert.False(ContainerSniffer.ExtensionContradicts("clip.mp4", MediaContainer.Unknown));
    }
}
