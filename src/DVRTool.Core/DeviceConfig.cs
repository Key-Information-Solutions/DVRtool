namespace DVRTool.Core;

/// <summary>
/// A recorder's own sense of time. Deliberately three separate facts, because the discovery
/// pass (<c>docs/device-config-discovery.md</c>) found a recorder whose wall clock was right
/// and whose declared offset was wrong.
/// </summary>
/// <param name="WallClock">
/// The digits the recorder shows, as a <see cref="DateTimeKind.Unspecified"/> local time —
/// the same "keep the wall clock, drop the offset" rule <c>HikvisionClient.ParseIsapiTime</c>
/// already follows.
/// </param>
/// <param name="DeclaredOffset">
/// The UTC offset the recorder *claims*, when it states one. Informational only, and never
/// used to build an instant: Hikvision firmware states −05:00 while standing in −04:00,
/// ignoring the DST rule in its own timeZone string. Null when the vendor states no offset
/// (Dahua's <c>getCurrentTime</c>) or has no such concept.
/// </param>
/// <param name="VendorZoneLabel">
/// The zone as the vendor names it, verbatim and unparsed: a POSIX-ish string on Hikvision
/// (<c>CST+5:00:00DST01:00:00,M3.2.0/02:00:00,M11.1.0/02:00:00</c>), an index plus a label on
/// Dahua (<c>25</c> / <c>Easterntime</c>). Not an IANA id and not convertible to one; it is
/// shown to an operator, never computed with.
/// </param>
/// <param name="DstEnabled">
/// Whether the device applies daylight saving. On Dahua this is its own switch
/// (<c>Locales.DSTEnable</c>) and is the field that made a recorder an hour slow; on
/// Hikvision DST is folded into <paramref name="VendorZoneLabel"/> and this is null.
/// </param>
public sealed record DeviceClock(
    DateTime WallClock,
    TimeSpan? DeclaredOffset,
    string? VendorZoneLabel,
    bool? DstEnabled)
{
    /// <summary>The wall clock as the recorder's own UI shows it, seconds included.</summary>
    public string WallClockText => WallClock == default
        ? "?"
        : WallClock.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>The declared offset in the form the vendor documents show it (<c>-05:00</c>).</summary>
    public string? DeclaredOffsetText => DeclaredOffset is TimeSpan o
        ? (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString(@"hh\:mm")
        : null;
}

/// <summary>
/// Whether a recorder has anything keeping its clock honest. Read separately from
/// <see cref="DeviceClock"/> and kept deliberately small, because the fleet audit needs it
/// for every device: a recorder with no time source is the fault with no symptom today —
/// its clock is fine until it isn't, and nothing in a single reading says so.
/// </summary>
/// <param name="NtpEnabled">
/// Null where the recorder has no NTP client to have an opinion about (Nx keeps a
/// distributed clock instead), which is a different state from "off".
/// </param>
/// <param name="Detail">
/// What the time source actually is, in the vendor's own terms — the server address and
/// interval, or Nx's clock-master setting — for the row's tooltip. Empty when unknown.
/// </param>
public sealed record TimeSourceStatus(bool? NtpEnabled, string Detail = "")
{
    /// <summary>Nothing was readable: not "off", not "none" — unknown.</summary>
    // Named argument on purpose: `new(null)` binds to the record's copy constructor.
    public static readonly TimeSourceStatus Unknown = new(NtpEnabled: null);
}

/// <summary>
/// How far a recorder's clock is from the operator's, measured the way an integrator asks
/// the question: same wall-clock digits or not.
/// </summary>
/// <remarks>
/// <para>
/// Drift is <b>device wall clock − reference wall clock</b>, both naive local times. It is
/// deliberately *not* computed from instants: doing that requires trusting the device's
/// declared offset, and the discovery pass proved the offset is the field that lies. This
/// also means the measurement needs no zone database and no vendor zone parsing — the two
/// things that would make it wrong in a new way per firmware.
/// </para>
/// <para>
/// The cost of the choice: a recorder deliberately set to a different zone from the
/// workstation reads as drifted by the zone difference. That is handled by naming it — the
/// reported <see cref="DeviceClock.VendorZoneLabel"/> rides along in every audit row — and,
/// for a fleet spanning zones, by <c>SavedDevice.ExpectedOffsetMinutes</c>, never by
/// inferring intent.
/// </para>
/// </remarks>
public sealed record ClockDrift
{
    public required TimeSpan Drift { get; init; }
    public required DateTime ReferenceWallClock { get; init; }

    /// <summary>Round-trip time of the read, the measurement's own error bar.</summary>
    public required TimeSpan ReadLatency { get; init; }

    /// <summary>
    /// The device states an offset that disagrees with the reference's actual offset. Not by
    /// itself a fault — it is a fault *report*, since the wall clock may still be right.
    /// </summary>
    public bool DeclaredOffsetDiffers { get; init; }

    /// <summary>
    /// The reference was shifted by a per-device expected offset, so this recorder is
    /// measured against the zone it is meant to be in rather than the workstation's.
    /// </summary>
    public int? ExpectedOffsetMinutes { get; init; }

    /// <summary>Beyond the tolerance a recorder's timestamps can be trusted at.</summary>
    public bool IsSignificant => Drift.Duration() > Tolerance;

    /// <summary>
    /// One minute. Below that, NTP jitter, read latency and the recorder's own
    /// second-granularity reporting are indistinguishable from real drift; above it, an
    /// export's filename minute is wrong.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(1);

    /// <summary>"58 min behind", "3 s ahead", "1 h 2 min behind", or "in step".</summary>
    public string Text => Describe(Drift);

    /// <summary>
    /// Measures one reading. Both ends of the round trip are taken so
    /// <see cref="ReadLatency"/> is real and the reference is its midpoint — a relay read
    /// through DW Cloud can take a second, and a one-second error bar on a one-minute
    /// tolerance has to be visible rather than assumed away.
    /// </summary>
    /// <param name="readStarted">
    /// When the read was issued, <b>in the reference's own zone</b> — the operator's
    /// <see cref="DateTimeOffset.Now"/> in both front ends. Its wall clock is what the
    /// device's is compared against and its offset is what
    /// <see cref="DeclaredOffsetDiffers"/> is judged against, so the measurement never
    /// consults the machine's zone database.
    /// </param>
    /// <param name="readFinished">When the answer came back, same zone.</param>
    /// <param name="expectedOffsetMinutes">
    /// How far this recorder's wall clock is *meant* to sit from the workstation's, in
    /// minutes (a recorder one zone east is +60). Null — the normal case — means "same wall
    /// clock as me".
    /// </param>
    public static ClockDrift Measure(DeviceClock clock, DateTimeOffset readStarted,
        DateTimeOffset readFinished, int? expectedOffsetMinutes = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (readFinished < readStarted)
            (readStarted, readFinished) = (readFinished, readStarted);

        var latency = readFinished - readStarted;
        var midpoint = readStarted + latency / 2;

        // The reference is the operator's own wall clock — the digits on the workstation —
        // for exactly the reason the device's offset is ignored: comparing digits needs no
        // zone data from either side. It comes from the offsets the caller passed in, not
        // from this machine's zone, so a measurement is reproducible off a fixture.
        var reference = midpoint.DateTime;
        if (expectedOffsetMinutes is int shift)
            reference = reference.AddMinutes(shift);

        return new ClockDrift
        {
            Drift = clock.WallClock - reference,
            ReferenceWallClock = DateTime.SpecifyKind(reference, DateTimeKind.Unspecified),
            ReadLatency = latency,
            ExpectedOffsetMinutes = expectedOffsetMinutes,
            // The reference's real offset, not the one the device claims: on 9 September a
            // Hikvision recorder in EDT reported −05:00 while the workstation stood at −04:00.
            DeclaredOffsetDiffers = clock.DeclaredOffset is TimeSpan declared &&
                declared != midpoint.Offset,
        };
    }

    /// <summary>Renders a signed drift the way an operator says it out loud.</summary>
    public static string Describe(TimeSpan drift)
    {
        var size = drift.Duration();
        if (size <= TimeSpan.FromSeconds(1))
            return "in step";
        string direction = drift > TimeSpan.Zero ? "ahead" : "behind";
        string amount =
            size < TimeSpan.FromMinutes(1) ? $"{size.TotalSeconds:0} s"
            : size < TimeSpan.FromHours(1) ? $"{size.TotalMinutes:0} min"
            : size.Minutes == 0 ? $"{(int)size.TotalHours} h"
            : $"{(int)size.TotalHours} h {size.Minutes} min";
        return $"{amount} {direction}";
    }
}

/// <summary>Which of DVRTool's own port fields a service port corresponds to, if any.</summary>
public enum ServiceKind { Http, Https, Rtsp, Sdk, SdkOverTls, WebSocket, Iot, Other }

/// <summary>A device-declared bound: what the firmware says the field may hold.</summary>
public sealed record ValueRange(int Min, int Max, int? Default = null)
{
    public bool Contains(int value) => value >= Min && value <= Max;

    /// <summary>"1024–65535 (default 554)".</summary>
    public string Text => Default is int d ? $"{Min}–{Max} (default {d})" : $"{Min}–{Max}";
}

/// <summary>
/// The bounds used where a vendor declares none. Hikvision self-describes every service
/// port; Dahua declares nothing at all outside <c>encode</c>, so its ranges are ours — and
/// both front ends say whose they are, because an operator refused a port by our constant
/// deserves to know it was our constant.
/// </summary>
public static class ServicePortRange
{
    public static readonly ValueRange Fallback = new(1, 65535);

    public const string FallbackLabel = "DVRTool's bound — the device declares no range";
}

/// <summary>One service port as the recorder configures it.</summary>
/// <param name="Protocol">The vendor's own name, verbatim — <c>DEV_MANAGE</c>, not "SDK".</param>
/// <param name="Enabled">
/// Per port, and HTTPS ships false on Hikvision — a config view that renders the number
/// without the switch invites "I set 443 and nothing happened".
/// </param>
/// <param name="Range">
/// The device's declared bounds, when it declares any. Null means the vendor does not
/// self-describe (all of Dahua), and the front ends fall back to
/// <see cref="ServicePortRange.Fallback"/> — a constant of ours, labelled as ours.
/// </param>
/// <param name="Extras">
/// The per-protocol oddments that hang off single ports — <c>redirectToHttps</c>,
/// <c>TLS1_1Enable</c>, <c>TLS1_2Enable</c> on HTTPS, <c>streamOverTls</c> on
/// <c>SDK_OVER_TLS</c>, the RTP range on Dahua's RTSP — carried rather than modelled, so one
/// port's quirk does not become a field on every port.
/// </param>
public sealed record ServicePort(
    int Id,
    string Protocol,
    ServiceKind Kind,
    int Port,
    bool Enabled,
    ValueRange? Range = null,
    IReadOnlyDictionary<string, string>? Extras = null)
{
    public IReadOnlyDictionary<string, string> Extras { get; init; } =
        Extras ?? new Dictionary<string, string>();

    /// <summary>The bounds to enforce, and whether they are the device's own.</summary>
    public (ValueRange Range, bool FromDevice) EffectiveRange =>
        Range is null ? (ServicePortRange.Fallback, false) : (Range, true);
}

/// <summary>One configured NTP server.</summary>
/// <param name="Id">The vendor's own index (1-based on Hikvision, 0-based in Dahua's list).</param>
public sealed record NtpServer(int Id, string Address, int Port = 123, bool Enabled = true);

public enum AddressingType { Unknown, Static, Dhcp, Apipa }

/// <summary>
/// One network interface of the recorder itself.
/// </summary>
/// <param name="IsDefault">
/// Not decoration: Dahua's 128-channel chassis lists six interfaces, four of them
/// unconfigured bonds, so "the LAN address" is the default interface's address and never a
/// fixed key.
/// </param>
/// <param name="AddressingOptions">
/// The addressing types the *device* declares (Hikvision's <c>opt=</c> list). Empty means the
/// vendor declares none and the choices, like the ranges, would be ours — which the front end
/// must be able to tell apart from a device-declared list.
/// </param>
public sealed record NetworkInterfaceConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
    public AddressingType AddressingType { get; init; }
    public string IpAddress { get; init; } = "";
    public string SubnetMask { get; init; } = "";
    public string Gateway { get; init; } = "";
    public IReadOnlyList<string> Dns { get; init; } = [];
    public bool? DnsAuto { get; init; }
    public int? Mtu { get; init; }
    public string MacAddress { get; init; } = "";
    public int? LinkSpeedMbps { get; init; }
    public bool? LinkUp { get; init; }
    public ValueRange? MtuRange { get; init; }
    public IReadOnlyList<string> AddressingOptions { get; init; } = [];

    /// <summary>"static", "DHCP", "APIPA" — the addressing type as an operator says it.</summary>
    public string AddressingText => AddressingType switch
    {
        AddressingType.Static => "static",
        AddressingType.Dhcp => "DHCP",
        AddressingType.Apipa => "APIPA",
        _ => "?",
    };
}

