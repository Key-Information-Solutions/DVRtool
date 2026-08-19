using System.Text;
using DVRTool.Core;

namespace DVRTool.Vendors.HikvisionAccess;

/// <summary>Bits of <c>dwModifyParamType</c> — which card fields a write should apply.</summary>
/// <remarks>
/// A set command with <c>dwModifyParamType == 0</c> changes nothing: the device treats
/// every field as "leave alone". Each field written must have its bit set.
/// </remarks>
[Flags]
internal enum CardModifyParam : uint
{
    None = 0,
    CardValid = 0x00000001,
    ValidPeriod = 0x00000002,
    CardType = 0x00000004,
    DoorRight = 0x00000008,
    LeaderCard = 0x00000010,
    MaxSwipeTime = 0x00000020,
    Group = 0x00000040,
    Password = 0x00000080,
    RightPlan = 0x00000100,
    SwipedNum = 0x00000200,
    EmployeeNo = 0x00000400,
    Name = 0x00000800,
    DepartmentNo = 0x00001000,
    SchedulePlanNo = 0x00002000,
    SchedulePlanType = 0x00004000,
    UserType = 0x00040000,
}

/// <summary>
/// A <c>NET_DVR_CARD_CFG_V50</c> record: the exact 2708 bytes the panel sends and accepts.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a wrapper over the raw buffer rather than a marshalled struct. An edit
/// mutates only the fields being changed and leaves every other byte exactly as the device
/// sent it, so features this driver does not model — week plans, holiday groups, card
/// passwords, lock/room codes, the 83 reserved bytes — survive a round trip instead of
/// being zeroed by a well-meaning rewrite. That matters: this tool writes to physical doors.
/// </para>
/// <para>
/// Offsets are byte-verified against live DS-K2604 records (see
/// <c>tests/DVRTool.Tests/Fixtures/card-records.b64</c>).
/// </para>
/// </remarks>
internal sealed class CardRecord
{
    /// <summary><c>sizeof(NET_DVR_CARD_CFG_V50)</c>.</summary>
    internal const int Size = 2708;

    /// <summary><c>ACS_CARD_NO_LEN</c>.</summary>
    internal const int CardNoLen = 32;

    /// <summary><c>MAX_DOOR_NUM</c> — the wire array is always 256 wide.</summary>
    internal const int MaxDoorNum = 256;

    /// <summary><c>NAME_LEN</c>.</summary>
    internal const int NameLen = 32;

    private const int MaxCardRightPlanNum = 4;

    // ---- field offsets (see remarks) ----
    private const int OffSize = 0;
    private const int OffModifyParamType = 4;
    private const int OffCardNo = 8;
    private const int OffCardValid = 40;
    private const int OffCardType = 41;
    private const int OffLeaderCard = 42;
    private const int OffUserType = 43;
    private const int OffDoorRight = 44;
    private const int OffValidPeriod = 300;      // NET_DVR_VALID_PERIOD_CFG, 52 bytes
    private const int OffValidEnable = 300;
    private const int OffValidBeginFlag = 301;
    private const int OffValidEndFlag = 302;
    private const int OffValidBeginTime = 304;   // NET_DVR_TIME_EX, 8 bytes
    private const int OffValidEndTime = 312;
    private const int OffBelongGroup = 352;
    private const int OffCardPassword = 480;
    private const int OffCardRightPlan = 488;    // WORD[256][4]
    private const int OffMaxSwipeTime = 2536;
    private const int OffSwipeTime = 2540;
    private const int OffEmployeeNo = 2548;
    private const int OffName = 2552;
    private const int OffDepartmentNo = 2584;
    private const int OffCardRight = 2612;
    private const int OffPlanTemplate = 2616;
    private const int OffCardUserId = 2620;

    /// <summary>
    /// Plan-template number written alongside a granted door. Every live card observed
    /// carries 1 here for each door it opens; leaving it 0 produces a card that holds the
    /// door right but has no schedule attached and therefore never actually opens.
    /// </summary>
    internal const ushort DefaultRightPlan = 1;

    private readonly byte[] _buffer;

    private CardRecord(byte[] buffer) => _buffer = buffer;

