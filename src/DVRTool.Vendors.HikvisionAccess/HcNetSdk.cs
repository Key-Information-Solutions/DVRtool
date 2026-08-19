using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DVRTool.Core;

namespace DVRTool.Vendors.HikvisionAccess;

/// <summary>
/// Every P/Invoke into Hikvision's HCNetSDK, plus the process-wide SDK lifetime.
/// Nothing outside this file talks to the native library.
/// </summary>
/// <remarks>
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
internal static class HcNetSdk
{
    private const string Dll = "HCNetSDK.dll";

    // ---- access-control config commands (Device Network SDK, access-control volume) ----

    /// <summary>Enumerate/query card parameters. Long-connection command.</summary>
    internal const uint NET_DVR_GET_CARD_CFG_V50 = 2178;

    /// <summary>Write card parameters. Long-connection command.</summary>
    internal const uint NET_DVR_SET_CARD_CFG_V50 = 2179;

    /// <summary>
    /// Card → cardholder-name association. DS-K2604 V2.0 firmware answers this with
    /// error 23 (NOSUPPORT): those panels store no cardholder identity whatsoever.
    /// </summary>
    internal const uint NET_DVR_GET_CARD_USERINFO_CFG = 2163;

    /// <summary><c>ENUM_ACS_SEND_DATA</c> — the data type for access-host payloads.</summary>
    internal const uint ENUM_ACS_SEND_DATA = 0x3;

    // ---- remote-config callback contract ----

    internal const uint NET_SDK_CALLBACK_TYPE_STATUS = 0;
    internal const uint NET_SDK_CALLBACK_TYPE_PROGRESS = 1;
    internal const uint NET_SDK_CALLBACK_TYPE_DATA = 2;

    internal const int NET_SDK_CALLBACK_STATUS_SUCCESS = 1000;
    internal const int NET_SDK_CALLBACK_STATUS_PROCESSING = 1001;
    internal const int NET_SDK_CALLBACK_STATUS_FAILED = 1002;
    internal const int NET_SDK_CALLBACK_STATUS_EXCEPTION = 1003;

    // ---- error codes actually observed on these panels ----

