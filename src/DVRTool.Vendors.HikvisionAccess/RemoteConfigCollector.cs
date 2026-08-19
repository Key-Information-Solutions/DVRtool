using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DVRTool.Core;

namespace DVRTool.Vendors.HikvisionAccess;

/// <summary>
/// Bridges one HCNetSDK "remote config" long connection back into managed code: it holds the
/// callback the SDK invokes, accumulates the records it delivers, and blocks until the
/// exchange reaches a terminal status.
/// </summary>
/// <remarks>
/// <para>
/// The callback runs on a native thread the SDK owns. Two consequences drive this design:
/// the delegate must stay reachable for as long as the SDK might call it (a collected
/// delegate is an immediate process crash, not an exception), and no managed exception may
/// escape into native code — so the callback records failures instead of throwing.
/// </para>
/// <para>
/// <see cref="Release"/> must be called only after <c>NET_DVR_StopRemoteConfig</c>, which is
/// the point at which the SDK promises to stop invoking the callback.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class RemoteConfigCollector
{
    /// <summary>
    /// If the device stops delivering records without ever sending a terminal status, give
    /// up this long after the last one. Firmware in the field does send the terminal status,
    /// but hanging until the outer timeout would be a poor failure mode if one didn't.
    /// </summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly List<CardRecord> _records = [];
    private readonly CancellationToken _ct;
    private readonly ManualResetEventSlim _finished = new(false);

    private GCHandle _self;
    private int _terminalStatus;
    private uint? _failureCode;
    private string? _malformed;
    private long _lastRecordTicks;

    internal RemoteConfigCollector(CancellationToken ct)
    {
        _ct = ct;
        _lastRecordTicks = DateTime.UtcNow.Ticks;
        Callback = OnRemoteConfig;
        // Belt and braces: the field reference already roots the delegate, but the SDK keeps
        // the raw function pointer past the managed call, so pin it explicitly too.
        _self = GCHandle.Alloc(Callback);
    }

    /// <summary>The delegate to hand to <c>NET_DVR_StartRemoteConfig</c>.</summary>
    internal HcNetSdk.RemoteConfigCallback Callback { get; }

    internal List<CardRecord> Records
    {
        get { lock (_gate) return [.. _records]; }
    }

    /// <summary>Device-reported error code when the exchange ended in failure.</summary>
    internal uint? FailureCode
    {
        get { lock (_gate) return _failureCode; }
    }

    private void OnRemoteConfig(uint dwType, IntPtr lpBuffer, uint dwBufLen, IntPtr pUserData)
    {
        try
        {
            switch (dwType)
            {
                case HcNetSdk.NET_SDK_CALLBACK_TYPE_STATUS:
                    HandleStatus(lpBuffer, dwBufLen);
                    break;

                case HcNetSdk.NET_SDK_CALLBACK_TYPE_DATA:
                    HandleData(lpBuffer, dwBufLen);
                    break;

                // PROGRESS carries a percentage this command family never uses.
            }
        }
        catch (Exception ex)
        {
            // Never let this propagate: the caller is native code with no way to handle it.
            lock (_gate)
            {
                _malformed ??= ex.Message;
                _terminalStatus = HcNetSdk.NET_SDK_CALLBACK_STATUS_FAILED;
            }
            _finished.Set();
        }
    }

    private void HandleStatus(IntPtr buffer, uint length)
    {
        int status = length >= sizeof(int) ? Marshal.ReadInt32(buffer) : -1;
        switch (status)
        {
            case HcNetSdk.NET_SDK_CALLBACK_STATUS_SUCCESS:
                lock (_gate) _terminalStatus = status;
                _finished.Set();
                break;

            case HcNetSdk.NET_SDK_CALLBACK_STATUS_FAILED:
            case HcNetSdk.NET_SDK_CALLBACK_STATUS_EXCEPTION:
                lock (_gate)
                {
                    _terminalStatus = status;
                    // FAILED carries: 4-byte status, 4-byte error code, 32-byte card number.
                    if (length >= 2 * sizeof(int))
                        _failureCode = (uint)Marshal.ReadInt32(buffer, sizeof(int));
                }
                _finished.Set();
                break;

            case HcNetSdk.NET_SDK_CALLBACK_STATUS_PROCESSING:
                Interlocked.Exchange(ref _lastRecordTicks, DateTime.UtcNow.Ticks);
                break;
        }
    }

    private void HandleData(IntPtr buffer, uint length)
    {
        if (length == 0)
            return;

        var bytes = new byte[length];
        Marshal.Copy(buffer, bytes, 0, (int)length);
        var record = CardRecord.FromBytes(bytes);

        lock (_gate) _records.Add(record);
        Interlocked.Exchange(ref _lastRecordTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Blocks until the device reports a terminal status, the record stream goes quiet, the
    /// overall <paramref name="timeout"/> expires, or the caller cancels.
    /// </summary>
    internal void Wait(TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (_finished.Wait(TimeSpan.FromMilliseconds(200), _ct))
                break;

            _ct.ThrowIfCancellationRequested();

            var lastRecord = new DateTime(Interlocked.Read(ref _lastRecordTicks), DateTimeKind.Utc);
            bool gotAnything;
            lock (_gate) gotAnything = _records.Count > 0;

            if (gotAnything && DateTime.UtcNow - lastRecord > QuietPeriod)
                break;

            if (DateTime.UtcNow > deadline)
                throw new NvrException(
                    $"{what} timed out after {timeout.TotalSeconds:F0}s " +
                    $"({(gotAnything ? $"{Records.Count} record(s) received" : "no records received")}).");
        }

        string? malformed;
        lock (_gate) malformed = _malformed;
        if (malformed is not null)
            throw new NvrException($"{what} returned data this driver could not read: {malformed}");
    }

    /// <summary>
    /// Releases the pinned delegate. Call only after <c>NET_DVR_StopRemoteConfig</c>.
    /// </summary>
    internal void Release()
    {
        if (_self.IsAllocated)
            _self.Free();
        _finished.Dispose();
    }
}
