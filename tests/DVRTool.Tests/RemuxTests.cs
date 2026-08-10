using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

public class RemuxTests
{
    private static string[] Args(string? codec, RemuxContainer container, string output = "out") =>
        [.. Remux.BuildArguments("in.raw", output, container, codec)];

    private static string ValueOf(string[] args, string option) =>
        args[Array.IndexOf(args, option) + 1];

    [Fact]
    public void BuildArguments_HevcToMp4_TagsHvc1AndFrontLoadsMoov()
    {
        string[] args = Args("hevc", RemuxContainer.Mp4, "out.mp4");

        string[] expected =
        [
            "-y", "-loglevel", "error", "-i", "in.raw", "-c", "copy",
            "-tag:v", "hvc1", "-movflags", "+faststart", "-f", "mp4", "out.mp4",
        ];
        Assert.Equal(expected, args);
    }

    [Fact]
    public void BuildArguments_H264ToMp4_OmitsTheHevcTag()
    {
        // "-tag:v hvc1" on an H.264 stream is a hard ffmpeg failure:
        // "Tag hvc1 incompatible with output codec id '27' (avc1)".
        string[] args = Args("h264", RemuxContainer.Mp4, "out.mp4");

        Assert.DoesNotContain("hvc1", args);
        Assert.DoesNotContain("-tag:v", args);
        Assert.Contains("+faststart", args);
    }

    [Fact]
    public void BuildArguments_UnknownCodecToMp4_OmitsTheHevcTag()
    {
        Assert.DoesNotContain("hvc1", Args(null, RemuxContainer.Mp4, "out.mp4"));
    }

    [Fact]
    public void BuildArguments_Matroska_HasNoMp4OnlyOptions()
    {
        string[] args = Args("hevc", RemuxContainer.Matroska, "out.mkv");

        string[] expected =
        [
            "-y", "-loglevel", "error", "-i", "in.raw", "-c", "copy",
            "-f", "matroska", "out.mkv",
        ];
        Assert.Equal(expected, args);
    }

    [Theory]
    [InlineData("hevc")]
    [InlineData("h264")]
    [InlineData(null)]
    public void BuildArguments_AlwaysStreamCopies(string? codec)
    {
        // Evidence footage is never re-encoded, in any container.
        Assert.Equal("copy", ValueOf(Args(codec, RemuxContainer.Mp4), "-c"));
        Assert.Equal("copy", ValueOf(Args(codec, RemuxContainer.Matroska), "-c"));
    }

    [Fact]
    public void BuildArguments_ForcesTheMuxerRegardlessOfTheOutputName()
    {
        // The operator's chosen file name never decides the container we promise.
        Assert.Equal("mp4", ValueOf(Args("hevc", RemuxContainer.Mp4, "evidence.export"), "-f"));
        Assert.Equal("matroska",
            ValueOf(Args("hevc", RemuxContainer.Matroska, "evidence.export"), "-f"));
    }

    [Fact]
    public void ParseVideoCodec_ReadsFfmpegStderr()
    {
        const string stderr = """
            Input #0, mpeg, from 'frontdoor.raw':
              Duration: 00:01:01.86, start: 42627.302533, bitrate: 1713 kb/s
              Stream #0:0[0x1e0]: Video: hevc (Main), yuvj420p(pc, progressive), 2688x1520, 30 fps
            At least one output file must be specified
            """;

        Assert.Equal("hevc", Remux.ParseVideoCodec(stderr));
    }

    [Fact]
    public void ParseVideoCodec_ReadsH264AndIgnoresAudioOnlyInput()
    {
        Assert.Equal("h264", Remux.ParseVideoCodec(
            "  Stream #0:0[0x1e0]: Video: h264 (High) ([27][0][0][0] / 0x001B), yuv420p, 1920x1080"));
        Assert.Null(Remux.ParseVideoCodec(
            "  Stream #0:0[0xc0]: Audio: pcm_alaw, 8000 Hz, mono, s16, 64 kb/s"));
        Assert.Null(Remux.ParseVideoCodec(""));
    }

    [Fact]
    public void IsCodecTagRejection_RecognizesTheTagMismatch()
    {
        Assert.True(Remux.IsCodecTagRejection(
            "[mp4 @ 0000] Tag hvc1 incompatible with output codec id '27' (avc1)\n" +
            "Could not write header (incorrect codec parameters ?)"));
        Assert.False(Remux.IsCodecTagRejection(
            "out.mkv: Invalid argument\nError opening output files: Invalid argument"));
    }

