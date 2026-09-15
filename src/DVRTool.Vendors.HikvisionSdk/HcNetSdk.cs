using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DVRTool.Core;

namespace DVRTool.Vendors.HikvisionSdk;

/// <summary>
/// Every P/Invoke into Hikvision's HCNetSDK, plus the process-wide SDK lifetime.
/// Nothing outside this file talks to the native library.
/// </summary>
/// <remarks>
/// <para>
/// Two unrelated features ride this one library — door panels
/// (<c>DVRTool.Vendors.HikvisionAccess</c>) and SDK live video
/// (<see cref="HikvisionSdkSession"/>) — which is why the P/Invoke surface and the
/// reference-counted <see cref="SdkRuntime"/> live in an assembly of their own rather than
/// inside either consumer.
/// </para>
/// Marshalling rules learned the hard way against live DS-K2604 panels:
/// <list type="bullet">
///   <item>64-bit process only — the SDK ships x64 and x86 builds that cannot mix.</item>
///   <item>Strings are ANSI, not Unicode.</item>
///   <item>The remote-config callback fires on an SDK-owned native thread, so the
///     delegate must be rooted for the whole call (a collected delegate = hard crash)
///     and must never let an exception escape into native code.</item>
///   <item><c>HCNetSDK.dll</c> pulls in <c>HCCore.dll</c> and the <c>HCNetSDKCom\</c>
///     plugins (notably <c>HCCoreDevCfg.dll</c>, which is what actually implements the
///     access-control config commands). They resolve only if the SDK directory is on the
///     DLL search path, so <see cref="SdkRuntime"/> pre-loads them by full path.</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class HcNetSdk
{
    private const string Dll = "HCNetSDK.dll";

    // ---- access-control config commands (Device Network SDK, access-control volume) ----

    /// <summary>Enumerate/query card parameters. Long-connection command.</summary>
    public const uint NET_DVR_GET_CARD_CFG_V50 = 2178;

    /// <summary>Write card parameters. Long-connection command.</summary>
    public const uint NET_DVR_SET_CARD_CFG_V50 = 2179;

    /// <summary>
    /// Card → cardholder-name association. DS-K2604 V2.0 firmware answers this with
    /// error 23 (NOSUPPORT): those panels store no cardholder identity whatsoever.
    /// </summary>
    public const uint NET_DVR_GET_CARD_USERINFO_CFG = 2163;

    /// <summary><c>ENUM_ACS_SEND_DATA</c> — the data type for access-host payloads.</summary>
    public const uint ENUM_ACS_SEND_DATA = 0x3;

    // ---- remote-config callback contract ----

    public const uint NET_SDK_CALLBACK_TYPE_STATUS = 0;
    public const uint NET_SDK_CALLBACK_TYPE_PROGRESS = 1;
    public const uint NET_SDK_CALLBACK_TYPE_DATA = 2;

    public const int NET_SDK_CALLBACK_STATUS_SUCCESS = 1000;
    public const int NET_SDK_CALLBACK_STATUS_PROCESSING = 1001;
    public const int NET_SDK_CALLBACK_STATUS_FAILED = 1002;
    public const int NET_SDK_CALLBACK_STATUS_EXCEPTION = 1003;

    // ---- live preview (Device Network SDK, preview volume) ----

    /// <summary>
    /// <c>dwLinkMode</c> 0: the media comes back multiplexed over the very TCP session the
    /// login already authenticated on.
    /// </summary>
    /// <remarks>
    /// This is the whole reason SDK live view reaches sites RTSP cannot. The other modes
    /// (1 UDP, 2 multicast, 3 RTP, 4 RTP/RTSP, 5 RTP/HTTP, 6 HRUDP) all want a second port
    /// open; mode 4 is the one that would dial 554 and hit the closed firewall. iVMS-4200
    /// uses 0, which is why it works on sites where only the SDK port is forwarded.
    /// </remarks>
    public const uint LinkModeTcp = 0;

    /// <summary>Stream header — arrives once, before any media, and must be written first.</summary>
    public const uint NET_DVR_SYSHEAD = 1;

    /// <summary>Muxed audio+video payload.</summary>
    public const uint NET_DVR_STREAMDATA = 2;

    /// <summary>
    /// Hikvision's private out-of-band channel (smart-event overlays and the like). Not
    /// media: writing it into the media stream corrupts the mux.
    /// </summary>
    public const uint NET_DVR_PRIVATE_DATA = 112;

    /// <summary><c>NET_DVR_PREVIEW_INFO</c>. 288 bytes on x64 — see the field comments.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_PREVIEW_INFO
    {
        /// <summary>
        /// The device's own channel number, which is not the display channel — an NVR's first
        /// IP camera is channel 33. <see cref="SdkChannelMap"/> does the conversion.
        /// </summary>
        public int lChannel;

        /// <summary>0 main, 1 sub, 2 third.</summary>
        public uint dwStreamType;

        /// <summary>See <see cref="LinkModeTcp"/>.</summary>
        public uint dwLinkMode;

        /// <summary>
        /// Zero means "do not decode or render — hand the bytes to the callback instead",
        /// which is the only mode used here: a non-zero HWND would put Hikvision's renderer
        /// on screen and take the stream away from LibVLC.
        /// </summary>
        /// <remarks>
        /// Naturally aligned to offset 16 on x64, so the four bytes after
        /// <c>dwLinkMode</c> are padding in both the C header and here.
        /// </remarks>
        public IntPtr hPlayWnd;

        /// <summary>Whether the SDK blocks until the stream is set up. 1 = report failure now.</summary>
        public uint bBlocked;

        public uint bPassbackRecord;
        public byte byPreviewMode;

        /// <summary><c>byStreamID[32]</c> — only meaningful for zero-channel encoding.</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] byStreamID;

        public byte byProtoType;
        public byte byRes1;
        public byte byVideoCodingType;
        public uint dwDisplayBufNum;
        public byte byNPQMode;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 215)]
        public byte[] byRes;

        /// <summary>A preview request for one channel over the private TCP transport.</summary>
        public static NET_DVR_PREVIEW_INFO ForCallback(int sdkChannel, uint streamType) => new()
        {
            lChannel = sdkChannel,
            dwStreamType = streamType,
            dwLinkMode = LinkModeTcp,
            hPlayWnd = IntPtr.Zero,
            // Blocking: a channel that cannot start says so from RealPlay's return value,
            // rather than handing back a live handle that never delivers a byte.
            bBlocked = 1,
            byStreamID = new byte[32],
            byRes = new byte[215],
        };
    }

    /// <summary>
    /// <c>REALDATACALLBACK</c>. Fires on an SDK-owned native thread, so the delegate must be
    /// rooted for the lifetime of the stream and must never let an exception escape.
    /// </summary>
    /// <remarks>
    /// It must also never block: the SDK feeds every stream on this thread, and a slow
    /// callback stalls the socket the whole session shares.
    /// </remarks>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void RealDataCallback(int lRealHandle, uint dwDataType, IntPtr pBuffer,
        uint dwBufSize, IntPtr pUser);

    [DllImport(Dll)]
    public static extern int NET_DVR_RealPlay_V40(int lUserID,
        ref NET_DVR_PREVIEW_INFO lpPreviewInfo, RealDataCallback? fRealDataCallBack_V30,
        IntPtr pUser);

    [DllImport(Dll)]
    public static extern bool NET_DVR_StopRealPlay(int lRealHandle);

    // ---- error codes actually observed on these panels ----

    public const uint NET_DVR_PASSWORD_ERROR = 1;
    public const uint NET_DVR_NOENOUGHPRI = 2;
    public const uint NET_DVR_NETWORK_FAIL_CONNECT = 7;
    public const uint NET_DVR_PARAMETER_ERROR = 17;
    public const uint NET_DVR_NOSUPPORT = 23;
    public const uint NET_DVR_USER_LOCKED = 96;

    /// <summary>
    /// Every stream slot on the device is taken. Live view is the one feature that hits
    /// this: each viewer burns a session, and iVMS-4200 sitting on the same recorder has
    /// usually taken several already.
    /// </summary>
    public const uint NET_DVR_OVER_MAXLINK = 46;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string? lpPathName);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport(Dll)]
    public static extern bool NET_DVR_Init();

    [DllImport(Dll)]
    public static extern bool NET_DVR_Cleanup();

    [DllImport(Dll)]
    public static extern uint NET_DVR_GetLastError();

    [DllImport(Dll)]
    public static extern bool NET_DVR_SetConnectTime(uint dwWaitTime, uint dwTryTimes);

    /// <summary>
    /// Pass <paramref name="dwEnableRecon"/> = 0. Background reconnect threads keep the
    /// SDK alive after logout and re-dial panels nobody asked about.
    /// </summary>
    [DllImport(Dll)]
    public static extern bool NET_DVR_SetReconnect(uint dwInterval, int dwEnableRecon);

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern int NET_DVR_Login_V30(string sDVRIP, ushort wDVRPort,
        string sUserName, string sPassword, IntPtr lpDeviceInfo);

    [DllImport(Dll)]
    public static extern bool NET_DVR_Logout(int lUserID);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void RemoteConfigCallback(uint dwType, IntPtr lpBuffer, uint dwBufLen,
        IntPtr pUserData);

    [DllImport(Dll)]
    public static extern int NET_DVR_StartRemoteConfig(int lUserID, uint dwCommand,
        IntPtr lpInBuffer, uint dwInBufferLen, RemoteConfigCallback cbStateCallback,
        IntPtr pUserData);

    [DllImport(Dll)]
    public static extern bool NET_DVR_SendRemoteConfig(int lHandle, uint dwDataType,
        IntPtr pSendBuf, uint dwBufSize);

    [DllImport(Dll)]
    public static extern bool NET_DVR_StopRemoteConfig(int lHandle);

    [DllImport(Dll)]
    public static extern bool NET_DVR_GetDeviceConfig(int lUserID, uint dwCommand,
        uint dwCount, IntPtr lpInBuffer, uint dwInBufferSize, IntPtr lpStatusList,
        IntPtr lpOutBuffer, uint dwOutBufferSize);

    /// <summary>Human-readable text for the SDK error codes this driver can actually hit.</summary>
    /// <remarks>
    /// <c>NET_DVR_GetErrorMsg</c> is deliberately not used: the shipped builds have no
    /// string for code 23 and cheerfully report it as "No error", which sends an operator
    /// hunting for the wrong problem.
    /// </remarks>
    public static string DescribeError(uint code) => code switch
    {
        0 => "no error",
        NET_DVR_PASSWORD_ERROR => "wrong user name or password (1)",
        NET_DVR_NOENOUGHPRI => "insufficient privilege for this account (2)",
        4 => "illegal channel (4)",
        NET_DVR_NETWORK_FAIL_CONNECT =>
            "cannot connect — device offline, wrong IP, or port 8000 blocked (7)",
        8 => "the device refused the connection (8)",
        10 => "receive timeout (10)",
        // Documented as "wrong data sent to, or returned by, the device". What it means in
        // practice on a preview start is a channel with nothing behind it: a DS-7716NI
        // answers exactly this for an IP channel whose camera is offline.
        11 =>
            "the device rejected the request (11) — on a live view this is what a channel " +
            "with no camera on it answers",
        12 => "the SDK rejected the call order (12)",
        NET_DVR_PARAMETER_ERROR =>
            "parameter error — the command exists but the struct or its size is wrong (17)",
        NET_DVR_NOSUPPORT => "this device firmware does not implement that command (23)",
        41 => "SDK not initialized (41)",
        NET_DVR_OVER_MAXLINK =>
            "the device is out of stream slots (46) — close a live view in iVMS-4200 or " +
            "another client, or use the sub-stream",
        47 => "the user is not logged in (47)",
        NET_DVR_USER_LOCKED =>
            "account locked out after repeated failed logins (96) — wait for the lockout to expire",
        _ => $"SDK error {code}",
    };

    public static NvrException Fail(string what)
    {
        uint code = NET_DVR_GetLastError();
        return new NvrException($"{what}: {DescribeError(code)}", statusCode: (int)code);
    }
}

