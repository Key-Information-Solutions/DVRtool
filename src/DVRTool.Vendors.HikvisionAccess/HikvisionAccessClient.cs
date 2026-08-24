using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DVRTool.Core;
using DVRTool.Vendors.HikvisionSdk;

namespace DVRTool.Vendors.HikvisionAccess;

/// <summary>
/// Hikvision access-control panel driver (DS-K series and OEM rebrands such as "OCB"),
/// speaking HCNetSDK's structured access-control commands over port 8000.
/// </summary>
/// <remarks>
/// <para>
/// The transport is deliberately unlike <c>HikvisionClient</c>, which is ISAPI over HTTP.
/// These controllers run no web server at all — 80/443/8443 are closed — and
/// <c>NET_DVR_STDXMLConfig</c> answers every ISAPI URL with error 23 on V2.0 firmware.
/// The only way in is the private SDK protocol, so none of the HTTP plumbing is shared.
/// </para>
/// <para>
/// Card enumeration is a long-connection ("remote config") exchange:
/// <c>NET_DVR_StartRemoteConfig</c> opens it and every card arrives on a callback, with a
/// terminal status callback closing it. There is no <c>GetNextRemoteConfig</c> polling loop
/// for this command family.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HikvisionAccessClient : IAccessControlClient
{
    /// <summary><c>sizeof(NET_DVR_CARD_CFG_COND)</c>.</summary>
    private const int CardCondSize = 40;

    /// <summary><c>sizeof(NET_DVR_CARD_CFG_SEND_DATA)</c>.</summary>
    private const int SendDataSize = 52;

    /// <summary><c>sizeof(NET_DVR_CARD_USER_INFO_CFG)</c>.</summary>
    private const int UserInfoSize = 292;

    /// <summary><c>dwCardNum</c> sentinel meaning "every card".</summary>
    private const uint AllCards = 0xFFFFFFFF;

    /// <summary>Longest we wait for a whole enumeration. 149 cards took ~7s live.</summary>
    private static readonly TimeSpan EnumerationTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Longest we wait for a single write to be acknowledged.</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

    private readonly SdkRuntime _runtime;
    private readonly int _userId;
    private string? _serial;
    private bool _disposed;

    public Vendor Vendor => Vendor.Hikvision;
    public AccessPanelConnection Connection { get; }

    /// <param name="connection">Panel address and credentials.</param>
    /// <param name="sdkDirectory">
    /// Folder holding <c>HCNetSDK.dll</c>. Null falls back to <c>OCB_SDK_DIR</c> and then
    /// to the usual iVMS-4200 / HikCentral install locations.
    /// </param>
    public HikvisionAccessClient(AccessPanelConnection connection, string? sdkDirectory = null)
    {
        Connection = connection;
        _runtime = SdkRuntime.Acquire(sdkDirectory);

        IntPtr deviceInfo = Marshal.AllocHGlobal(512);
        try
        {
            // Exactly one login attempt, ever. These panels lock out a *source IP* after a
            // handful of failures, and iVMS-4200 usually shares that IP — a retry loop here
            // would lock the incumbent management software out of its own doors.
            _userId = HcNetSdk.NET_DVR_Login_V30(
                connection.Host, (ushort)connection.SdkPort,
                connection.Username, connection.Password, deviceInfo);

            if (_userId < 0)
            {
                var ex = HcNetSdk.Fail($"login to {connection.Label} failed");
                _runtime.Dispose();
                throw ex;
            }

            var serial = new byte[48];
            Marshal.Copy(deviceInfo, serial, 0, serial.Length);
            _serial = Encoding.ASCII.GetString(serial).TrimEnd('\0').Trim();
        }
        catch
        {
            Marshal.FreeHGlobal(deviceInfo);
            throw;
        }
        finally
        {
            if (_userId >= 0)
                Marshal.FreeHGlobal(deviceInfo);
        }
    }

    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        string serial = _serial ?? "";
        return Task.FromResult(new DeviceInfo(
            Connection.Label, PanelIdentity.ParseModel(serial), serial,
            PanelIdentity.ParseFirmware(serial)));
    }

    public async Task<AccessPanelCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        bool names = await Task.Run(() => ProbeCardUserInfo(), ct).ConfigureAwait(false);
        return new AccessPanelCapabilities(names, PanelIdentity.ParseDoorCount(_serial ?? ""));
    }

    public async Task<IReadOnlyList<AccessCard>> GetCardsAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var records = await Task.Run(() => Enumerate(cardNo: null, ct), ct).ConfigureAwait(false);
        // Stamped with the port-qualified label, not the bare host: two panels behind one
        // address would otherwise stamp their cards identically and merge in the roster.
        return records.Select(r => r.ToAccessCard(Connection.Label)).ToList();
    }

    public async Task<AccessCard?> GetCardAsync(string cardNo, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ValidateCardNo(cardNo);
        var records = await Task.Run(() => Enumerate(cardNo, ct), ct).ConfigureAwait(false);

        // A targeted query can still answer with the device's "no such card" placeholder
        // rather than nothing at all, so match on the number we asked for.
        var match = records.FirstOrDefault(r =>
            AccessRoster.NormalizeCardNo(r.CardNo) == AccessRoster.NormalizeCardNo(cardNo));
        return match?.ToAccessCard(Connection.Label);
    }

    public async Task UpsertCardAsync(AccessCard card, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ValidateCardNo(card.CardNo);

        // Read-modify-write: start from the record the device already holds so fields this
        // driver does not model (week plans, holiday groups, card password, lock/room codes)
        // are preserved rather than zeroed. Only a genuinely new card starts from blank.
        var existing = (await Task.Run(() => Enumerate(card.CardNo, ct), ct).ConfigureAwait(false))
            .FirstOrDefault(r =>
                AccessRoster.NormalizeCardNo(r.CardNo) == AccessRoster.NormalizeCardNo(card.CardNo));

        var record = existing ?? CardRecord.CreateEmpty();
        var modify = CardModifyParam.CardValid | CardModifyParam.CardType |
                     CardModifyParam.DoorRight | CardModifyParam.RightPlan |
                     CardModifyParam.LeaderCard | CardModifyParam.UserType;

        record.CardNo = card.CardNo;
        record.Valid = card.Valid;
        record.CardType = card.NativeCardType != 0
            ? card.NativeCardType
            : (byte)(card.Type == AccessCardType.Unknown ? AccessCardType.Normal : card.Type);
        record.IsLeaderCard = card.IsLeaderCard;
        record.IsAdmin = card.IsAdmin;
        record.SetDoors(card.Doors);

        if (card.ValidFrom is DateTime from && card.ValidUntil is DateTime until)
        {
            record.SetValidPeriod(from, until);
            modify |= CardModifyParam.ValidPeriod;
        }

        if (card.Name is not null)
        {
            record.Name = card.Name;
            modify |= CardModifyParam.Name;
        }

        if (card.EmployeeNo != 0)
        {
            record.EmployeeNo = card.EmployeeNo;
            modify |= CardModifyParam.EmployeeNo;
        }

        record.ModifyParams = modify;
        await Task.Run(() => Write(record, ct), ct).ConfigureAwait(false);
    }

    public async Task RevokeCardAsync(string cardNo, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ValidateCardNo(cardNo);

        // The device has no delete verb: writing byCardValid = 0 *is* the delete, and the
        // only field that has to change.
        var record = CardRecord.CreateEmpty();
        record.CardNo = cardNo;
        record.Valid = false;
        record.ModifyParams = CardModifyParam.CardValid;

        await Task.Run(() => Write(record, ct), ct).ConfigureAwait(false);
    }

    // ---------- SDK exchanges ----------

    /// <summary>
    /// Runs a <c>NET_DVR_GET_CARD_CFG_V50</c> long connection and collects the records.
    /// <paramref name="cardNo"/> null enumerates every card; otherwise one card is requested.
    /// </summary>
    private List<CardRecord> Enumerate(string? cardNo, CancellationToken ct)
    {
        var collector = new RemoteConfigCollector(ct);
        IntPtr cond = Marshal.AllocHGlobal(CardCondSize);
        IntPtr sendData = IntPtr.Zero;
        int handle = -1;
        try
        {
            WriteCardCondition(cond, cardNo is null ? AllCards : 1);

            handle = HcNetSdk.NET_DVR_StartRemoteConfig(
                _userId, HcNetSdk.NET_DVR_GET_CARD_CFG_V50, cond, CardCondSize,
                collector.Callback, IntPtr.Zero);
            if (handle < 0)
                throw HcNetSdk.Fail(
                    $"starting a card query on {Connection.Label} failed " +
                    "(NET_DVR_GET_CARD_CFG_V50)");

            if (cardNo is not null)
            {
                // Fetching *all* cards needs no send; a targeted lookup sends the condition.
                sendData = Marshal.AllocHGlobal(SendDataSize);
                WriteSendData(sendData, cardNo);
                if (!HcNetSdk.NET_DVR_SendRemoteConfig(
                        handle, HcNetSdk.ENUM_ACS_SEND_DATA, sendData, SendDataSize))
                    throw HcNetSdk.Fail($"querying card {cardNo} on {Connection.Label} failed");
            }

            collector.Wait(EnumerationTimeout, $"card query on {Connection.Label}");
            return collector.Records;
        }
        finally
        {
            if (handle >= 0)
                HcNetSdk.NET_DVR_StopRemoteConfig(handle);
            if (sendData != IntPtr.Zero)
                Marshal.FreeHGlobal(sendData);
            Marshal.FreeHGlobal(cond);
            // Only now is the SDK guaranteed to have stopped calling into the delegate.
            collector.Release();
        }
    }

    /// <summary>Runs a <c>NET_DVR_SET_CARD_CFG_V50</c> long connection for one record.</summary>
    private void Write(CardRecord record, CancellationToken ct)
    {
        var collector = new RemoteConfigCollector(ct);
        IntPtr cond = Marshal.AllocHGlobal(CardCondSize);
        IntPtr payload = Marshal.AllocHGlobal(CardRecord.Size);
        int handle = -1;
        try
        {
            WriteCardCondition(cond, cardNum: 1);
            Marshal.Copy(record.ToArray(), 0, payload, CardRecord.Size);

            handle = HcNetSdk.NET_DVR_StartRemoteConfig(
                _userId, HcNetSdk.NET_DVR_SET_CARD_CFG_V50, cond, CardCondSize,
                collector.Callback, IntPtr.Zero);
            if (handle < 0)
                throw HcNetSdk.Fail(
                    $"starting a card write on {Connection.Label} failed " +
                    "(NET_DVR_SET_CARD_CFG_V50)");

            SendWithBackoff(handle, payload, CardRecord.Size, record.CardNo, ct);
            collector.Wait(WriteTimeout, $"card write on {Connection.Label}");

            if (collector.FailureCode is uint code)
                throw new NvrException(
                    $"the panel rejected the write for card {record.CardNo}: " +
                    HcNetSdk.DescribeError(code), statusCode: (int)code);
        }
        finally
        {
            if (handle >= 0)
                HcNetSdk.NET_DVR_StopRemoteConfig(handle);
            Marshal.FreeHGlobal(payload);
            Marshal.FreeHGlobal(cond);
            collector.Release();
        }
    }

    /// <summary>
    /// Sends a record, honoring the SDK's "wait and resend" backpressure.
    /// </summary>
    /// <remarks>
    /// A false return is not necessarily a failure: the long connection reports
    /// <c>SEND_WAIT</c> when the device is still digesting the previous item, and the
    /// documented handling is to pause and send again.
    /// </remarks>
    private void SendWithBackoff(int handle, IntPtr payload, int size, string cardNo,
        CancellationToken ct)
    {
        const int attempts = 10;
        uint lastError = 0;
        for (int i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (HcNetSdk.NET_DVR_SendRemoteConfig(
                    handle, HcNetSdk.ENUM_ACS_SEND_DATA, payload, (uint)size))
                return;

            lastError = HcNetSdk.NET_DVR_GetLastError();
            Thread.Sleep(200 * (i + 1));
        }

        throw new NvrException(
            $"the panel would not accept the write for card {cardNo} after {attempts} " +
            $"attempts: {HcNetSdk.DescribeError(lastError)}", statusCode: (int)lastError);
    }

    /// <summary>Fills a <c>NET_DVR_CARD_CFG_COND</c>.</summary>
    private static void WriteCardCondition(IntPtr buffer, uint cardNum)
    {
        var cond = new byte[CardCondSize];
        BitConverter.TryWriteBytes(cond.AsSpan(0), CardCondSize);
        BitConverter.TryWriteBytes(cond.AsSpan(4), cardNum);
        // byCheckCardNo = 1: let the device reject a duplicate card number rather than
        // writing it blind. Slower, and correct.
        cond[8] = 1;
        Marshal.Copy(cond, 0, buffer, CardCondSize);
    }

    /// <summary>Fills a <c>NET_DVR_CARD_CFG_SEND_DATA</c> for a targeted lookup.</summary>
    private static void WriteSendData(IntPtr buffer, string cardNo)
    {
        var data = new byte[SendDataSize];
        BitConverter.TryWriteBytes(data.AsSpan(0), SendDataSize);
        Encoding.ASCII.GetBytes(cardNo).CopyTo(data, 4);
        Marshal.Copy(data, 0, buffer, SendDataSize);
    }

    /// <summary>
    /// Asks the panel for a card's associated cardholder name to learn whether it stores
    /// identity at all. DS-K2604 V2.0 answers error 23 (NOSUPPORT).
    /// </summary>
    private bool ProbeCardUserInfo()
    {
        IntPtr send = Marshal.AllocHGlobal(SendDataSize);
        IntPtr status = Marshal.AllocHGlobal(sizeof(uint));
        IntPtr output = Marshal.AllocHGlobal(UserInfoSize);
        try
        {
            WriteSendData(send, "1");
            Marshal.WriteInt32(status, 0);

            var blank = new byte[UserInfoSize];
            BitConverter.TryWriteBytes(blank.AsSpan(0), UserInfoSize);
            Marshal.Copy(blank, 0, output, UserInfoSize);

            if (HcNetSdk.NET_DVR_GetDeviceConfig(
                    _userId, HcNetSdk.NET_DVR_GET_CARD_USERINFO_CFG, 1, send, SendDataSize,
                    status, output, UserInfoSize))
                return true;

            uint code = HcNetSdk.NET_DVR_GetLastError();
            if (code is HcNetSdk.NET_DVR_NOSUPPORT or HcNetSdk.NET_DVR_PARAMETER_ERROR)
                return false;

            // Anything else (auth lost, transport dead) is a real problem, not a capability
            // answer, and must not be reported as "this panel has no names".
            throw new NvrException(
                $"probing cardholder-name support on {Connection.Label} failed: " +
                HcNetSdk.DescribeError(code), statusCode: (int)code);
        }
        finally
        {
            Marshal.FreeHGlobal(send);
            Marshal.FreeHGlobal(status);
            Marshal.FreeHGlobal(output);
        }
    }

    // ---------- helpers ----------

    private static void ValidateCardNo(string cardNo)
    {
        if (string.IsNullOrWhiteSpace(cardNo))
            throw new ArgumentException("card number is required", nameof(cardNo));
        if (Encoding.ASCII.GetByteCount(cardNo) > CardRecord.CardNoLen)
            throw new ArgumentException(
                $"card number '{cardNo}' is longer than the {CardRecord.CardNoLen} " +
                "characters the device stores", nameof(cardNo));
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_userId >= 0)
            HcNetSdk.NET_DVR_Logout(_userId);
        _runtime.Dispose();
    }
}