/// <summary>
/// A vendor fact with nowhere better to live — DDNS, the UPnP map, PPPoE, link state, a
/// lockout policy, Nx's clock-master settings. Shown verbatim and never computed with.
/// </summary>
public sealed record ConfigNote(string Group, string Label, string Value)
{
    public override string ToString() => $"{Group}: {Label} = {Value}";
}

/// <summary>
/// Which parts of the configuration model this recorder has at all. The load-bearing
/// distinction of the whole feature: <b>"this recorder has no LAN address to show" is not
/// "the read failed"</b>. The Users tab already draws this line for accounts — an unreadable
/// device shows "?" and is excluded from row status, never read as "missing" — and the config
/// views draw it the same way, with "n/a" for out of scope and "?" only for a failed read.
/// </summary>
public sealed record ConfigScope
{
    public required bool Clock { get; init; }
    public required bool Ntp { get; init; }
    public required bool TimeZone { get; init; }
    public required bool Network { get; init; }
    public required bool Ports { get; init; }
    public required bool DeviceName { get; init; }

    /// <summary>Why the missing parts are missing, for the "n/a" tooltip. Empty when nothing is.</summary>
    public string Reason { get; init; } = "";

    /// <summary>An appliance recorder: all of it, and every part is the recorder's own.</summary>
    public static readonly ConfigScope Appliance = new()
    {
        Clock = true,
        Ntp = true,
        TimeZone = true,
        Network = true,
        Ports = true,
        DeviceName = true,
    };