    /// <summary>
    /// A 1x1 GIF: valid enough for ffmpeg to demux, and a codec Matroska stream-copies
    /// happily while the MP4 muxer rejects it — so the same bytes drive both a mux that
    /// fails at "write header" (after ffmpeg has opened and truncated whatever file it was
    /// pointed at) and one that succeeds and reaches the promotion.
    /// </summary>
    private static byte[] TinyGif() =>
    [
        .. "GIF89a"u8, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
        0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x44, 0x01, 0x00, 0x3B,
    ];

    [Fact]
    public async Task RemuxAsync_FailedMux_LeavesAPreviousExportUntouched()
    {
        // ffmpeg writing straight to the destination destroys a prior export before it
        // ever discovers it cannot mux — the destination is only ever written by the
        // promotion of a staging file that a completed ffmpeg produced.
        string dir = Directory.CreateTempSubdirectory("remux").FullName;
        string destination = Path.Combine(dir, "case-1182.mp4");
        string input = Path.Combine(dir, "case-1182.mp4.raw");
        const string prior = "the export already handed to the client";
        await File.WriteAllTextAsync(destination, prior);
        await File.WriteAllBytesAsync(input, TinyGif());
        try
        {
            var (ok, _) = await Remux.RemuxAsync(input, destination, RemuxContainer.Mp4);

            Assert.False(ok);
            Assert.True(File.Exists(destination));
            Assert.Equal(prior, await File.ReadAllTextAsync(destination));
            // …and the failure leaves no staging file to be mistaken for footage.
            Assert.Equal(
                [destination, input],
                Directory.GetFiles(dir).OrderBy(p => p.Length).ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RemuxAsync_DestinationAppearsDuringTheRemux_RefusesToPromoteOverIt()
    {
        // The caller's overwrite check runs before a download and remux that take minutes;
        // this is the file that lands at the destination inside that window.
        string dir = Directory.CreateTempSubdirectory("remux").FullName;
        string destination = Path.Combine(dir, "case-1182.mkv");
        string input = Path.Combine(dir, "case-1182.mkv.raw");
        const string prior = "an export another operator wrote while this one downloaded";
        await File.WriteAllBytesAsync(input, TinyGif());
        await File.WriteAllTextAsync(destination, prior);
        try
        {
            // Matroska stream-copies the GIF, so this mux genuinely succeeds and reaches
            // the promotion — the only place left that can still destroy the destination.
            var result = await Remux.RemuxAsync(input, destination, RemuxContainer.Matroska,
                force: false);

            Assert.False(result.Success);
            Assert.True(result.RefusedOverwrite);
            Assert.Contains(Path.GetFullPath(destination), result.Output);
            Assert.Contains("already exists", result.Output);
            Assert.Equal(prior, await File.ReadAllTextAsync(destination));
            // …and the refused remux is not left behind to be mistaken for the export.
            Assert.Equal(
                [destination, input],
                Directory.GetFiles(dir).OrderBy(p => p.Length).ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RemuxAsync_DestinationExistsWithForce_PromotesOverIt()
    {
        string dir = Directory.CreateTempSubdirectory("remux").FullName;
        string destination = Path.Combine(dir, "case-1182.mkv");
        string input = Path.Combine(dir, "case-1182.mkv.raw");
        await File.WriteAllBytesAsync(input, TinyGif());
        await File.WriteAllTextAsync(destination, "the copy the operator means to replace");
        try
        {
            var result = await Remux.RemuxAsync(input, destination, RemuxContainer.Matroska,
                force: true);

            Assert.True(result.Success);
            Assert.False(result.RefusedOverwrite);
            // EBML magic: the destination now holds the remux, not the file it replaced.
            byte[] promoted = await File.ReadAllBytesAsync(destination);
            Assert.Equal<byte[]>([0x1A, 0x45, 0xDF, 0xA3], promoted[..4]);
            Assert.Equal(
                [destination, input],
                Directory.GetFiles(dir).OrderBy(p => p.Length).ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RemuxAsync_SamePathInAndOut_FailsWithoutTouchingTheFile()
    {
        // ffmpeg would answer "Output ... same as Input #0 - exiting"; the guard makes
        // sure that mistake can never eat the only copy of the footage.
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mkv");
        await File.WriteAllBytesAsync(path, [0x00, 0x00, 0x01, 0xBA]);
        try
        {
            var (ok, output) = await Remux.RemuxAsync(path, path, RemuxContainer.Matroska);

            Assert.False(ok);
            Assert.Contains("itself", output);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
