using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The pure arithmetic both front ends describe a camera write from. The cases that matter are
/// the ones where the plan must <em>refuse</em>: a value the camera never declared, and a rate
/// the operator pinned.
/// </summary>
public class CameraSettingsPlanTests
{
    private static ChannelEncoding Track(int channel, StreamType stream = StreamType.Main,
        int width = 2688, int height = 1520, double? fps = 20.0, bool fullRate = false,
        string codec = "H.265", string quality = "VBR", int? vbr = 4096, int? gov = 50,
        bool? audio = false) =>
        new(Channel: channel,
            TrackId: channel * 100 + (int)stream + 1,
            Stream: stream,
            Enabled: true,
            CodecType: codec,
            Width: width,
            Height: height,
            FrameRateFps: fps,
            QualityControlType: quality,
            VbrUpperCapKbps: vbr,
            ConstantBitrateKbps: null,
            FixedQuality: null,
            FrameRateIsFull: fullRate,
            GovLength: gov,
            AudioEnabled: audio);

    private static EncodingOptions Options() => new()
    {
        Resolutions = [new Resolution(1920, 1080), new Resolution(2688, 1520)],
        FrameRates = [12.0, 15.0, 20.0, 25.0],
        SupportsFullFrameRate = true,
        Codecs = ["H.264", "H.265"],
        QualityControlTypes = ["CBR", "VBR"],
        Bitrate = new BitrateRange(32, 16384),
        GovLength = new ValueRange(1, 400, 50),
    };

    [Fact]
    public void A_request_that_matches_the_camera_is_no_change()
    {
        var plan = CameraSettingsPlan.Build(
            [Track(1)], new EncodingSettings(FrameRateFps: 20.0));

        Assert.Equal(ChannelPlanStatus.NoChange, plan.Channels[0].Status);
        Assert.Equal(0, plan.WriteCount);
    }

    [Fact]
    public void A_changed_field_is_planned_and_worded()
    {
        var plan = CameraSettingsPlan.Build(
            [Track(1)], new EncodingSettings(FrameRateFps: 15.0));

        var one = Assert.Single(plan.Writes);
        Assert.Equal(ChannelPlanStatus.WillChange, one.Status);
        Assert.Contains("fps 20.0 → 15.0", one.Fields);
    }

    [Fact]
    public void A_value_the_camera_never_declared_is_refused_not_clamped()
    {
        var options = new Dictionary<int, EncodingOptions> { [101] = Options() };

        var plan = CameraSettingsPlan.Build(
            [Track(1)], new EncodingSettings(FrameRateFps: 30.0), options);

        var one = plan.Channels[0];
        Assert.Equal(ChannelPlanStatus.OutOfRange, one.Status);
        Assert.Equal(0, plan.WriteCount);
        // The refusal names what the camera did declare — an operator who is told "no" is owed
        // the list that made it a no.
        Assert.Contains("25.0", one.Problem);
    }

    [Fact]
    public void A_bitrate_outside_the_declared_range_is_refused()
    {
        var options = new Dictionary<int, EncodingOptions> { [101] = Options() };

        var plan = CameraSettingsPlan.Build(
            [Track(1)], new EncodingSettings(BitrateKbps: 99999), options);

        Assert.Equal(ChannelPlanStatus.OutOfRange, plan.Channels[0].Status);
        Assert.Contains("32–16384", plan.Channels[0].Problem);
    }

    [Fact]
    public void An_empty_option_list_means_the_device_did_not_say_so_anything_is_allowed()
    {
        // The load-bearing distinction: "declared nothing" is not "allows nothing". A camera
        // whose capabilities document is silent must not become uneditable.
        var options = new Dictionary<int, EncodingOptions> { [101] = new() };

        var plan = CameraSettingsPlan.Build(
            [Track(1)], new EncodingSettings(FrameRateFps: 30.0), options);

        Assert.Equal(ChannelPlanStatus.WillChange, plan.Channels[0].Status);
    }

    [Fact]
    public void A_pinned_camera_is_held_back_from_a_bitrate_change()
    {
        var pins = new ChannelPinSet("192.0.2.10:80", "SERIAL1",
            [new ChannelPin(1, 4096, "Front Door", "customer asked", DateTimeOffset.UtcNow)]);

        var plan = CameraSettingsPlan.Build(
            [Track(1)], new EncodingSettings(BitrateKbps: 2048), pins: pins);

        var one = plan.Channels[0];
        Assert.Equal(ChannelPlanStatus.Pinned, one.Status);
        Assert.Equal(0, plan.WriteCount);
        Assert.Contains("pinned", one.Problem);
    }

    [Fact]
    public void A_pin_does_not_block_a_change_that_is_not_about_the_rate()
    {
        // A pin is a promise about the camera's bitrate, which is all the retention planner
        // was ever going to decide for it. Blocking a resolution change would be a pin
        // meaning something nobody agreed to.
        var pins = new ChannelPinSet("192.0.2.10:80", "SERIAL1",
            [new ChannelPin(1, 4096, "Front Door", null, DateTimeOffset.UtcNow)]);

        var plan = CameraSettingsPlan.Build(
            [Track(1)], new EncodingSettings(Resolution: new Resolution(1920, 1080)),
            pins: pins);

        Assert.Equal(ChannelPlanStatus.WillChange, plan.Channels[0].Status);
    }