    /// <summary>
    /// Nx: a clock and nothing else. Its time keys configure which server in the system is
    /// the clock master by GUID, not an NTP client; zone and address belong to Windows on
    /// the box.
    /// </summary>
    public static readonly ConfigScope ClockOnly = new()
    {
        Clock = true,
        Ntp = false,
        TimeZone = false,
        Network = false,
        Ports = false,
        DeviceName = true,
        Reason = "A software VMS keeps one clock for the whole system and takes its zone, " +
            "address and ports from the Windows box it runs on — there is no device " +
            "configuration to read, which is not the same as a read that failed.",
    };
}

/// <summary>Everything one recorder says about its own configuration.</summary>
public sealed record DeviceConfiguration
{
    public required DeviceClock Clock { get; init; }
    public string? DeviceName { get; init; }
    public string? Model { get; init; }
    public string? FirmwareVersion { get; init; }

    /// <summary>Null where the vendor has no NTP client of its own (Nx).</summary>
    public IReadOnlyList<NtpServer>? NtpServers { get; init; }
    public bool? NtpEnabled { get; init; }
    public TimeSpan? NtpInterval { get; init; }

    /// <summary>Null where the address belongs to the host OS rather than the recorder (Nx).</summary>
    public IReadOnlyList<NetworkInterfaceConfig>? Interfaces { get; init; }

