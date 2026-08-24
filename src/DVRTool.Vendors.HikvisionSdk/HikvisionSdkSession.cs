using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DVRTool.Core;

namespace DVRTool.Vendors.HikvisionSdk;

/// <summary>
/// A logged-in HCNetSDK session against a recorder, and the live streams opened on it.
/// </summary>
/// <remarks>
/// <para>
/// This is the transport that reaches sites RTSP cannot. Hikvision's private protocol
/// carries login, configuration <em>and</em> media over the single SDK port, so a live view
/// opened this way needs nothing forwarded but 8000 — which across our installed base is
/// open far more often than 554 is. It is what iVMS-4200 does; see
/// <c>docs/hikvision-sdk-live.md</c>.
/// </para>
/// <para>
/// The session is deliberately not an <see cref="INvrClient"/>. It answers one question —
/// give me this channel's video — and shares no plumbing with the ISAPI driver, exactly as
/// <c>HikvisionAccessClient</c> shares none.
/// </para>
/// <para>
/// Only Hikvision and its OEM rebrands. Dahua's equivalent is <c>CLIENT_RealPlayEx</c> in
/// <c>dhnetsdk.dll</c> on port 37777, which DVRTool does not link at all.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HikvisionSdkSession : IDisposable
{
    private readonly SdkRuntime _runtime;
    private readonly int _userId;
    private readonly List<HikvisionLiveStream> _streams = [];
    private bool _disposed;

    private HikvisionSdkSession(SdkRuntime runtime, int userId, NvrConnection connection,
        DeviceInfo deviceInfo, SdkChannelMap channels)
    {
        _runtime = runtime;
        _userId = userId;
        Connection = connection;
        DeviceInfo = deviceInfo;
        Channels = channels;
    }

    public NvrConnection Connection { get; }

    /// <summary>What the login itself reported. No extra round trip was needed for it.</summary>
    public DeviceInfo DeviceInfo { get; }

    /// <summary>The device's own channel numbering, read from the login response.</summary>
    public SdkChannelMap Channels { get; }

    /// <summary>The address this session's identity is pinned under.</summary>
    /// <remarks>
    /// The HTTP port, not the SDK one — <see cref="DeviceIdentityGuard.AddressOf(NvrConnection)"/>
    /// keys every device by the one address, so an SDK port that has been re-forwarded to a
    /// different recorder shows up as a serial mismatch instead of as a second, separately
    /// trusted identity.
    /// </remarks>
    public string Address => DeviceIdentityGuard.AddressOf(Connection);

    /// <summary>
    /// Logs in over the SDK port and confirms the recorder is the one meant, in that order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verification is inside the factory rather than left to the caller so that no code path
    /// can stream first and check afterwards. A login costs the device a user session and
    /// reads nothing, so doing it before the check is harmless; opening a video stream is
    /// not — an operator watching the wrong site's cameras under this site's name is the
    /// failure this whole mechanism exists to prevent, and on a fleet with one shared account
    /// the login succeeds either way.
    /// </para>
    /// <para>
    /// A mismatch throws <see cref="DeviceIdentityException"/> and the session is closed
    /// before the throw. Only one login attempt is ever made: repeated failures lock the
    /// account out on the device, and the tech's own iVMS-4200 usually shares the source IP.
    /// </para>
    /// </remarks>
    /// <param name="expectedSerial">The serial this record is bound to, when it has one.</param>
    /// <param name="expectedBy">Who holds that expectation, for the mismatch message.</param>
    /// <param name="sdkDirectory">
    /// Folder holding <c>HCNetSDK.dll</c>. Null falls back to <c>OCB_SDK_DIR</c> and then to
    /// the usual iVMS-4200 / HikCentral install locations.
    /// </param>
    /// <param name="onIdentityChecked">
    /// Receives the verdict for a first contact or an unpinnable device, so a front end can
    /// say so. Not called for the unremarkable match.
    /// </param>
    public static HikvisionSdkSession Open(NvrConnection connection,
        string? expectedSerial = null, string? expectedBy = null, string? sdkDirectory = null,
        DeviceIdentityStore? store = null, Action<IdentityCheck>? onIdentityChecked = null)
    {
        var runtime = SdkRuntime.Acquire(sdkDirectory);
        var session = LogIn(runtime, connection);
        try
        {
            var check = DeviceIdentityGuard.Check(session.Address, session.DeviceInfo,
                expectedSerial, expectedBy, store);
            DeviceIdentityGuard.Ensure(check);
            if (check.Message.Length > 0)
                onIdentityChecked?.Invoke(check);
        }
        catch
        {
            session.Dispose();
            throw;
        }
        return session;
    }

    private static HikvisionSdkSession LogIn(SdkRuntime runtime, NvrConnection connection)
    {
        int userId = -1;
        IntPtr deviceInfo = Marshal.AllocHGlobal(SdkChannelMap.DeviceInfoBufferSize);
        try
        {
            // Zeroed first: NET_DVR_Login_V30 does not clear the fields it has no answer for,
            // and reading uninitialised heap as a channel count is how a mapping silently
            // points at the wrong camera.
            for (int i = 0; i < SdkChannelMap.DeviceInfoBufferSize; i++)
                Marshal.WriteByte(deviceInfo, i, 0);

            userId = HcNetSdk.NET_DVR_Login_V30(
                connection.Host, (ushort)connection.SdkPort,
                connection.Username, connection.Password, deviceInfo);

            if (userId < 0)
                throw HcNetSdk.Fail(
                    $"SDK login to {connection.Host}:{connection.SdkPort} failed");

            var raw = new byte[SdkChannelMap.DeviceInfoBufferSize];
            Marshal.Copy(deviceInfo, raw, 0, raw.Length);
            string serial = SdkChannelMap.ReadSerial(raw);

            // The SDK reports no model or firmware string, only the serial — which on live
            // hardware begins with the model. Leaving Model empty rather than guessing keeps
            // the fingerprint comparable with the ISAPI one, which compares serials only.
            var info = new DeviceInfo(
                Name: $"{connection.Host}:{connection.SdkPort}",
                Model: "",
                SerialNumber: serial,
                FirmwareVersion: "");

            return new HikvisionSdkSession(runtime, userId, connection, info,
                SdkChannelMap.From(raw));
        }
        catch
        {
            // Anything thrown before the session exists owns nothing that would clean itself
            // up: a successful login leaves a user session on the device, and the SDK runtime
            // is reference-counted, so both have to be released here.
            if (userId >= 0)
                HcNetSdk.NET_DVR_Logout(userId);
            runtime.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(deviceInfo);
        }
    }

    /// <summary>
    /// Starts live video for a 1-based display channel and returns the byte stream it feeds.
    /// </summary>
    /// <param name="displayChannel">
    /// As <see cref="Channel.Id"/> numbers them. Converted to the device's own numbering by
    /// <see cref="Channels"/> — they differ, and not by a constant.
    /// </param>
    /// <param name="stream">Main or sub. Sub is the one to reach for over a thin uplink.</param>
    /// <param name="capacityBytes">Buffer size; see <see cref="SdkMediaStream"/>.</param>
    /// <param name="stallTimeout">How long a silent stream is waited on before it ends.</param>
    public HikvisionLiveStream StartLive(int displayChannel,
        StreamType stream = StreamType.Main, int capacityBytes = 8 * 1024 * 1024,
        TimeSpan? stallTimeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int sdkChannel = Channels.ToSdkChannel(displayChannel);

        // The plugins that implement preview are pulled in only now: they announce
        // themselves on stdout, and a command that never streams must not carry that noise.
        _runtime.EnsurePreviewPlugins();

        var live = new HikvisionLiveStream(displayChannel, sdkChannel, stream,
            capacityBytes, stallTimeout);
        try
        {
            live.Start(_userId);
        }
        catch
        {
            live.Dispose();
            throw;
        }

        lock (_streams)
            _streams.Add(live);
        return live;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Streams first: NET_DVR_Logout while a preview is running leaves the SDK's receive
        // threads pointed at a dead session.
        HikvisionLiveStream[] streams;
        lock (_streams)
        {
            streams = [.. _streams];
            _streams.Clear();
        }
        foreach (var stream in streams)
            stream.Dispose();

        HcNetSdk.NET_DVR_Logout(_userId);
        _runtime.Dispose();
    }
}

