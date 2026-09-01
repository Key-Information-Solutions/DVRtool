using DVRTool.Core;

namespace DVRTool.Vendors.HikvisionSdk;

/// <summary>
/// Translates DVRTool's display channel number into the channel number HCNetSDK wants.
/// </summary>
/// <remarks>
/// <para>
/// They are not the same number, and the difference is silent: on an NVR the first IP camera
/// is display channel 1 but SDK channel <b>33</b>. Ask for SDK channel 1 on a
/// DS-7716NI and the device does not complain — there is simply no video, or on a hybrid
/// unit, video from a completely different camera. That is the same class of bug the serial
/// pin exists to stop, one layer down, so the mapping is read from the device rather than
/// assumed.
/// </para>
/// <para>
/// The four numbers come from <c>NET_DVR_DEVICEINFO_V30</c>, filled in by
/// <c>NET_DVR_Login_V30</c>. Analog inputs are numbered from <c>byStartChan</c> and IP ones
/// from <c>byStartDChan</c>; ISAPI — where DVRTool's channel list comes from — numbers them
/// in one run, analog first, which is what makes the two numberings diverge.
/// </para>
/// </remarks>
/// <param name="AnalogStart"><c>byStartChan</c>. Usually 1.</param>
/// <param name="AnalogCount"><c>byChanNum</c>. Zero on a pure NVR.</param>
/// <param name="DigitalStart"><c>byStartDChan</c>. Zero means the device reports none.</param>
/// <param name="DigitalCount"><c>byIPChanNum</c> plus <c>byHighDChanNum</c> &lt;&lt; 8.</param>
public sealed record SdkChannelMap(int AnalogStart, int AnalogCount, int DigitalStart, int DigitalCount)
{
    /// <summary><c>sizeof(NET_DVR_DEVICEINFO_V30)</c> is smaller, but 512 is what callers allocate.</summary>
    public const int DeviceInfoBufferSize = 512;

    /// <summary>Offset of <c>sSerialNumber</c>'s 48 bytes: the very start of the struct.</summary>
    private const int SerialLength = 48;

    // Byte offsets into NET_DVR_DEVICEINFO_V30. Verified against a live DS-7716NI-I4/16P(B)
    // on V4.61.030, which reports byChanNum 0 / byStartDChan 33 / byIPChanNum 16 — the
    // right answer for a 16-channel NVR, and wrong at every neighbouring offset.
    private const int OffsetChanNum = 52;
    private const int OffsetStartChan = 53;
    private const int OffsetIpChanNum = 55;
    private const int OffsetStartDChan = 66;
    private const int OffsetHighDChanNum = 68;

    /// <summary>The serial <c>NET_DVR_Login_V30</c> wrote into its device-info buffer.</summary>
    /// <remarks>
    /// The same serial ISAPI's <c>serialNumber</c> reports, which is what lets an SDK login
    /// be checked against the same identity pin an HTTP login made — but not always the same
    /// bytes: M-series firmware omits the hyphen ISAPI puts between the model prefix and the
    /// serial digits, which is why <c>DeviceFingerprint</c> strips hyphens before comparing.
    /// </remarks>
    public static string ReadSerial(ReadOnlySpan<byte> deviceInfo) =>
        System.Text.Encoding.ASCII
            .GetString(deviceInfo[..Math.Min(SerialLength, deviceInfo.Length)])
            .TrimEnd('\0')
            .Trim();

    public static SdkChannelMap From(ReadOnlySpan<byte> deviceInfo)
    {
        if (deviceInfo.Length <= OffsetHighDChanNum)
            throw new ArgumentException(
                $"device-info buffer is {deviceInfo.Length} bytes; needs at least " +
                $"{OffsetHighDChanNum + 1}", nameof(deviceInfo));

        return new SdkChannelMap(
            AnalogStart: deviceInfo[OffsetStartChan],
            AnalogCount: deviceInfo[OffsetChanNum],
            DigitalStart: deviceInfo[OffsetStartDChan],
            // Counts above 255 are split across two bytes; a 256-channel NVR is a real
            // product, so the high byte is not optional.
            DigitalCount: deviceInfo[OffsetIpChanNum] | (deviceInfo[OffsetHighDChanNum] << 8));
    }

    /// <summary>How many channels the device says it has, analog and IP together.</summary>
    public int TotalChannels => AnalogCount + DigitalCount;

    /// <summary>
    /// The SDK channel number for a 1-based display channel — the numbering
    /// <see cref="Channel.Id"/> uses.
    /// </summary>
    /// <exception cref="NvrException">
    /// The device has no such channel. Thrown rather than passed through, because HCNetSDK
    /// answers an out-of-range channel with a stream that never delivers a frame, which
    /// reads as a network problem.
    /// </exception>
    public int ToSdkChannel(int displayChannel)
    {
        if (displayChannel < 1)
            throw new ArgumentOutOfRangeException(nameof(displayChannel),
                "channel numbers are 1-based");

        // Old firmware that fills none of these in: pass the number through rather than
        // refuse. Nothing is known to be wrong, so the device gets to answer for itself.
        if (AnalogCount == 0 && DigitalCount == 0)
            return displayChannel;

        if (displayChannel <= AnalogCount)
            return AnalogStart + displayChannel - 1;

        int digitalIndex = displayChannel - AnalogCount; // 1-based within the IP block
        if (digitalIndex <= DigitalCount && DigitalStart > 0)
            return DigitalStart + digitalIndex - 1;

        throw new NvrException(
            $"channel {displayChannel} does not exist on this device: it reports " +
            $"{Describe()}. Run `dvrtool channels` to see the channels it does have.");
    }

    /// <summary>Operator-facing summary of the device's channel layout.</summary>
    public string Describe() => (AnalogCount, DigitalCount) switch
    {
        (0, 0) => "no channel counts at all",
        (0, _) => $"{DigitalCount} IP channel(s) numbered from {DigitalStart}",
        (_, 0) => $"{AnalogCount} analog channel(s) numbered from {AnalogStart}",
        _ => $"{AnalogCount} analog channel(s) from {AnalogStart} and {DigitalCount} IP " +
             $"channel(s) from {DigitalStart}",
    };
}