    /// <summary>Empty where the vendor exposes no port configuration; never null-as-unknown.</summary>
    public IReadOnlyList<ServicePort> Ports { get; init; } = [];

    /// <summary>What this recorder *can* be asked, so a blank cell can be explained.</summary>
    public required ConfigScope Scope { get; init; }

    /// <summary>Vendor facts with nowhere better to live: DDNS, UPnP map, PPPoE, link state.</summary>
    public IReadOnlyList<ConfigNote> Notes { get; init; } = [];

    /// <summary>
    /// The parts that were in scope and still could not be read, each with why. A recorder
    /// that answers the clock and refuses the port list is worth a partial answer — the clock
    /// is the part that matters — so a per-part failure lands here instead of throwing.
    /// </summary>
    public IReadOnlyList<ConfigNote> Failures { get; init; } = [];

    /// <summary>The interface "the LAN address" means, or null when there is none.</summary>
    public NetworkInterfaceConfig? DefaultInterface =>
        Interfaces?.FirstOrDefault(i => i.IsDefault) ?? Interfaces?.FirstOrDefault();

    /// <summary>The port carrying one of DVRTool's own port fields, when the device lists it.</summary>
    public ServicePort? PortOf(ServiceKind kind) => Ports.FirstOrDefault(p => p.Kind == kind);

    public TimeSourceStatus TimeSource => new(NtpEnabled, NtpSummary);

    /// <summary>"time.windows.com:123, every 60 min", or "" when there is nothing to say.</summary>
    public string NtpSummary
    {
        get
        {
            if (NtpServers is null)
                return "";
            var parts = new List<string>();
            foreach (var s in NtpServers.Where(s => s.Address.Length > 0))
                parts.Add(s.Port == 123 ? s.Address : $"{s.Address}:{s.Port}");
            if (parts.Count == 0)
                return NtpEnabled is true ? "enabled, no server set" : "no server set";
            string list = string.Join(", ", parts);
            if (NtpInterval is TimeSpan interval && interval > TimeSpan.Zero)
                list += $", every {interval.TotalMinutes:0} min";
            return NtpEnabled is false ? $"{list} (disabled)" : list;
        }
    }
}