    /// <summary>Wraps bytes received from the device.</summary>
    /// <exception cref="NvrException">The buffer is not a full record.</exception>
    internal static CardRecord FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Size)
            throw new NvrException(
                $"card record is {bytes.Length} bytes, expected {Size} — the SDK build's " +
                "NET_DVR_CARD_CFG_V50 layout does not match this driver.");

        var copy = new byte[Size];
        bytes[..Size].CopyTo(copy);
        return new CardRecord(copy);
    }

    /// <summary>A zeroed record with <c>dwSize</c> set, for creating a card from scratch.</summary>
    internal static CardRecord CreateEmpty()
    {
        var buffer = new byte[Size];
        BitConverter.TryWriteBytes(buffer.AsSpan(OffSize), Size);
        return new CardRecord(buffer);
    }

    internal ReadOnlySpan<byte> Bytes => _buffer;

    internal byte[] ToArray() => (byte[])_buffer.Clone();

    internal uint DeclaredSize => BitConverter.ToUInt32(_buffer, OffSize);

    internal CardModifyParam ModifyParams
    {
        get => (CardModifyParam)BitConverter.ToUInt32(_buffer, OffModifyParamType);
        set => BitConverter.TryWriteBytes(_buffer.AsSpan(OffModifyParamType), (uint)value);
    }

    internal string CardNo
    {
        get => ReadAscii(OffCardNo, CardNoLen);
        set => WriteAscii(OffCardNo, CardNoLen, value, nameof(CardNo));
    }

    internal bool Valid
    {
        get => _buffer[OffCardValid] != 0;
        set => _buffer[OffCardValid] = value ? (byte)1 : (byte)0;
    }

    internal byte CardType
    {
        get => _buffer[OffCardType];
        set => _buffer[OffCardType] = value;
    }

    internal bool IsLeaderCard
    {
        get => _buffer[OffLeaderCard] != 0;
        set => _buffer[OffLeaderCard] = value ? (byte)1 : (byte)0;
    }

    internal bool IsAdmin
    {
        get => _buffer[OffUserType] != 0;
        set => _buffer[OffUserType] = value ? (byte)1 : (byte)0;
    }

    internal uint EmployeeNo
    {
        get => BitConverter.ToUInt32(_buffer, OffEmployeeNo);
        set => BitConverter.TryWriteBytes(_buffer.AsSpan(OffEmployeeNo), value);
    }

    internal uint CardUserId
    {
        get => BitConverter.ToUInt32(_buffer, OffCardUserId);
        set => BitConverter.TryWriteBytes(_buffer.AsSpan(OffCardUserId), value);
    }

    internal ushort DepartmentNo => BitConverter.ToUInt16(_buffer, OffDepartmentNo);

    internal uint CardRight => BitConverter.ToUInt32(_buffer, OffCardRight);

    internal uint PlanTemplate => BitConverter.ToUInt32(_buffer, OffPlanTemplate);

    /// <summary>
    /// Cardholder name. Encoded GB2312 — the SDK's native code page; plain ASCII names
    /// (our <c>First.Last</c> convention) encode identically.
    /// </summary>
    internal string Name
    {
        get => ReadText(OffName, NameLen);
        set => WriteText(OffName, NameLen, value, nameof(Name));
    }

    /// <summary>1-based door numbers this card opens.</summary>
    internal IReadOnlyList<int> Doors
    {
        get
        {
            var doors = new List<int>();
            for (int i = 0; i < MaxDoorNum; i++)
            {
                if (_buffer[OffDoorRight + i] != 0)
                    doors.Add(i + 1);
            }
            return doors;
        }
    }

    /// <summary>
    /// Replaces the door grants, and the matching per-door right-plan slot along with them.
    /// </summary>
    /// <remarks>
    /// Door rights and right plans are set together on purpose: they are two halves of one
    /// permission, and writing either alone yields a card that looks correct in a listing
    /// but does not open the door.
    /// </remarks>
    internal void SetDoors(IEnumerable<int> doors)
    {
        var wanted = new HashSet<int>(doors);
        foreach (int door in wanted)
        {
            if (door is < 1 or > MaxDoorNum)
                throw new ArgumentException(
                    $"door {door} is out of range (1-{MaxDoorNum})", nameof(doors));
        }

        for (int i = 0; i < MaxDoorNum; i++)
        {
            bool granted = wanted.Contains(i + 1);
            _buffer[OffDoorRight + i] = granted ? (byte)1 : (byte)0;

            int planOffset = OffCardRightPlan + i * MaxCardRightPlanNum * sizeof(ushort);
            BitConverter.TryWriteBytes(_buffer.AsSpan(planOffset),
                granted ? DefaultRightPlan : (ushort)0);
        }
    }

    /// <summary>The plan-template number attached to a 1-based door's first slot.</summary>
    internal ushort RightPlanFor(int door)
    {
        if (door is < 1 or > MaxDoorNum)
            throw new ArgumentOutOfRangeException(nameof(door));
        return BitConverter.ToUInt16(_buffer,
            OffCardRightPlan + (door - 1) * MaxCardRightPlanNum * sizeof(ushort));
    }

    internal bool ValidPeriodEnabled
    {
        get => _buffer[OffValidEnable] != 0;
        set => _buffer[OffValidEnable] = value ? (byte)1 : (byte)0;
    }

    internal DateTime? ValidFrom => ValidPeriodEnabled ? ReadTime(OffValidBeginTime) : null;

    internal DateTime? ValidUntil => ValidPeriodEnabled ? ReadTime(OffValidEndTime) : null;

    /// <summary>
    /// Sets the validity window. Times are the panel's own local wall clock, matching the
    /// repo's convention of never silently converting device time.
    /// </summary>
    internal void SetValidPeriod(DateTime from, DateTime until)
    {
        if (until <= from)
            throw new ArgumentException("the validity window must end after it starts", nameof(until));

        ValidPeriodEnabled = true;
        // The device gates on the window itself; the flags mark "unbounded" ends, which we
        // never want for a credential (an unbounded fob is the thing offboarding must avoid).
        _buffer[OffValidBeginFlag] = 0;
        _buffer[OffValidEndFlag] = 0;
        WriteTime(OffValidBeginTime, from);
        WriteTime(OffValidEndTime, until);
    }

    /// <summary>Projects the record onto the vendor-neutral model.</summary>
    internal AccessCard ToAccessCard(string panelHost)
    {
        byte rawType = CardType;
        return new AccessCard
        {
            CardNo = CardNo,
            Valid = Valid,
            Type = Enum.IsDefined(typeof(AccessCardType), (int)rawType)
                ? (AccessCardType)rawType
                : AccessCardType.Unknown,
            NativeCardType = rawType,
            Doors = Doors,
            IsLeaderCard = IsLeaderCard,
            IsAdmin = IsAdmin,
            Name = Name is { Length: > 0 } n ? n : null,
            EmployeeNo = EmployeeNo,
            CardUserId = CardUserId,
            ValidFrom = ValidFrom,
            ValidUntil = ValidUntil,
            PanelHost = panelHost,
        };
    }

    // ---- helpers ----

    private static Encoding DeviceEncoding { get; } = CreateDeviceEncoding();

    private static Encoding CreateDeviceEncoding()
    {
        try
        {
            // .NET Core drops the non-Unicode code pages unless they are registered.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("gb2312");
        }
        catch (ArgumentException)
        {
            // No GB2312 on this platform: ASCII still round-trips the names we write.
            return Encoding.ASCII;
        }
        catch (NotSupportedException)
        {
            return Encoding.ASCII;
        }
    }

    private string ReadAscii(int offset, int length) =>
        Trim(Encoding.ASCII.GetString(_buffer, offset, length));

    private string ReadText(int offset, int length) =>
        Trim(DeviceEncoding.GetString(_buffer, offset, length));

    private static string Trim(string s)
    {
        int nul = s.IndexOf('\0');
        return (nul >= 0 ? s[..nul] : s).Trim();
    }

    private void WriteAscii(int offset, int length, string value, string field)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value ?? "");
        Store(offset, length, bytes, field, value ?? "");
    }

    private void WriteText(int offset, int length, string value, string field)
    {
        byte[] bytes = DeviceEncoding.GetBytes(value ?? "");
        Store(offset, length, bytes, field, value ?? "");
    }

    private void Store(int offset, int length, byte[] bytes, string field, string value)
    {
        // Truncating would write a different credential (or a different person's name)
        // than the caller asked for, so refuse instead.
        if (bytes.Length > length)
            throw new ArgumentException(
                $"{field} '{value}' needs {bytes.Length} bytes but the field holds {length}.");

        Array.Clear(_buffer, offset, length);
        bytes.CopyTo(_buffer, offset);
    }

    /// <summary><c>NET_DVR_TIME_EX</c>: WORD year, then month/day/hour/min/sec/reserved.</summary>
    private DateTime? ReadTime(int offset)
    {
        ushort year = BitConverter.ToUInt16(_buffer, offset);
        int month = _buffer[offset + 2];
        int day = _buffer[offset + 3];
        int hour = _buffer[offset + 4];
        int minute = _buffer[offset + 5];
        int second = _buffer[offset + 6];

        if (year == 0 || month is < 1 or > 12 || day is < 1 or > 31 ||
            hour > 23 || minute > 59 || second > 59)
            return null;

        try
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private void WriteTime(int offset, DateTime t)
    {
        BitConverter.TryWriteBytes(_buffer.AsSpan(offset), (ushort)t.Year);
        _buffer[offset + 2] = (byte)t.Month;
        _buffer[offset + 3] = (byte)t.Day;
        _buffer[offset + 4] = (byte)t.Hour;
        _buffer[offset + 5] = (byte)t.Minute;
        _buffer[offset + 6] = (byte)t.Second;
        _buffer[offset + 7] = 0;
    }
}