    internal const uint NET_DVR_PASSWORD_ERROR = 1;
    internal const uint NET_DVR_NOENOUGHPRI = 2;
    internal const uint NET_DVR_NETWORK_FAIL_CONNECT = 7;
    internal const uint NET_DVR_PARAMETER_ERROR = 17;
    internal const uint NET_DVR_NOSUPPORT = 23;
    internal const uint NET_DVR_USER_LOCKED = 96;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetDllDirectory(string? lpPathName);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport(Dll)]
    internal static extern bool NET_DVR_Init();

    [DllImport(Dll)]
    internal static extern bool NET_DVR_Cleanup();

    [DllImport(Dll)]
    internal static extern uint NET_DVR_GetLastError();

    [DllImport(Dll)]
    internal static extern bool NET_DVR_SetConnectTime(uint dwWaitTime, uint dwTryTimes);

    /// <summary>
    /// Pass <paramref name="dwEnableRecon"/> = 0. Background reconnect threads keep the
    /// SDK alive after logout and re-dial panels nobody asked about.
    /// </summary>
    [DllImport(Dll)]
    internal static extern bool NET_DVR_SetReconnect(uint dwInterval, int dwEnableRecon);

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    internal static extern int NET_DVR_Login_V30(string sDVRIP, ushort wDVRPort,
        string sUserName, string sPassword, IntPtr lpDeviceInfo);

    [DllImport(Dll)]
    internal static extern bool NET_DVR_Logout(int lUserID);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void RemoteConfigCallback(uint dwType, IntPtr lpBuffer, uint dwBufLen,
        IntPtr pUserData);

    [DllImport(Dll)]
    internal static extern int NET_DVR_StartRemoteConfig(int lUserID, uint dwCommand,
        IntPtr lpInBuffer, uint dwInBufferLen, RemoteConfigCallback cbStateCallback,
        IntPtr pUserData);

    [DllImport(Dll)]
    internal static extern bool NET_DVR_SendRemoteConfig(int lHandle, uint dwDataType,
        IntPtr pSendBuf, uint dwBufSize);

    [DllImport(Dll)]
    internal static extern bool NET_DVR_StopRemoteConfig(int lHandle);

    [DllImport(Dll)]
    internal static extern bool NET_DVR_GetDeviceConfig(int lUserID, uint dwCommand,
        uint dwCount, IntPtr lpInBuffer, uint dwInBufferSize, IntPtr lpStatusList,
        IntPtr lpOutBuffer, uint dwOutBufferSize);

    /// <summary>Human-readable text for the SDK error codes this driver can actually hit.</summary>
    /// <remarks>
    /// <c>NET_DVR_GetErrorMsg</c> is deliberately not used: the shipped builds have no
    /// string for code 23 and cheerfully report it as "No error", which sends an operator
    /// hunting for the wrong problem.
    /// </remarks>
    internal static string DescribeError(uint code) => code switch
    {
        0 => "no error",
        NET_DVR_PASSWORD_ERROR => "wrong user name or password (1)",
        NET_DVR_NOENOUGHPRI => "insufficient privilege for this account (2)",
        4 => "illegal channel (4)",
        NET_DVR_NETWORK_FAIL_CONNECT =>
            "cannot connect — device offline, wrong IP, or port 8000 blocked (7)",
        8 => "the device refused the connection (8)",
        10 => "receive timeout (10)",
        12 => "the SDK rejected the call order (12)",
        NET_DVR_PARAMETER_ERROR =>
            "parameter error — the command exists but the struct or its size is wrong (17)",
        NET_DVR_NOSUPPORT => "this device firmware does not implement that command (23)",
        41 => "SDK not initialized (41)",
        47 => "the user is not logged in (47)",
        NET_DVR_USER_LOCKED =>
            "account locked out after repeated failed logins (96) — wait for the lockout to expire",
        _ => $"SDK error {code}",
    };

    internal static NvrException Fail(string what)
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
internal sealed class SdkRuntime : IDisposable
{
    private static readonly object Gate = new();
    private static int _refCount;
    private static string? _loadedFrom;

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
            ?? ProbePaths.FirstOrDefault(p => File.Exists(Path.Combine(p, "HCNetSDK.dll")));

        if (dir is null)
            throw new NvrException(
                "Could not find HCNetSDK.dll. Install the Hikvision SDK (or iVMS-4200 / " +
                "HikCentral Lite, which bundle it) and point --sdk-dir or OCB_SDK_DIR at the " +
                "folder holding HCNetSDK.dll. Looked in:\n  " + string.Join("\n  ", ProbePaths));

        if (!File.Exists(Path.Combine(dir, "HCNetSDK.dll")))
            throw new NvrException($"no HCNetSDK.dll in '{dir}'.");

        return Path.GetFullPath(dir);
    }

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

        // Only the modules the access-control config commands actually need.
        // Loading the whole HCNetSDKCom folder also drags in the audio/preview plugins,
        // whose initializers print "Load OpenAL32.dll success!" and friends straight to
        // stdout — which would corrupt this tool's own machine-readable output.
        foreach (string relative in new[]
                 {
                     "HCCore.dll",
                     Path.Combine("HCNetSDKCom", "HCCoreDevCfg.dll"),
                     Path.Combine("HCNetSDKCom", "HCGeneralCfgMgr.dll"),
                 })
        {
            string path = Path.Combine(dir, relative);
            // Best effort: a genuinely missing plugin surfaces later as a clear
            // per-command SDK error rather than a confusing failure here.
            if (File.Exists(path))
                HcNetSdk.LoadLibrary(path);
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
        }
    }
}