/// <summary>What a clock/zone write was asked to do. A null field is left alone.</summary>
/// <param name="VendorZoneLabel">
/// The vendor's own zone value — a Hikvision <c>timeZone</c> string, a Dahua index. There is
/// no IANA mapping and inventing one would silently mis-set clocks, which is the failure this
/// whole feature exists to catch.
/// </param>
/// <param name="WallClock">
/// A wall clock to stamp onto the recorder, for the "set it to this workstation" path.
/// </param>
/// <param name="UseNtp">Whether the recorder should keep its own clock from NTP.</param>
public sealed record TimeSettings(
    string? VendorZoneLabel = null,
    bool? DstEnabled = null,
    DateTime? WallClock = null,
    bool? UseNtp = null);

/// <summary>What an NTP write was asked to do. A null field is left alone.</summary>
public sealed record NtpSettings(
    string? Address = null,
    int? Port = null,
    TimeSpan? Interval = null,
    bool? Enabled = null);

/// <summary>
/// What the recorder held before a config write, what it holds after, and whether anything
/// moved. Mirrors <see cref="RecordingOptionChange"/> exactly, and for the same reason: write
/// only when the requested state differs, then read back — a recorder that accepts a field it
/// does not honour is the failure mode worth catching, and only the read-back catches it.
/// </summary>
/// <param name="Field">What was asked, in words, for the report ("NTP server").</param>
/// <param name="Note">
/// Why nothing changed, or what the recorder did instead of what was asked. Empty on a plain
/// successful change.
/// </param>
public sealed record ConfigChange(
    string Field,
    DeviceConfiguration Before,
    DeviceConfiguration After,
    bool Changed,
    string Note = "")
{
    /// <summary>A change the recorder refused or silently dropped: asked, but not applied.</summary>
    public bool Rejected { get; init; }
}

/// <summary>
/// Opt-in capability: reading — and, through <see cref="IDeviceConfigWriter"/>, changing —
/// the recorder's own configuration, as opposed to the cameras on it. Separate from
/// <see cref="INvrClient"/> like <see cref="IStorageClient"/> and
/// <see cref="IRecordingOptionsClient"/>, because what a recorder exposes varies from "all of
/// it" to "a clock".
/// </summary>
public interface IDeviceConfigClient
{
    /// <summary>
    /// The whole configuration in one call. Implementations issue several requests and
    /// tolerate a per-part failure by leaving that part null and recording it in
    /// <see cref="DeviceConfiguration.Failures"/> — a recorder that answers the clock and
    /// refuses the port list is worth a partial answer, since the clock is the part that
    /// matters.
    /// </summary>
    Task<DeviceConfiguration> GetConfigurationAsync(CancellationToken ct = default);

    /// <summary>Just the clock, for a fleet sweep that reads 17 recorders.</summary>
    Task<DeviceClock> GetClockAsync(CancellationToken ct = default);

    /// <summary>
    /// Whether the clock has anything keeping it honest — one cheap read, so the fleet audit
    /// can say "no time source" about a recorder whose clock happens to be right today.
    /// </summary>
    Task<TimeSourceStatus> GetTimeSourceAsync(CancellationToken ct = default);
}

/// <summary>
/// The write half. Kept a separate interface so a vendor can ship the read tier without a
/// write path existing at all (Nx has no writer: a distributed clock master is not an NTP
/// server, and setting a zone means logging into Windows).
/// </summary>
/// <remarks>
/// Read-modify-write is mandatory and enforced: the M-series NTP document carries
/// <c>portType</c>, <c>customPortNo</c> and <c>hostNameExampleList</c> that the I-series
/// document does not, so a hand-built minimal PUT drops them on every save. Implementations
/// therefore hold the document from their last read, refuse to write without one, replace only
/// the requested fields, and re-read before writing so a document edited from a vendor console
/// in the meantime is refused rather than clobbered.
/// </remarks>
public interface IDeviceConfigWriter : IDeviceConfigClient
{
    Task<ConfigChange> SetTimeAsync(TimeSettings requested, CancellationToken ct = default);
    Task<ConfigChange> SetNtpAsync(NtpSettings requested, CancellationToken ct = default);
    Task<ConfigChange> SetDeviceNameAsync(string name, CancellationToken ct = default);

    /// <summary>Stamps this workstation's wall clock onto the recorder.</summary>
    Task<ConfigChange> SyncTimeNowAsync(CancellationToken ct = default);
}
