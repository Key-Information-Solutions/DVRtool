using System.Text.Json;
using DVRTool.Core;
using DVRTool.Vendors.NxWitness;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The two per-camera "which tracks reach the disk" switches: parsing them off a device
/// document, and the distinction the report rests on — a camera with no second stream is not
/// the same as one told not to record the second stream it has.
/// </summary>
/// <remarks>
/// The fixtures are trimmed from the live Site D device list (2026-09-08, DW Spectrum
/// 6.1.1.42624): audio is <c>options.isAudioEnabled</c>, capability is
/// <c>parameters.isAudioSupported</c> as 1/0, and <c>dontRecordSecondaryStream</c> is absent
/// on every camera that records both streams.
/// </remarks>
public class NxRecordingOptionsTests
{
    private static NxCamera Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return NxCamera.Parse(doc.RootElement)!;
    }

    /// <summary>A primary and a secondary, as the live device list spells them.</summary>
    private const string TwoStreams =
        "\"mediaStreams\":[{\"codec\":27,\"encoderIndex\":0,\"resolution\":\"2560x1440\"}," +
        "{\"codec\":27,\"encoderIndex\":1,\"resolution\":\"640x480\"}]";

    [Fact]
    public void Camera_ReadsAudioFromOptions_AndCapabilityFromTheNumericProperty()
    {
        var cam = Parse($$"""
            {"id":"{aaaaaaaa-0000-0000-0000-000000000010}","name":"SiteD-Cashier",
             "options":{"isAudioEnabled":false,"isDualStreamingDisabled":false},
             "parameters":{"isAudioSupported":1},
             {{TwoStreams}}}
            """);

        Assert.False(cam.AudioEnabled);
        Assert.True(cam.AudioSupported);   // 1, not true — the live spelling
    }

    [Fact]
    public void Camera_TreatsAbsentAudioSupportAsUnknownAndFalse_ButHonoursTheForcedOverride()
    {
        var plain = Parse("""
            {"id":"{aaaaaaaa-0000-0000-0000-000000000011}","name":"SiteD-Finance 1",
             "options":{"isAudioEnabled":false},"parameters":{"isAudioSupported":0}}
            """);
        Assert.False(plain.AudioSupported);

        // forcedIsAudioSupported is the operator's override for a camera whose ONVIF answer
        // was wrong, and wins over the probed value.
        var forced = Parse("""
            {"id":"{aaaaaaaa-0000-0000-0000-000000000012}","name":"SiteD-Break Room",
             "options":{"isAudioEnabled":true},
             "parameters":{"isAudioSupported":0,"forcedIsAudioSupported":1}}
            """);
        Assert.True(forced.AudioEnabled);
        Assert.True(forced.AudioSupported);
    }

    [Fact]
    public void Options_AbsentDontRecordSecondary_MeansTheStreamIsRecorded()
    {
        // The live default on all 64 Site D cameras: the property is simply not there.
        var cam = Parse($$"""
            {"id":"{aaaaaaaa-0000-0000-0000-000000000013}","name":"SiteD-Service Drive",
             "options":{"isAudioEnabled":false,"isDualStreamingDisabled":false},
             {{TwoStreams}}}
            """);

        Assert.False(cam.DontRecordSecondary);
        var row = Describe(7, cam);
        Assert.True(row.RecordSecondary);
        Assert.True(row.SecondaryAvailable);
        Assert.Equal("records secondary, audio off", row.Summary);
    }

    [Fact]
    public void Options_NoSecondStream_ReportsNullRatherThanNotRecorded()
    {
        // "there is nothing to record" and "told not to record it" must not print the same:
        // only the second is a setting somebody chose.
        var noStream = Parse("""
            {"id":"{aaaaaaaa-0000-0000-0000-000000000014}","name":"Single",
             "options":{"isAudioEnabled":false},
             "mediaStreams":[{"codec":27,"encoderIndex":0,"resolution":"1920x1080"}]}
            """);
        var row = Describe(1, noStream);
        Assert.Null(row.RecordSecondary);
        Assert.False(row.SecondaryAvailable);
        Assert.Contains("no second stream", row.Summary);

        // Dual streaming switched off is the same story: the stream is not being pulled.
        var dualOff = Parse($$"""
            {"id":"{aaaaaaaa-0000-0000-0000-000000000015}","name":"DualOff",
             "options":{"isAudioEnabled":false,"isDualStreamingDisabled":true},
             {{TwoStreams}}}
            """);
        Assert.Null(Describe(2, dualOff).RecordSecondary);
    }

    [Fact]
    public void Options_DontRecordSecondarySet_ReportsNotRecorded()
    {
        var cam = Parse($$"""
            {"id":"{aaaaaaaa-0000-0000-0000-000000000016}","name":"Quiet",
             "options":{"isAudioEnabled":false,"isDualStreamingDisabled":false},
             "parameters":{"dontRecordSecondaryStream":"1"},
             {{TwoStreams}}}
            """);

        var row = Describe(3, cam);
        Assert.False(row.RecordSecondary);
        Assert.True(row.SecondaryAvailable);   // it exists; it is just not archived
        Assert.Equal("no secondary, audio off", row.Summary);
    }

    [Fact]
    public void Summary_NamesAudioSeparatelyFromAudioSupport()
    {
        var withAudio = Parse($$"""
            {"id":"{aaaaaaaa-0000-0000-0000-000000000017}","name":"Loud",
             "options":{"isAudioEnabled":true,"isDualStreamingDisabled":false},
             "parameters":{"isAudioSupported":1},
             {{TwoStreams}}}
            """);
        var lively = Describe(4, withAudio);
        Assert.Equal("records secondary, audio on", lively.Summary);
        Assert.True(lively.AudioReachesDisk);   // capture on, archive bar clear
    }

    [Fact]
    public void Camera_ReadsTheExpertArchiveBarSeparatelyFromCapture()
    {
        // DW's Expert tab "Do not record audio" is its own property, absent by default, and
        // written by the DW client as the number 1 — confirmed live 2026-09-08 by ticking the
        // box on SiteD-Cashier and diffing the device document.
        var barred = Parse($$"""
            {"id":"{aaaaaaaa-0000-0000-0000-000000000018}","name":"SiteD-Cashier",
             "options":{"isAudioEnabled":false,"isDualStreamingDisabled":false},
             "parameters":{"isAudioSupported":1,"dontRecordAudio":1},
             {{TwoStreams}}}
            """);
        Assert.True(barred.DontRecordAudio);
        Assert.False(barred.AudioEnabled);          // the two switches are independent
        var row = Describe(8, barred);
        Assert.True(row.AudioRecordingBlocked);
        Assert.False(row.AudioReachesDisk);
        Assert.Equal("records secondary, audio barred", row.Summary);

        // Absent means clear, exactly like the secondary-stream property.
        var plain = Parse($$"""
            {"id":"{aaaaaaaa-0000-0000-0000-000000000019}","name":"Plain",
             "options":{"isAudioEnabled":false,"isDualStreamingDisabled":false},
             "parameters":{"isAudioSupported":1},
             {{TwoStreams}}}
            """);
        Assert.False(plain.DontRecordAudio);
        Assert.False(Describe(9, plain).AudioRecordingBlocked);
    }

    [Fact]
    public void AudioReachesDisk_NeedsCaptureOnAndTheBarClear()
    {
        // The bar is the durable switch: it keeps audio off the disk even with capture on,
        // which is the whole reason it is worth setting on a camera whose capture is already
        // off today.
        var capturingButBarred = Parse($$"""
            {"id":"{aaaaaaaa-0000-0000-0000-00000000001a}","name":"Barred",
             "options":{"isAudioEnabled":true,"isDualStreamingDisabled":false},
             "parameters":{"isAudioSupported":1,"dontRecordAudio":1},
             {{TwoStreams}}}
            """);
        var row = Describe(10, capturingButBarred);
        Assert.True(row.AudioEnabled);
        Assert.False(row.AudioReachesDisk);
        Assert.Equal("records secondary, audio barred (capture on)", row.Summary);
    }

    /// <summary>
    /// <c>NxWitnessClient.Describe</c> is private; this mirrors it so the mapping rules above
    /// are pinned by a test without opening the client's internals to the test assembly.
    /// Kept deliberately identical — if one changes, this fails and says so.
    /// </summary>
    private static CameraRecordingOptions Describe(int channel, NxCamera camera)
    {
        bool available = camera.Secondary is not null && !camera.DualStreamingDisabled;
        return new CameraRecordingOptions(
            channel, camera.Name,
            RecordSecondary: available ? !camera.DontRecordSecondary : null,
            SecondaryAvailable: available,
            AudioEnabled: camera.AudioEnabled,
            AudioSupported: camera.AudioSupported,
            AudioRecordingBlocked: camera.DontRecordAudio);
    }
}
