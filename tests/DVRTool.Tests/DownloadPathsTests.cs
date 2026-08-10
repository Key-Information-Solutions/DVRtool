using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

public class DownloadPathsTests
{
    private const string AutoName = "ch3_20260730_140000-140100";

    [Fact]
    public void Plan_RemuxToMkvNamedOutput_DoesNotDownloadOntoItself()
    {
        // The reported bug: --out foo.mkv --remux made ffmpeg's input and output the
        // same path ("same as Input #0 - exiting"), and exit 1 left a raw stream
        // sitting at the operator's chosen name.
        var plan = DownloadPaths.Plan("foo.mkv", AutoName, RemuxContainer.Matroska);

        Assert.Equal("foo.mkv", plan.RemuxPath);
        Assert.Equal("foo.mkv", plan.FinalPath);
        Assert.NotEqual(plan.FinalPath, plan.DownloadPath);
        Assert.True(plan.NeedsRemux);
    }

    [Fact]
    public void Plan_RemuxToMp4NamedOutput_DoesNotDownloadOntoItself()
    {
        var plan = DownloadPaths.Plan("foo.mp4", AutoName, RemuxContainer.Mp4);

        Assert.Equal("foo.mp4", plan.FinalPath);
        Assert.NotEqual(plan.FinalPath, plan.DownloadPath);
    }

    [Fact]
    public void Plan_RawPathIsASiblingOfTheDestination()
    {
        // Same directory: AtomicDownload's rename must not cross a volume, and the
        // raw file must be findable next to where the operator expected the export.
        var plan = DownloadPaths.Plan(@"E:\exports\case-1182\clip.mkv", AutoName,
            RemuxContainer.Matroska);

        Assert.Equal(Path.GetDirectoryName(plan.FinalPath), Path.GetDirectoryName(plan.DownloadPath));
        Assert.DoesNotContain(".part", plan.DownloadPath);
    }

    [Fact]
    public void Plan_AutoNamed_UsesContainerExtension()
    {
        Assert.Equal(AutoName + ".mp4",
            DownloadPaths.Plan(null, AutoName, RemuxContainer.Mp4).FinalPath);
        Assert.Equal(AutoName + ".mkv",
            DownloadPaths.Plan(null, AutoName, RemuxContainer.Matroska).FinalPath);
    }

    [Fact]
    public void Plan_ExtensionlessOutput_GainsTheContainerExtension()
    {
        Assert.Equal("clip.mp4", DownloadPaths.Plan("clip", AutoName, RemuxContainer.Mp4).FinalPath);
        Assert.Equal("clip.mkv", DownloadPaths.Plan("clip", AutoName, RemuxContainer.Matroska).FinalPath);
    }

    [Fact]
    public void Plan_NoRemux_DownloadsStraightToTheTarget()
    {
        var explicitOut = DownloadPaths.Plan("clip.mpg", AutoName, null);
        Assert.Equal("clip.mpg", explicitOut.DownloadPath);
        Assert.Null(explicitOut.RemuxPath);
        Assert.False(explicitOut.NeedsRemux);

        // Auto-named: extensionless until the bytes are sniffed, so the CLI never
        // commits to a container the device did not send.
        var auto = DownloadPaths.Plan(null, AutoName, null);
        Assert.Equal(AutoName, auto.FinalPath);
        Assert.Equal("", Path.GetExtension(auto.FinalPath));
    }

    [Fact]
    public void ResolveContainer_DefaultsToMp4()
    {
        Assert.Equal(RemuxContainer.Mp4, DownloadPaths.ResolveContainer("", null));
        Assert.Equal(RemuxContainer.Mp4, DownloadPaths.ResolveContainer(null, "clip.mp4"));
        Assert.Equal(RemuxContainer.Mp4, DownloadPaths.ResolveContainer("", "clip"));
    }

    [Fact]
    public void ResolveContainer_FollowsTheRequestedOutputExtension()
    {
        Assert.Equal(RemuxContainer.Matroska, DownloadPaths.ResolveContainer("", "clip.mkv"));
        Assert.Equal(RemuxContainer.Matroska, DownloadPaths.ResolveContainer("", "clip.MKV"));
    }