/// <summary>
/// One running <c>NET_DVR_RealPlay_V40</c> preview, and the media it produces.
/// </summary>
/// <remarks>
/// <para>
/// The bytes on <see cref="Media"/> are what the SDK's data callback delivers: one
/// <c>NET_DVR_SYSHEAD</c> header followed by <c>NET_DVR_STREAMDATA</c> payload, written in
/// arrival order. That is an MPEG program stream — the same container Hikvision's HTTP
/// exports turn out to be whatever they are named — so ffmpeg and VLC demux it directly, and
/// the repo's existing remux recipe applies unchanged.
/// </para>
/// <para>
/// <c>NET_DVR_PRIVATE_DATA</c> is dropped rather than written. It is Hikvision's out-of-band
/// channel for smart-event overlays, not media, and splicing it into the mux corrupts the
/// stream in a way that looks like a network fault.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HikvisionLiveStream : IDisposable
{
    /// <summary>
    /// Rooted for the whole life of the stream. The SDK holds a raw function pointer; a
    /// delegate collected while the callback is registered is a hard crash, not an error.
    /// </summary>
    private readonly HcNetSdk.RealDataCallback _callback;

    private readonly SdkMediaStream _media;
    private int _handle = -1;
    private bool _disposed;

    internal HikvisionLiveStream(int displayChannel, int sdkChannel, StreamType stream,
        int capacityBytes, TimeSpan? stallTimeout)
    {
        DisplayChannel = displayChannel;
        SdkChannel = sdkChannel;
        StreamType = stream;
        _media = new SdkMediaStream(capacityBytes, stallTimeout);
        _callback = OnData;
    }

    /// <summary>The channel as DVRTool numbers it.</summary>
    public int DisplayChannel { get; }

    /// <summary>The channel as the device numbers it — 33 for an NVR's first camera.</summary>
    public int SdkChannel { get; }

    public StreamType StreamType { get; }

    /// <summary>
    /// The live media, as a non-seekable stream. Hand it to LibVLC's
    /// <c>StreamMediaInput</c>, to ffmpeg, or copy it to a file.
    /// </summary>
    public SdkMediaStream Media => _media;

    internal void Start(int userId)
    {
        var preview = HcNetSdk.NET_DVR_PREVIEW_INFO.ForCallback(SdkChannel, (uint)StreamType);
        _handle = HcNetSdk.NET_DVR_RealPlay_V40(userId, ref preview, _callback, IntPtr.Zero);
        if (_handle < 0)
            throw HcNetSdk.Fail(
                $"starting live view on channel {DisplayChannel} (device channel " +
                $"{SdkChannel}, {StreamType.ToString().ToLowerInvariant()} stream) failed");
    }

    private void OnData(int handle, uint dataType, IntPtr buffer, uint length, IntPtr user)
    {
        // Native callers: an exception escaping here terminates the process.
        try
        {
            if (dataType is HcNetSdk.NET_DVR_SYSHEAD or HcNetSdk.NET_DVR_STREAMDATA)
                _media.Append(buffer, (int)length);
        }
        catch
        {
            // Nothing safe to do with it here. A stream that stops delivering is reported
            // by SdkMediaStream's stall timeout.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Stop before Complete, and in that order only: StopRealPlay blocks until the SDK's
        // receive thread has joined, which is what guarantees no callback is still running
        // when the delegate goes out of scope.
        if (_handle >= 0)
        {
            HcNetSdk.NET_DVR_StopRealPlay(_handle);
            _handle = -1;
        }
        _media.Complete();
        _media.Dispose();
    }
}
