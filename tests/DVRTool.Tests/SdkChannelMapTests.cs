using DVRTool.Core;
using DVRTool.Vendors.HikvisionSdk;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The display-channel to SDK-channel translation, which has no visible failure mode: ask a
/// recorder for the wrong channel number and it either sends nothing or sends a different
/// camera. Both look like a network problem, so the arithmetic is pinned here.
/// </summary>
public class SdkChannelMapTests
{
    /// <summary>
    /// A device-info buffer as <c>NET_DVR_Login_V30</c> fills it in. Offsets are the ones
    /// <see cref="SdkChannelMap"/> reads, verified against a live DS-7716NI-I4/16P(B).
    /// </summary>
    private static byte[] DeviceInfo(string serial = "", byte chanNum = 0, byte startChan = 1,
        byte ipChanNum = 0, byte startDChan = 0, byte highDChanNum = 0)
    {
        var raw = new byte[SdkChannelMap.DeviceInfoBufferSize];
        System.Text.Encoding.ASCII.GetBytes(serial).CopyTo(raw, 0);
        raw[52] = chanNum;
        raw[53] = startChan;
        raw[55] = ipChanNum;
        raw[66] = startDChan;
        raw[68] = highDChanNum;
        return raw;
    }

    [Fact]
    public void Reads_the_live_NVR_layout()
    {
        // Exactly what the DS-7716NI-I4/16P(B) on V4.61.030 reports.
        var map = SdkChannelMap.From(DeviceInfo(ipChanNum: 16, startDChan: 33));

        Assert.Equal(0, map.AnalogCount);
        Assert.Equal(16, map.DigitalCount);
        Assert.Equal(33, map.DigitalStart);
        Assert.Equal(16, map.TotalChannels);
    }

    [Fact]
    public void Pure_NVR_offsets_display_channels_to_the_digital_block()
    {
        var map = SdkChannelMap.From(DeviceInfo(ipChanNum: 16, startDChan: 33));

        // The whole point: display channel 1 is device channel 33, not 1.
        Assert.Equal(33, map.ToSdkChannel(1));
        Assert.Equal(34, map.ToSdkChannel(2));
        Assert.Equal(48, map.ToSdkChannel(16));
    }

    [Fact]
    public void Hybrid_DVR_numbers_analog_first_then_IP()
    {
        // A 16-channel HUHI-series DVR with four IP channels bolted on.
        var map = SdkChannelMap.From(
            DeviceInfo(chanNum: 16, startChan: 1, ipChanNum: 4, startDChan: 17));

        Assert.Equal(1, map.ToSdkChannel(1));
        Assert.Equal(16, map.ToSdkChannel(16));
        // The first IP channel continues the display numbering but starts the digital block.
        Assert.Equal(17, map.ToSdkChannel(17));
        Assert.Equal(20, map.ToSdkChannel(20));
        Assert.Equal(20, map.TotalChannels);
    }

    [Fact]
    public void Analog_block_honours_a_non_default_start()
    {
        var map = SdkChannelMap.From(DeviceInfo(chanNum: 4, startChan: 5));

        Assert.Equal(5, map.ToSdkChannel(1));
        Assert.Equal(8, map.ToSdkChannel(4));
    }

    [Fact]
    public void Channel_counts_above_255_use_the_high_byte()
    {
        // byIPChanNum alone cannot express 300; dropping the high byte would refuse every
        // channel past 44 on a large NVR.
        var map = SdkChannelMap.From(
            DeviceInfo(ipChanNum: 44, startDChan: 33, highDChanNum: 1));

        Assert.Equal(300, map.DigitalCount);
        Assert.Equal(332, map.ToSdkChannel(300));
    }

    [Fact]
    public void A_channel_the_device_does_not_have_is_refused()
    {
        var map = SdkChannelMap.From(DeviceInfo(ipChanNum: 16, startDChan: 33));

        // Not passed through: the SDK accepts channel 49 and then silently sends nothing,
        // which reads as a firewall problem rather than a typo.
        var ex = Assert.Throws<NvrException>(() => map.ToSdkChannel(17));
        Assert.Contains("does not exist", ex.Message);
        Assert.Contains("16 IP channel(s) numbered from 33", ex.Message);
    }

    [Fact]
    public void Zero_and_negative_channels_are_rejected()
    {
        var map = SdkChannelMap.From(DeviceInfo(ipChanNum: 16, startDChan: 33));

        Assert.Throws<ArgumentOutOfRangeException>(() => map.ToSdkChannel(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.ToSdkChannel(-1));
    }

    [Fact]
    public void A_device_reporting_no_counts_gets_the_number_it_was_given()
    {
        // Old firmware that fills none of the channel fields in. Nothing is known to be
        // wrong, so the device answers for itself rather than being refused locally.
        var map = SdkChannelMap.From(DeviceInfo());

        Assert.Equal(1, map.ToSdkChannel(1));
        Assert.Equal(7, map.ToSdkChannel(7));
        Assert.Equal("no channel counts at all", map.Describe());
    }

    [Fact]
    public void A_digital_block_with_no_start_channel_is_not_guessed_at()
    {
        // byStartDChan 0 is the SDK's "invalid". Assuming 33 here would dial a channel the
        // device never claimed to have.
        var map = SdkChannelMap.From(DeviceInfo(chanNum: 8, startChan: 1, ipChanNum: 4));

        Assert.Equal(8, map.ToSdkChannel(8));
        Assert.Throws<NvrException>(() => map.ToSdkChannel(9));
    }

    [Fact]
    public void Serial_is_read_as_the_ISAPI_form_so_one_pin_covers_both_transports()
    {
        // Byte-identical to ISAPI's <serialNumber> on live hardware — that equality is what
        // lets an SDK login be checked against a pin an HTTP login created.
        const string serial = "DS-7716NI-I4/16P(B)0000000000AAAAAA0000000AAAA";
        var raw = DeviceInfo(serial, ipChanNum: 16, startDChan: 33);

        Assert.Equal(serial, SdkChannelMap.ReadSerial(raw));
    }

    [Fact]
    public void Serial_reading_stops_at_the_terminator_and_trims()
    {
        var raw = new byte[SdkChannelMap.DeviceInfoBufferSize];
        System.Text.Encoding.ASCII.GetBytes("ABC123  ").CopyTo(raw, 0);

        Assert.Equal("ABC123", SdkChannelMap.ReadSerial(raw));
    }

    [Fact]
    public void A_serial_less_device_reads_as_empty_rather_than_as_junk()
    {
        // Which is what makes the identity check report Unverifiable instead of pinning "".
        Assert.Equal("", SdkChannelMap.ReadSerial(new byte[SdkChannelMap.DeviceInfoBufferSize]));
        Assert.False(DeviceFingerprint
            .From(new DeviceInfo("", "", SdkChannelMap.ReadSerial(new byte[64]), ""))
            .IsUsable);
    }

    [Fact]
    public void A_buffer_too_short_to_hold_the_struct_is_refused()
    {
        Assert.Throws<ArgumentException>(() => SdkChannelMap.From(new byte[48]));
    }
}
