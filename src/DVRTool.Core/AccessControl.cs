namespace DVRTool.Core;

/// <summary>Connection settings for one access-control panel.</summary>
/// <remarks>
/// Deliberately separate from <see cref="NvrConnection"/>: door panels are not reached
/// over HTTP at all. Hikvision DS-K controllers expose only the private SDK protocol on
/// port 8000 — no ISAPI, no web server — so there is no HttpBase to share.
/// </remarks>
public sealed record AccessPanelConnection
{
    public required string Host { get; init; }
    public int SdkPort { get; init; } = VendorPorts.HikvisionSdk;
    public required string Username { get; init; }
    public required string Password { get; init; }

    public string Label => SdkPort == VendorPorts.HikvisionSdk ? Host : $"{Host}:{SdkPort}";
}

/// <summary>
/// Hikvision <c>byCardType</c>. Numbering is the device's, not ours — a card read back
/// with an unmapped value keeps it in <see cref="AccessCard.NativeCardType"/>.
/// </summary>
public enum AccessCardType
{
    Unknown = 0,
    Normal = 1,
    DisabledPerson = 2,
    BlockList = 3,
    Patrol = 4,
    /// <summary>Opens the door but silently raises a duress alarm.</summary>
    Duress = 5,
    Super = 6,
    Visitor = 7,
    /// <summary>Clears an active alarm; does not open a door.</summary>
    Dismiss = 8,
    Staff = 9,
    Emergency = 10,
    /// <summary>Grants emergency rights to other cards; cannot open a door itself.</summary>
    EmergencyAdmin = 11,
}

/// <summary>
/// One credential (fob/card) as stored on a single panel.
/// </summary>
/// <remarks>
/// This is the panel's actual primitive. DS-K controllers have no "person" record: the
/// card number is the identity, and door rights hang off it. <see cref="Name"/> and
/// <see cref="EmployeeNo"/> exist in the wire struct but are blank on DS-K2604 V2.0
/// firmware — that firmware stores no cardholder identity at all, so a name-based
/// lookup cannot be answered from the panels alone. See
/// <c>docs/hikvision-access-control-handoff.md</c>.
/// </remarks>
public sealed record AccessCard
{
    /// <summary>Fob number as the panel stores it (ASCII digits, up to 32 chars).</summary>
    public required string CardNo { get; init; }

    /// <summary>
    /// False means the card is revoked. Writing <c>false</c> is how a card is deleted —
    /// the device has no separate delete verb.
    /// </summary>
    public bool Valid { get; init; } = true;

    public AccessCardType Type { get; init; } = AccessCardType.Normal;

    /// <summary>Raw <c>byCardType</c>, preserved so unmapped vendor values survive a round trip.</summary>
    public byte NativeCardType { get; init; } = (byte)AccessCardType.Normal;

    /// <summary>1-based door numbers this card opens on <see cref="PanelHost"/>.</summary>
    public IReadOnlyList<int> Doors { get; init; } = [];

    /// <summary>First-card / "leader" card: unlocks the door for others to follow.</summary>
    public bool IsLeaderCard { get; init; }

    /// <summary><c>byUserType</c> 1 — administrator rather than ordinary holder.</summary>
    public bool IsAdmin { get; init; }

    /// <summary>
    /// Cardholder name from <c>byName</c>. Empty on DS-K2604 V2.0 — see the type remarks.
    /// </summary>
    public string? Name { get; init; }

    public uint EmployeeNo { get; init; }

    /// <summary><c>dwCardUserId</c> — cardholder id. 0 on DS-K2604 V2.0.</summary>
    public uint CardUserId { get; init; }

    /// <summary>Validity window, when the panel enforces one (<c>struValid.byEnable</c>).</summary>
    public DateTime? ValidFrom { get; init; }

    public DateTime? ValidUntil { get; init; }

    /// <summary>Which panel this record was read from. Null for a card being written.</summary>
    public string? PanelHost { get; init; }

    /// <summary>Whether the panel reported any cardholder identity for this card.</summary>
    public bool HasIdentity => !string.IsNullOrWhiteSpace(Name) || EmployeeNo != 0;

    public string DoorSummary => Doors.Count == 0 ? "(none)" : string.Join(",", Doors);
}

/// <summary>What one panel actually supports, probed rather than assumed.</summary>
/// <param name="SupportsCardholderNames">
/// True when the panel answers the card→cardholder-name channel
/// (<c>NET_DVR_GET_CARD_USERINFO_CFG</c>). False on DS-K2604 V2.0 firmware, which
/// rejects it as unsupported — the panels there hold card numbers and door rights only.
/// </param>
/// <param name="DoorCount">
/// How many doors the controller drives, inferred from its model (a DS-K2604 is 4-door).
/// Null when the model is not recognized — DS-K2604 V2.0 firmware refuses the SDK
/// capability-set query outright, so this is a hint, not a reading.
/// </param>
public sealed record AccessPanelCapabilities(bool SupportsCardholderNames, int? DoorCount);

/// <summary>
/// Opt-in capability: read and write the credentials provisioned on a door-access panel.
/// A sibling of <see cref="IUserManagementClient"/> — separate because a device that
/// records video and a device that controls doors share nothing but a vendor name.
/// </summary>
public interface IAccessControlClient : IDisposable
{
    Vendor Vendor { get; }
    AccessPanelConnection Connection { get; }

    Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default);

    Task<AccessPanelCapabilities> GetCapabilitiesAsync(CancellationToken ct = default);

    /// <summary>Every credential on the panel. Read-only; no side effects.</summary>
    Task<IReadOnlyList<AccessCard>> GetCardsAsync(CancellationToken ct = default);

    /// <summary>One credential by fob number, or null when the panel has no such card.</summary>
    Task<AccessCard?> GetCardAsync(string cardNo, CancellationToken ct = default);

    /// <summary>
    /// Create or update a credential. Writing an existing card number overwrites its
    /// rights, so callers are expected to read first and confirm.
    /// </summary>
    Task UpsertCardAsync(AccessCard card, CancellationToken ct = default);

    /// <summary>
    /// Revoke a credential. On Hikvision this writes <c>byCardValid = 0</c>, which is
    /// the device's own delete mechanism.
    /// </summary>
    Task RevokeCardAsync(string cardNo, CancellationToken ct = default);
}