    [Fact]
    public void Ignore_pins_writes_the_pinned_camera_anyway()
    {
        var pins = new ChannelPinSet("192.0.2.10:80", "SERIAL1",
            [new ChannelPin(1, 4096, "Front Door", null, DateTimeOffset.UtcNow)]);

        var plan = CameraSettingsPlan.Build(
            [Track(1)], new EncodingSettings(BitrateKbps: 2048), pins: pins, ignorePins: true);

        Assert.Equal(ChannelPlanStatus.WillChange, plan.Channels[0].Status);
    }

    [Fact]
    public void Full_frame_rate_is_a_change_even_when_the_resolved_rate_matches()
    {
        // A camera sitting at 20 fps because somebody typed 20 is not the same camera as one
        // told to follow its own maximum, which the firmware re-resolves per resolution.
        var plan = CameraSettingsPlan.Build(
            [Track(1, fps: 20.0, fullRate: false)], new EncodingSettings(FullFrameRate: true));

        Assert.Equal(ChannelPlanStatus.WillChange, plan.Channels[0].Status);
        Assert.Contains("→ full", plan.Channels[0].Fields[0]);
    }

    [Fact]
    public void A_camera_already_on_full_frame_rate_is_no_change()
    {
        var plan = CameraSettingsPlan.Build(
            [Track(1, fps: 30.0, fullRate: true)], new EncodingSettings(FullFrameRate: true));

        Assert.Equal(ChannelPlanStatus.NoChange, plan.Channels[0].Status);
    }

    [Fact]
    public void Full_frame_rate_is_refused_where_the_camera_does_not_offer_it()
    {
        var options = new Dictionary<int, EncodingOptions>
        {
            [101] = new() { FrameRates = [12.0, 20.0], SupportsFullFrameRate = false },
        };

        var plan = CameraSettingsPlan.Build(
            [Track(1)], new EncodingSettings(FullFrameRate: true), options);

        Assert.Equal(ChannelPlanStatus.OutOfRange, plan.Channels[0].Status);
    }

    [Fact]
    public void Several_fields_in_one_request_are_all_reported()
    {
        var plan = CameraSettingsPlan.Build(
            [Track(1)],
            new EncodingSettings(
                Resolution: new Resolution(1920, 1080),
                CodecType: "H.264",
                BitrateKbps: 2048,
                GovLength: 40,
                AudioEnabled: true));

        var fields = plan.Channels[0].Fields;
        Assert.Equal(5, fields.Count);
        Assert.Contains(fields, f => f.StartsWith("resolution"));
        Assert.Contains(fields, f => f.StartsWith("audio"));
    }

    [Fact]
    public void Sub_streams_are_planned_alongside_main_streams()
    {
        var plan = CameraSettingsPlan.Build(
            [Track(1, StreamType.Main), Track(1, StreamType.Sub, 704, 480, 12.0)],
            new EncodingSettings(CodecType: "H.264"));

        Assert.Equal(2, plan.WriteCount);
        Assert.Contains(plan.Channels, c => c.Stream == StreamType.Sub);
    }

    [Fact]
    public void Held_back_channels_are_never_silently_dropped_from_the_summary()
    {
        var options = new Dictionary<int, EncodingOptions>
        {
            [101] = Options(),
            // A second camera on the same recorder that tops out lower — the ordinary mixed
            // fleet, and the reason a plan is per channel rather than per device.
            [201] = new() { FrameRates = [12.0, 15.0] },
        };

        var plan = CameraSettingsPlan.Build(
            [Track(1, fps: 20.0), Track(2, fps: 12.0)],
            new EncodingSettings(FrameRateFps: 25.0), options);

        // ch1 declares 25 and moves; ch2 does not and is refused rather than clamped.
        Assert.Equal(1, plan.WriteCount);
        Assert.Single(plan.HeldBack);
        Assert.Contains("held back", plan.Summary);
    }

    [Fact]
    public void Channel_names_reach_the_report()
    {
        var names = new Dictionary<int, string> { [1] = "Front Door" };

        var plan = CameraSettingsPlan.Build(
            [Track(1)], new EncodingSettings(FrameRateFps: 15.0), names: names);

        Assert.Contains("Front Door", plan.Channels[0].Describe());
    }

    [Theory]
    [InlineData("1920x1080", 1920, 1080)]
    [InlineData("1920X1080", 1920, 1080)]
    public void Resolution_parses_both_spellings(string text, int w, int h)
    {
        var parsed = Resolution.Parse(text);
        Assert.NotNull(parsed);
        Assert.Equal(new Resolution(w, h), parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1920")]
    [InlineData("x1080")]
    [InlineData("1920x")]
    [InlineData("axb")]
    [InlineData("0x0")]
    public void Resolution_refuses_what_is_not_one(string text) =>
        Assert.Null(Resolution.Parse(text));
}