    [Theory]
    [InlineData("mkv", RemuxContainer.Matroska)]
    [InlineData("matroska", RemuxContainer.Matroska)]
    [InlineData("MKV", RemuxContainer.Matroska)]
    [InlineData("mp4", RemuxContainer.Mp4)]
    public void ResolveContainer_ExplicitValueWins(string value, RemuxContainer expected)
    {
        // Explicit beats inference, even when the two disagree.
        Assert.Equal(expected, DownloadPaths.ResolveContainer(value, "clip.mkv"));
        Assert.Equal(expected, DownloadPaths.ResolveContainer(value, "clip.mp4"));
    }

    [Fact]
    public void ResolveContainer_UnknownValue_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => DownloadPaths.ResolveContainer("avi", null));
        Assert.Contains("mp4 or mkv", ex.Message);
    }

    [Fact]
    public void StagingPathFor_IsASiblingThatCollidesWithNothing()
    {
        var plan = DownloadPaths.Plan(@"E:\exports\case-1182\clip.mkv", AutoName,
            RemuxContainer.Matroska);
        string staging = DownloadPaths.StagingPathFor(plan.FinalPath);

        // The rename into place must not cross a volume.
        Assert.Equal(Path.GetDirectoryName(plan.FinalPath), Path.GetDirectoryName(staging));
        // Both the raw download and the destination exist while ffmpeg is writing.
        Assert.NotEqual(plan.FinalPath, staging);
        Assert.NotEqual(plan.DownloadPath, staging);
        Assert.NotEqual(DownloadPaths.RawPathFor(plan.FinalPath), staging);
    }

    [Fact]
    public void ExplicitContainerContradictsName_FlagsMp4WrittenIntoAnMkvName()
    {
        // "--out clip.mkv --remux mp4": real MP4 bytes under a name that says Matroska.
        Assert.True(DownloadPaths.ExplicitContainerContradictsName(
            "mp4", "clip.mkv", RemuxContainer.Mp4));
        Assert.True(DownloadPaths.ExplicitContainerContradictsName(
            "mkv", "clip.mp4", RemuxContainer.Matroska));
        Assert.True(DownloadPaths.ExplicitContainerContradictsName(
            "matroska", "clip.MP4", RemuxContainer.Matroska));
    }

    [Fact]
    public void ExplicitContainerContradictsName_BareRemuxNeverWarns()
    {
        // With no value the --out extension picks the container, so the two agree by
        // construction — warning there would make the common case noisy.
        Assert.False(DownloadPaths.ExplicitContainerContradictsName(
            "", "clip.mkv", DownloadPaths.ResolveContainer("", "clip.mkv")));
        Assert.False(DownloadPaths.ExplicitContainerContradictsName(
            null, "clip.mp4", RemuxContainer.Mp4));
    }

    [Fact]
    public void ExplicitContainerContradictsName_AgreementAndSilentNamesDoNotWarn()
    {
        Assert.False(DownloadPaths.ExplicitContainerContradictsName(
            "mp4", "clip.mp4", RemuxContainer.Mp4));
        // No --out, or an --out with no extension: nothing claimed, nothing to contradict.
        Assert.False(DownloadPaths.ExplicitContainerContradictsName("mp4", null, RemuxContainer.Mp4));
        Assert.False(DownloadPaths.ExplicitContainerContradictsName("mkv", "clip", RemuxContainer.Matroska));
    }

    [Fact]
    public void EnsureNotOverwriting_ExistingFile_ThrowsNamingThePath()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mp4");
        File.WriteAllText(path, "a previous export");
        try
        {
            var ex = Assert.Throws<ArgumentException>(
                () => DownloadPaths.EnsureNotOverwriting(path, force: false));

            Assert.Contains(Path.GetFullPath(path), ex.Message);
            Assert.Contains("--force", ex.Message);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EnsureNotOverwriting_ForceOrMissingFile_Allows()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mp4");
        DownloadPaths.EnsureNotOverwriting(path, force: false);

        File.WriteAllText(path, "a previous export");
        try
        {
            DownloadPaths.EnsureNotOverwriting(path, force: true);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