/// <summary>
/// Owns the one-per-process <c>NET_DVR_Init</c>/<c>NET_DVR_Cleanup</c> pair.
/// </summary>
/// <remarks>
/// The SDK is global state, not per-connection: initializing twice or cleaning up while
/// another client is still logged in tears down that client's session. Access is therefore
/// reference-counted, so talking to three panels at once works and the last client out
/// turns the lights off.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SdkRuntime : IDisposable
{
    private static readonly object Gate = new();
    private static int _refCount;
    private static string? _loadedFrom;

    /// <summary>
    /// Whether the preview plugin set has been pulled in. Process-wide, like the modules it
    /// tracks: <c>LoadLibrary</c> is not undone by a logout.
    /// </summary>
    private static bool _previewLoaded;

    private bool _disposed;

    /// <summary>Directories the SDK is normally installed into on our techs' machines.</summary>
    private static readonly string[] ProbePaths =
    [
        @"C:\Program Files (x86)\HikCentral Lite\Client",
        @"C:\Program Files\HikCentral Lite\Client",
        @"C:\Program Files (x86)\iVMS-4200 Site\iVMS-4200 Client\Client",
        @"C:\Program Files (x86)\iVMS-4200\iVMS-4200 Client\Client",
    ];

    public string SdkDirectory { get; }

    private SdkRuntime(string sdkDirectory) => SdkDirectory = sdkDirectory;

    private static bool? _installed;

    /// <summary>
    /// Whether HCNetSDK can be found on this machine — asked without loading anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SDK ships with iVMS-4200 / HikCentral rather than with DVRTool, so "is it here at
    /// all" is a real question on a fresh workstation, and one the GUI has to answer before it
    /// can offer the SDK transport as a camera's default route rather than as a click.
    /// </para>
    /// <para>
    /// The answer is cached for the process: an install that appears mid-session is not worth
    /// probing the file system for on every device click, and it is picked up on the next run.
    /// A true here is "the DLL is where <see cref="Acquire"/> will look", not "the SDK
    /// initialized" — that can still fail, and says so when it does.
    /// </para>
    /// </remarks>
    public static bool IsInstalled => _installed ??= ProbeInstalled();

    private static bool ProbeInstalled()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return false;
        if (_loadedFrom is not null)
            return true;
        string? dir = Environment.GetEnvironmentVariable("OCB_SDK_DIR") ?? ProbeForDirectory();
        return dir is not null && File.Exists(Path.Combine(dir, "HCNetSDK.dll"));
    }

    /// <summary>
    /// Initializes (or joins) the SDK. <paramref name="sdkDirectory"/> null means: use
    /// <c>OCB_SDK_DIR</c>, else probe the usual install locations.
    /// </summary>
    public static SdkRuntime Acquire(string? sdkDirectory)
    {
        if (!OperatingSystem.IsWindows())
            throw new NvrException(
                "Access-control panels are reached through Hikvision's HCNetSDK, which is " +
                "Windows-only. Run this on the Windows host that has the SDK installed.");

        if (!Environment.Is64BitProcess)
            throw new NvrException(
                "HCNetSDK.dll is 64-bit; this process is 32-bit and cannot load it.");

        string dir = ResolveDirectory(sdkDirectory);

        lock (Gate)
        {
            if (_refCount > 0)
            {
                // Two different SDK copies in one process would load two HCNetSDK images
                // and the second Init would fight the first.
                if (!string.Equals(_loadedFrom, dir, StringComparison.OrdinalIgnoreCase))
                    throw new NvrException(
                        $"the SDK is already loaded from '{_loadedFrom}'; one process cannot " +
                        $"also load it from '{dir}'.");
                _refCount++;
                return new SdkRuntime(dir);
            }

            Preload(dir);

            if (!HcNetSdk.NET_DVR_Init())
                throw HcNetSdk.Fail($"NET_DVR_Init failed (SDK directory '{dir}')");

            // 3s / one attempt: a panel that is not answering should fail fast, and a retry
            // loop against a wrong password is what triggers the device's IP lockout.
            HcNetSdk.NET_DVR_SetConnectTime(3000, 1);
            HcNetSdk.NET_DVR_SetReconnect(5000, 0);

            _loadedFrom = dir;
            _refCount = 1;
            return new SdkRuntime(dir);
        }
    }

    private static string ResolveDirectory(string? requested)
    {
        string? dir = requested
            ?? Environment.GetEnvironmentVariable("OCB_SDK_DIR")
            ?? ProbeForDirectory();

        if (dir is null)
            throw new NvrException(
                "Could not find HCNetSDK.dll. Install the Hikvision SDK (or iVMS-4200 / " +
                "HikCentral Lite, which bundle it) and point --sdk-dir or OCB_SDK_DIR at the " +
                "folder holding HCNetSDK.dll. Looked in:\n  " + string.Join("\n  ", ProbePaths));

        if (!File.Exists(Path.Combine(dir, "HCNetSDK.dll")))
            throw new NvrException($"no HCNetSDK.dll in '{dir}'.");

        return Path.GetFullPath(dir);
    }

    private static string? ProbeForDirectory() =>
        ProbePaths.FirstOrDefault(p => File.Exists(Path.Combine(p, "HCNetSDK.dll")));

    /// <summary>
    /// Loads HCNetSDK and its plugin set by absolute path.
    /// </summary>
    /// <remarks>
    /// The plugins are pre-loaded deliberately. The SDK asks for them later by bare file
    /// name (<c>LoadLibrary("HCCoreDevCfg.dll")</c>); once a module of that name is already
    /// in the process it is reused, which spares us mutating the process-wide current
    /// directory the way Hikvision's own samples do.
    /// </remarks>
    private static void Preload(string dir)
    {
        HcNetSdk.SetDllDirectory(dir);

        if (HcNetSdk.LoadLibrary(Path.Combine(dir, "HCNetSDK.dll")) == IntPtr.Zero)
            throw new NvrException(
                $"LoadLibrary failed for '{Path.Combine(dir, "HCNetSDK.dll")}' " +
                $"(Win32 error {Marshal.GetLastWin32Error()}). A 32-bit SDK in a 64-bit " +
                "process is the usual cause.");

        // Only the modules every session needs. Loading the whole HCNetSDKCom folder also
        // drags in the audio and preview plugins, whose initializers print "Load
        // OpenAL32.dll success!" and friends straight to stdout — which would corrupt this
        // tool's own machine-readable output. The preview plugin is therefore loaded on
        // demand instead, by EnsurePreviewPlugins.
        LoadPlugins(dir,
            "HCCore.dll",
            Path.Combine("HCNetSDKCom", "HCCoreDevCfg.dll"),
            Path.Combine("HCNetSDKCom", "HCGeneralCfgMgr.dll"));
    }

    private static void LoadPlugins(string dir, params string[] relativePaths)
    {
        foreach (string relative in relativePaths)
        {
            string path = Path.Combine(dir, relative);
            // Best effort: a genuinely missing plugin surfaces later as a clear
            // per-command SDK error rather than a confusing failure here.
            if (File.Exists(path))
                HcNetSdk.LoadLibrary(path);
        }
    }

    /// <summary>
    /// Loads the plugins that implement live preview. Idempotent, and only ever called by a
    /// caller that is about to stream.
    /// </summary>
    /// <remarks>
    /// Deliberately not part of <see cref="Preload"/>. <c>HCPreview.dll</c> and its
    /// neighbours announce themselves on stdout as they initialize, and `dvrtool access
    /// find` piping a roster into a script must not have SDK banner text spliced into it.
    /// A command that asks for video has already accepted that noise; one that asks for a
    /// card list has not.
    /// </remarks>
    public void EnsurePreviewPlugins()
    {
        lock (Gate)
        {
            if (_previewLoaded)
                return;
            LoadPlugins(SdkDirectory,
                Path.Combine("HCNetSDKCom", "HCPreview.dll"),
                Path.Combine("HCNetSDKCom", "StreamTransClient.dll"),
                Path.Combine("HCNetSDKCom", "SystemTransform.dll"));
            _previewLoaded = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (Gate)
        {
            if (--_refCount > 0)
                return;
            HcNetSdk.NET_DVR_Cleanup();
            _loadedFrom = null;
            // _previewLoaded is deliberately not reset: NET_DVR_Cleanup unwinds the SDK's
            // own state, not the process's loaded modules, so a re-Acquire in the same
            // process must not print the plugin banners a second time.
        }
    }
}
