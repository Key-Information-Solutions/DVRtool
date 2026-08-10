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
    public void ContainerContradictsName_FlagsMp4WrittenIntoAnMkvName()
    {
        // "--out clip.mkv --remux mp4": real MP4 bytes under a name that says Matroska.
        Assert.True(DownloadPaths.ContainerContradictsName("clip.mkv", RemuxContainer.Mp4));
        Assert.True(DownloadPaths.ContainerContradictsName("clip.mp4", RemuxContainer.Matroska));
        Assert.True(DownloadPaths.ContainerContradictsName("clip.MP4", RemuxContainer.Matroska));
    }

    [Fact]
    public void ContainerContradictsName_FlagsAVendorNameThatLettingTheNameDecideDoesNotSave()
    {
        // The hole this replaced an "explicit only" check to close. Bare --remux (and every
        // GUI remux, which has no explicit form) resolves a name the resolver does not
        // recognize to the MP4 default — so "--out case.dav --remux" writes MP4 bytes under
        // a .dav name and used to say nothing at all. Only .mp4/.mkv/extensionless names
        // genuinely agree by construction.
        foreach (string name in new[] { "case.dav", "clip.mpg", "clip.mpeg", "clip.vob" })
        {
            var resolved = DownloadPaths.ResolveContainer(null, name);
            Assert.Equal(RemuxContainer.Mp4, resolved);
            Assert.True(DownloadPaths.ContainerContradictsName(name, resolved));
        }
    }

    [Fact]
    public void ContainerContradictsName_NamesThatPickTheirOwnContainerNeverWarn()
    {
        // Warning on these would make the common case noisy: the name chose the container,
        // so the two agree.
        foreach (string name in new[] { "clip.mkv", "clip.mp4", "clip.m4v", "clip.mov" })
            Assert.False(DownloadPaths.ContainerContradictsName(
                name, DownloadPaths.ResolveContainer(null, name)));
    }

    [Fact]
    public void ContainerContradictsName_AgreementAndSilentNamesDoNotWarn()
    {
        Assert.False(DownloadPaths.ContainerContradictsName("clip.mp4", RemuxContainer.Mp4));
        // No --out, or an --out with no extension: nothing claimed, nothing to contradict.
        Assert.False(DownloadPaths.ContainerContradictsName(null, RemuxContainer.Mp4));
        Assert.False(DownloadPaths.ContainerContradictsName("clip", RemuxContainer.Matroska));
    }

    [Fact]
    public void Plan_ExtensionlessNameWithRemux_LandsOnAPathTheOperatorWasNeverShown()
    {
        // Pins the divergence the GUI's overwrite permission has to respect: a Save-As
        // dialog prompts about "clip", the export lands on "clip.mp4". Permission to
        // replace the first is not permission to destroy the second.
        var plan = DownloadPaths.Plan("clip", AutoName, RemuxContainer.Mp4);

        Assert.Equal("clip.mp4", plan.FinalPath);
        Assert.NotEqual("clip", plan.FinalPath);

        // …and when the name does carry an extension, the two agree and the permission
        // the dialog collected is the permission the export needs.
        Assert.Equal("clip.mp4", DownloadPaths.Plan("clip.mp4", AutoName, RemuxContainer.Mp4).FinalPath);
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
            Assert.True(File.Exists(path));

            // The refusal itself names no flags — the WPF app surfaces the same sentence
            // and has none to offer. Front-end advice is appended by the front end.
            Assert.DoesNotContain("--", ex.Message);
            var cli = Assert.Throws<ArgumentException>(() => DownloadPaths.EnsureNotOverwriting(
                path, force: false, DownloadPaths.CliOverwriteAdvice));
            Assert.Contains("--force", cli.Message);
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
