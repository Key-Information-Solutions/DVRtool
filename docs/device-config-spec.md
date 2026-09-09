# Device configuration: implementation spec

The feature the feasibility pass sized and the discovery pass grounded: viewing — and, in a
later tier, changing — a recorder's own configuration (clock, NTP, service ports, LAN
address) from the GUI and the CLI.

Read `docs/device-config-discovery.md` first. Every endpoint, field name, range and quirk
below is an **observed** response from that pass, not a recalled one; this document turns
those observations into a build.

## 0. The shape of the decision

Three tiers, shipped in order, each useful alone:

| tier | what | gate | ~LOC |
|---|---|---|---|
| **1 — read** | clock, NTP, ports, LAN address, device name, per-vendor "not applicable"; the **fleet clock audit** | none | ~1.6k |
| **2 — safe writes** | NTP server/interval, time zone, sync-now, device name | `--force` / Apply, read-back | +~400 |
| **3 — address & port writes** | IP/mask/gateway/DNS/DHCP, service ports | confirmation + re-key protocol (§7) | +~600 |

**Tier 1 is the product**, not a stepping stone. The discovery pass found two wrong clocks on
the first three recorders it read — one recorder 58 minutes behind, stamping an hour of wrong
time onto every recording it writes — and nothing in DVRTool would have said so. Footage
search, timeline geometry (`TimelineWindow`, `PlaybackClock`) and export naming
(`ExportNaming`) all run on NVR-local wall clock. Reading the clock is a **correctness check
on the export product already shipped**, which is a different kind of value from
"config editing", and it is the reason to build tier 1 before deciding whether tier 3 ever
happens.

Tier 3 stays last, or never. Nothing in the discovery pass weakened the argument against it:
a network write cannot be verified on the socket that issued it, and a subnet change made
*through a port forward* is unrecoverable by definition.

## 1. Core model — `src/DVRTool.Core/DeviceConfig.cs`

Same construction as `RecordingOptions.cs`: records plus one opt-in interface, no I/O, so the
model is identical whether the read came from the GUI, the CLI or a fixture.

### 1.1 The clock, and why drift is measured against the workstation

```csharp
/// <summary>
/// A recorder's own sense of time. Deliberately three separate facts, because the discovery
/// pass found a recorder whose wall clock was right and whose declared offset was wrong.
/// </summary>
/// <param name="WallClock">
/// The digits the recorder shows, as an <see cref="DateTimeKind.Unspecified"/> local time —
/// the same "keep the wall clock, drop the offset" rule
/// <c>HikvisionClient.ParseIsapiTime</c> already follows.
/// </param>
/// <param name="DeclaredOffset">
/// The UTC offset the recorder *claims*, when it states one. Informational only, and never
/// used to build an instant: Hikvision firmware states -05:00 while standing in -04:00,
/// ignoring the DST rule in its own timeZone string. Null when the vendor states no offset
/// (Dahua's getCurrentTime) or has no such concept.
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
    bool? DstEnabled);
```

```csharp
/// <summary>
/// How far a recorder's clock is from the operator's, measured the way an integrator asks
/// the question: same wall-clock digits or not.
/// </summary>
/// <remarks>
/// Drift is <b>device wall clock − reference wall clock</b>, both naive local times. It is
/// deliberately *not* computed from instants: doing that requires trusting the device's
/// declared offset, and the discovery pass proved the offset is the field that lies. This
/// also means the measurement needs no zone database and no vendor zone parsing — the two
/// things that would make it wrong in a new way per firmware.
///
/// The cost of the choice: a recorder deliberately set to a different zone from the
/// workstation reads as drifted by the zone difference. That is handled by naming it —
/// <see cref="ZoneLabelDiffers"/> and the reported <see cref="DeviceClock.VendorZoneLabel"/>
/// — and, for a fleet spanning zones, by <c>ExpectedOffsetMinutes</c> on the saved device
/// (§6.3), never by inferring intent.
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

    /// <summary>Beyond the tolerance a recorder's timestamps can be trusted at.</summary>
    public bool IsSignificant => Drift.Duration() > Tolerance;

    /// <summary>
    /// One minute. Below that, NTP jitter, read latency and the recorder's own
    /// second-granularity reporting are indistinguishable from real drift; above it, an
    /// export's filename minute is wrong.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(1);

    public static ClockDrift Measure(DeviceClock clock, DateTimeOffset readStarted,
        DateTimeOffset readFinished);
}
```

`Measure` takes both ends of the round trip so `ReadLatency` is real and the reference is the
midpoint — a relay read through DW Cloud can take a second, and a one-second error bar on a
one-minute tolerance has to be visible rather than assumed away.

### 1.2 Ports

```csharp
/// <summary>Which of DVRTool's own port fields a service port corresponds to, if any.</summary>
public enum ServiceKind { Http, Https, Rtsp, Sdk, SdkOverTls, WebSocket, Iot, Other }

/// <param name="Protocol">The vendor's own name, verbatim — <c>DEV_MANAGE</c>, not "SDK".</param>
/// <param name="Range">
/// The device's declared bounds, when it declares any. Null means the vendor does not
/// self-describe (all of Dahua), and the front ends fall back to
/// <see cref="ServicePortRange.Fallback"/> — a constant of ours, labelled as ours.
/// </param>
public sealed record ServicePort(
    int Id,
    string Protocol,
    ServiceKind Kind,
    int Port,
    bool Enabled,
    ValueRange? Range,
    IReadOnlyDictionary<string, string> Extras);

public sealed record ValueRange(int Min, int Max, int? Default);
```

`Extras` carries the per-protocol oddments the discovery pass found hanging off single ports —
`redirectToHttps`, `TLS1_1Enable`, `TLS1_2Enable` on HTTPS, `streamOverTls` on
`SDK_OVER_TLS` — without inventing a field on every port for a thing one port has. They are
shown, and in tier 3 they round-trip; they are not modelled.

Two rules the parser owes the discovery pass:

- **`def=` and `default=` both mean default**, in the same document — ids 1/2/5/6 use one
  spelling, 4/7 the other. One helper reads both; a unit test asserts both spellings on one
  fixture, because losing the SDK port's default is silent.
- **`enabled` is per port and HTTPS ships false.** The switch renders beside the number, or
  an operator sets 443 and watches nothing happen.

### 1.3 The whole document

```csharp
public sealed record DeviceConfiguration
{
    public required DeviceClock Clock { get; init; }
    public string? DeviceName { get; init; }

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
}

/// <summary>
/// Which parts of the model this recorder has at all. The load-bearing distinction of the
/// whole feature: <b>"this recorder has no LAN address to show" is not "the read failed"</b>.
/// The Users tab already draws this line for accounts — an unreadable device shows "?" and is
/// excluded from row status, never read as "missing" — and the config views draw it the same
/// way, with "n/a" for out of scope and "?" only for a failed read.
/// </summary>
public sealed record ConfigScope
{
    public required bool Clock { get; init; }
    public required bool Ntp { get; init; }
    public required bool TimeZone { get; init; }
    public required bool Network { get; init; }
    public required bool Ports { get; init; }
    public required bool DeviceName { get; init; }

    public static readonly ConfigScope Appliance = /* everything true */;

    /// <summary>
    /// Nx: a clock and nothing else. Its time keys configure which server in the system is
    /// the clock master by GUID, not an NTP client; zone and address belong to Windows on
    /// the box.
    /// </summary>
    public static readonly ConfigScope ClockOnly = /* Clock only */;
}
```

`NetworkInterfaceConfig` carries `Id`, `Name`, `IsDefault`, `AddressingType`
(`Static`/`Dhcp`/`Apipa`), `IpAddress`, `SubnetMask`, `Gateway`, `Dns` (a list, both vendors
give two), `DnsAuto`, `Mtu`, `MacAddress`, `LinkSpeedMbps?`, `LinkUp?`, plus its own
`ValueRange? MtuRange` and `IReadOnlyList<string> AddressingOptions` — the `opt=` values,
because on Hikvision they are declared and on Dahua they are ours to hardcode, and the front
end must be able to tell those apart.

**`IsDefault` is not decoration.** Dahua's 128-channel chassis lists six interfaces, four of
them unconfigured bonds; "the LAN address" is `Network.<DefaultInterface>.IPAddress` and never
a fixed key. Hikvision has one interface. The GUI shows the default one and lets the rest be
expanded.

### 1.4 The interface

```csharp
/// <summary>
/// Opt-in capability: reading — and, from tier 2, changing — the recorder's own
/// configuration, as opposed to the cameras on it. Separate from <see cref="INvrClient"/>
/// like <see cref="IStorageClient"/> and <see cref="IRecordingOptionsClient"/>, because what
/// a recorder exposes varies from "all of it" to "a clock".
/// </summary>
public interface IDeviceConfigClient
{
    /// <summary>
    /// The whole configuration in one call. Implementations issue several requests and
    /// tolerate a per-part failure by leaving that part null and adding a
    /// <see cref="ConfigNote"/> — a recorder that answers the clock and refuses the port
    /// list is worth a partial answer, since the clock is the part that matters.
    /// </summary>
    Task<DeviceConfiguration> GetConfigurationAsync(CancellationToken ct = default);

    /// <summary>Just the clock, for a fleet sweep that reads 17 recorders.</summary>
    Task<DeviceClock> GetClockAsync(CancellationToken ct = default);
}

/// <summary>Tier 2. Kept separate so tier 1 ships without a write path existing at all.</summary>
public interface IDeviceConfigWriter : IDeviceConfigClient
{
    Task<ConfigChange> SetTimeAsync(TimeSettings requested, CancellationToken ct = default);
    Task<ConfigChange> SetNtpAsync(NtpSettings requested, CancellationToken ct = default);
    Task<ConfigChange> SetDeviceNameAsync(string name, CancellationToken ct = default);
    Task<ConfigChange> SyncTimeNowAsync(CancellationToken ct = default);
}
```

`ConfigChange` mirrors `RecordingOptionChange` exactly — `Before`, `After`, `Changed`,
`Note`, `Rejected` — for the same reason and with the same rule: **write only when the
requested state differs, then read back**; a recorder that accepts a field it does not honour
is the failure mode worth catching, and only the read-back catches it.

### 1.5 Read-modify-write is mandatory, and enforced

The M-series NTP document carries `portType`, `customPortNo` and `hostNameExampleList` that
the I-series document does not. A hand-built minimal PUT drops them on every save.

So: a writer **holds the raw document from its last read and refuses to write without one.**
This is the precedent `NxWitnessClient` already set for a different reason — it keeps the
camera list per instance, refuses a write when the list changed between read and write, and
its URL builders throw until some call has read the list. Same three behaviours here:

- `SetXAsync` throws `NvrException("read the configuration before writing it")` when no read
  has happened on this client instance.
- The write is the stored document with the requested fields replaced, serialized back whole.
- Before writing, the document is re-read and compared; if the device's copy changed since
  the read, the write is refused with the diff named. Schedules on Site D were observed
  moving under an operator mid-batch — config edited from a vendor console is the same
  hazard.

The GUI reads and writes on different client instances (as it does for storage), so the tab
holds the client that did the read for the life of the tab.

## 2. Per-vendor mapping

Everything here is from the discovery pass. Anything not listed does not exist.

### 2.1 Hikvision — `HikvisionClient.DeviceConfig.cs`

| part | endpoint | notes |
|---|---|---|
| clock | `GET /ISAPI/System/time` | `timeMode`, `localTime`, `timeZone`, `timeType` |
| — cheap sweep | `GET /ISAPI/System/time/localTime` | **plain-text scalar, not XML** |
| NTP | `/ISAPI/System/time/ntpServers` (+ `/1`) | interval 1–10080 **minutes** |
| ports | `/ISAPI/Security/adminAccesses` | all seven, one document |
| port ranges | `/ISAPI/Security/adminAccesses/capabilities` | `def=` **and** `default=` |
| interface | `/ISAPI/System/Network/interfaces/{id}/ipAddress` | values |
| field ranges | `/ISAPI/System/Network/interfaces/capabilities` | **list level only** |
| device name | `/ISAPI/System/deviceInfo` | `capabilities` declares nothing (§5) |
| notes | `Network/capabilities`, `/DDNS`, `/PPPoE`, `/ipFilter`, `/UPnP/ports`, `/System/status` | |

`GetClockAsync` uses the plain-text scalar; `GetConfigurationAsync` uses the XML. The
scalar path needs its own parse — an XML-only reader crashes on it.

`ServiceKind` mapping: `HTTP`→Http, `RTSP`→Rtsp, `HTTPS`→Https, **`DEV_MANAGE`→Sdk**,
`SDK_OVER_TLS`→SdkOverTls, `WebSocket`→WebSocket, `IOT`→Iot, anything new→Other with the
protocol string preserved. `DEV_MANAGE` is the port `VendorPorts.Sdk` defaults to 8000 and
the one Hikvision live view actually rides (`docs/hikvision-sdk-live.md`) — mapping it is
what makes the audit in §3 possible.

Endpoints that **do not exist** and must not be probed at runtime (403 `notSupport`):
`System/Network` itself, `Network/NAT`, `/serverPort`, `/http`, `/https`, `/RTSP`,
`Streaming/capabilities`, `time/DSTMode`, `interfaces/1/capabilities`,
`interfaces/1/ipAddress/capabilities`, `interfaces/1/ipAddress/dns`.

### 2.2 Dahua — `DahuaClient.DeviceConfig.cs`

All `GET /cgi-bin/configManager.cgi?action=getConfig&name=<X>`, parsed by the flat
`table.X.Y=Z` reader the storage path already has.

| part | name | notes |
|---|---|---|
| clock | `global.cgi?action=getCurrentTime` | bare `result=…`, **no offset** |
| zone | `NTP` → `TimeZone` (index), `TimeZoneDesc` | **not** in `Locales` |
| DST | `Locales` → `DSTEnable`, `DSTStart/End.{Year,Month,Week,Day,Hour}` | absolute dates, not a rule |
| NTP | `NTP` → `Address`, `Enable`, `Port`, `ServerList[0..2]`, `UpdatePeriod` | |
| interfaces | `Network` → `DefaultInterface` + a block per `eth*`/`bond*` | six on the 608H |
| link | `netApp.cgi?action=getInterfaces` | speed, connected state |
| ports | `RTSP` → `Enable`, `Port`, `RTP.StartPort/EndPort` | **RTSP only** |
| device name | `General` → `MachineName` | |
| notes | `DDNS`, `UPnP` (+`MapTable[n]`), `Multicast`, `T2UServer`, `General` lockout policy | |

Three consequences, all load-bearing:

- **`Scope.Ports` is true but the list holds one entry.** The web port is not reachable by
  name: `HTTP`, `HTTPS`, `HTTPD`, `Telnet`, `ClientPort`, `NetPort` all answer
  `403 Authority:check failure.` — the same body a genuine permission denial returns. **A
  wrong config name and a real denial are indistinguishable on Dahua**, so the client asks
  only for names the discovery pass observed answering, and **never guesses a name at
  runtime**. A speculative probe would be indistinguishable from a permissions problem in a
  support call, and each 403 is a failed request against a box that locks out after five.
- **No ranges.** `getConfigCaps&name=Network` returns the plain config — it ignores the caps
  verb for this name, a wider failure than the channel-parameter one in
  `docs/dahua-storage.md`. Every Dahua bound is `ServicePortRange.Fallback`, labelled in the
  UI as our constant rather than the device's declaration.
- **The lockout policy is readable** (`LockLoginEnable`, `LockLoginTimes=5`,
  `LoginFailLockTime=1800`) and becomes a `ConfigNote`. That 1800 s is the number the probe
  rules already respect from folklore; showing it makes the rule self-evident.

### 2.3 Nx / DW Spectrum — `NxWitnessClient.DeviceConfig.cs`

`Scope.ClockOnly`. `GET /rest/v3/system/info` → `synchronizedTimeMs`, a UTC instant, rendered
in the operator's zone the way every other Nx time already is. `DeclaredOffset` null,
`VendorZoneLabel` null, `DstEnabled` null.

`Notes` carry what is real and readable: `timeSynchronizationEnabled`, `primaryTimeServer`
(the clock-master **server GUID**, all-zero = follow the internet), `syncTimeEpsilon`,
`maxDifferenceBetweenSynchronizedAndLocalTimeMs`, the `version`, and the server's observed
`endpoints` list from `/rest/v3/servers` — four `ip:7001` addresses on Site D, the closest
thing Nx has to a LAN address and **observed, not configured**, which the note says.

`/rest/v3/system/time` and `/rest/v3/servers/this/time` are 404. Do not add them.

There is no tier 2 or tier 3 for Nx. `IDeviceConfigWriter` is not implemented; a distributed
clock master is not an NTP server and setting a zone means logging into Windows.

## 3. The fleet clock audit — `ConfigAudit` in Core

Pure aggregation, no I/O, like `FleetMatrix` and `AccessRoster`, and for the same reason:
identical from the GUI, the CLI, or a fixture.

```csharp
public sealed record ClockAuditRow
{
    public required string DeviceName { get; init; }
    public DeviceClock? Clock { get; init; }
    public ClockDrift? Drift { get; init; }

    /// <summary>Null on success; why the recorder could not be read otherwise.</summary>
    public string? Error { get; init; }

    /// <summary>What the operator should do about this row, or empty.</summary>
    public required string Verdict { get; init; }
}
```

The carried-failure rule from `DeviceUsersResult` applies verbatim: a recorder that could not
be read is a row with an `Error`, never a row omitted and never a row reading "fine". A
partial audit says it is partial.

`Verdict` is computed, and the discovery pass supplies its cases:

- drift beyond `Tolerance` → **`"58 min behind"`**, and where the vendor exposes a cause,
  the cause: on Dahua, `NtpEnabled` true with `DstEnabled` false and a zone label naming a
  DST zone is the exact shape of the fault that had a 128-channel recorder an hour out, and
  saying *"NTP is syncing; DST is disabled"* is the difference between a verdict and a
  number.
- drift within tolerance but `DeclaredOffsetDiffers` → `"clock right, reports the wrong
  offset"`. A report, not a fault.
- `NtpEnabled` false → `"no time source"`, whatever the drift is today.
- Nx → the drift, and no NTP verdict, because there is no NTP to have an opinion about.

`ClockDrift` and `ConfigAudit` are the two pieces that must be **pure and tested first**, in
that order, before any vendor client is written. They are where the value is.

## 4. CLI — `src/DVRTool.Cli/ConfigCommands.cs`

A command group, wired like `storage` and `recording` in `Program.cs`.

```
dvrtool config show      [connection options]
dvrtool config clock     [connection options]
dvrtool config audit     [--device <name>]... | --all-saved
dvrtool config set       --ntp-server <host> [--ntp-port <n>] [--ntp-interval <min>]
                         [--timezone <vendor value>] [--sync-now] [--name <text>]
                         [--force] [connection options]          # tier 2
```

House gates, unchanged: dry run is the default, an explicit `--dry-run` beats `--force`,
every applied write is read back, and what gets printed is what the recorder now holds.

`config audit` is the tier-1 headline and the one command that reads **more than one device**:
it walks saved devices, reads only the clock, and prints a row each. It is also the only
config verb that runs against a fleet, which keeps the blast radius of everything else at one
recorder.

`--timezone` takes the **vendor's own value** (a Hikvision zone string, a Dahua index) and
says so in help, because there is no IANA mapping and inventing one would be a translation
layer that silently mis-sets clocks. `config show` prints the current value in copyable form
so the value for one recorder can be moved to another of the same vendor.

## 5. GUI — a Config tab

`MainWindow.Config.cs` plus a `TabItem` in `MainWindow.xaml`, following `MainWindow.Storage.cs`
(~790 lines) as the model: a device picker at the top, group boxes below, a Refresh, and — in
tier 2 — an Apply that is the deliberate exception to "GUI writes stay in the CLI", on the
same grounds the Storage tab's Apply earned it: **reversible from the same tab**. Setting an
NTP server or a time zone is; changing the LAN address is not, which is why tier 3 does not
get an Apply button (§7).

Groups, top to bottom:

1. **Clock** — the wall clock, the drift against this workstation with its latency error bar,
   the vendor zone label verbatim, the DST switch where it exists, and the verdict sentence
   from `ConfigAudit`. Where `DeclaredOffsetDiffers`, the offset is shown **struck through**
   with a tooltip saying the device states it and DVRTool ignores it, so an operator
   comparing against the recorder's own web UI sees why the two disagree.
2. **Fleet clocks** — the audit across saved devices, one row each, the tab's reason to exist.
   Reachable without picking a device.
3. **NTP** — servers, port, interval; `n/a` for Nx with the reason, not a blank.
4. **Ports** — protocol, port, enabled, declared range. The row for a port DVRTool also
   stores for this device (`HttpPort`, `RtspPort`, `SdkPort`) shows a **match indicator
   against the saved record** — the cheap consistency check `adminAccesses` makes possible,
   and one that catches a device saved with a stale forward.
5. **Network** — the default interface expanded, the rest collapsed; addressing type, address,
   mask, gateway, DNS, MTU, MAC, link state.
6. **Notes** — DDNS, UPnP map, PPPoE, lockout policy, Nx's clock-master settings.

Two rendering rules, both from §1.3:

- **Out of scope renders `n/a` with the reason on hover; a failed read renders `?`.** Never
  the same glyph, never a blank. The Users tab's precedent is explicit that an unreadable
  device excluded from status must not read as "missing", and a Config tab that shows Nx an
  empty Network grid is making exactly that error about a third of the fleet.
- A range that came from the device is shown as the device's; a range that came from
  `ServicePortRange.Fallback` says so. An operator refused a port by our constant deserves
  to know it was ours.

Identity: the tab verifies the device before reading, like the Access panels — `host:port`
identifies a socket, not a recorder, and a config view is exactly where reading the wrong box
misleads worst.

## 6. Wiring

**6.1 Vendor plumbing.** `Vendor`/`VendorPorts` need nothing new. `ConnectivityProbe` needs
nothing new. Each client gains one partial-class file; the front ends type-test for
`IDeviceConfigClient` the way they do for `IStorageClient`.

**6.2 Identity.** Reads go through `DeviceIdentityGuard` before the first request.
`DeviceIdentityException` stays outside `NvrException` so the audit's per-device
"carry on with the rest" handler does **not** swallow it — a serial mismatch during a fleet
sweep is a finding, not a skipped row.

**6.3 `ExpectedOffsetMinutes` on `SavedDevice`** — optional, null by default. Only for a
fleet that genuinely spans zones: when set, drift is measured against the workstation's clock
shifted by it. Null means "same wall clock as me", which is right for every site in the
current fleet and is the reason this is a nullable int and not a zone picker.

## 7. Tier 3, if it ever happens

Not in the tier-1 or tier-2 build. Specified so the shape is on record.

A network write cannot be verified on the socket that issued it — the device answers at the
new address. `adminAccesses` being a single document makes a port change *easier to issue* and
no easier to recover from. The protocol:

1. Confirm, in a dialog that names the current and new address and says the connection will
   drop and may need physical access.
2. Write. **Expect the request to fail or hang.** A timeout is the expected outcome, not an
   error.
3. Re-dial at the new `host:port`.
4. Confirm the **serial** against the pin. This is what distinguishes "same recorder, new
   address" from "something else took the freed IP" — precisely what `DeviceIdentityGuard`
   was built for.
5. Only then re-key all four `host:port`-keyed stores: `identities.json`, the cert pins
   (`pins.json`), `channel-pins.json`, and the saved device record. Miss one and the device
   is orphaned from its own pins.
6. On failure to re-dial, keep the old record, say plainly that the device may be at the new
   address and unreachable, and do not re-key anything.

**Refuse outright when the connection is via a port forward or a relay** — a subnet or
gateway change made through a forward is unrecoverable by definition, because the forward
still points at the old address. That is a truck roll, and the tool should decline rather
than offer it. Detection is imperfect (a non-RFC1918 host, a mismatch between the connected
host and the device's own reported `IpAddress`, an Nx relay host) and should err toward
refusing.

CLI only, `--force` plus a typed confirmation. No Apply button.

## 8. Tests

Pure Core first, and they are most of the value:

- `ClockDriftTests` — drift sign and magnitude; the 58-minute case; latency as the error bar;
  tolerance boundary; `DeclaredOffsetDiffers` true for the observed
  `07:24:29-05:00`-in-`-04:00` case **with drift zero**, since that pairing is the whole
  reason the two fields are separate.
- `ConfigAuditTests` — verdict per case; the carried-failure rule (an unreadable device is a
  row with an error and the audit reports partial); NTP-on-with-DST-off producing the cause
  sentence.
- `HikvisionDeviceConfigTests` — over `MockHttpHandler`, with fixtures **transcribed from the
  discovery captures** (redacted: no real addresses, serials or MACs): `adminAccesses` with
  both `def=` and `default=` in one document asserting all seven defaults survive; the
  plain-text `localTime` scalar; capabilities-at-list-level; `DEV_MANAGE`→`Sdk`; the M-series
  NTP document round-tripping `portType`/`customPortNo` through a write.
- `DahuaDeviceConfigTests` — `TimeZone`/`TimeZoneDesc` from `NTP` not `Locales`; DST absolute
  dates; six interfaces with `DefaultInterface` selecting one; the offsetless
  `getCurrentTime`; a 403 `Authority:check failure.` surfacing as a note and **not** as a
  retry or a name guess.
- `NxDeviceConfigTests` — `ClockOnly` scope; `synchronizedTimeMs` → clock; the clock-master
  GUID as a note; `Scope.Network` false rendering as out-of-scope rather than an error.

A write test asserts the refusal path: `SetNtpAsync` before any read throws, and a write
against a document that changed underneath is refused with the diff named.

## 9. Sequence

1. `DeviceClock`, `ClockDrift`, `ConfigAudit` + their tests. No vendor code. **The value is
   already here** — the audit runs off any clock source, including a fixture.
2. Hikvision read (`GetClockAsync` first, then the full document) — 14 of 17 recorders.
3. `dvrtool config audit` across saved devices. **Ship. Fix Site B's hour.**
4. `dvrtool config show`, Dahua read, Nx read.
5. GUI Config tab.
6. Tier 2 writes: NTP, zone, sync-now, name. CLI then Apply.
7. Tier 3: only on a decision that it is wanted, with §7's protocol intact.

Steps 1–3 are the smallest thing that repays the build, and step 3 is where a recorder that
has been stamping the wrong hour onto its footage gets caught.

## 10. Decisions taken

Recorded so they are not re-litigated:

- **Drift is measured against the workstation's wall clock, not against instants.** The
  device's declared offset is the field that lies; a measurement that depends on it inherits
  the lie. Cost: a genuinely-other-zone recorder reads as drifted, handled by
  `ExpectedOffsetMinutes` and by naming the zone label, never by inference.
- **Vendor zone values are passed through verbatim, never mapped to IANA.** There is no
  reliable mapping from `CST+5:00:00DST01:00:00,M3.2.0/…` or from Dahua index `25`, and a
  wrong mapping mis-sets a clock silently — the exact failure the feature exists to catch.
- **Writes are read-modify-write, enforced by refusing to write without a read.**
- **Out of scope and unreadable are different states.** One glyph for both would tell the
  operator that a third of the fleet is broken.
- **Dahua config names are never guessed at runtime.** A wrong name is indistinguishable
  from a permission denial, and the box locks out after five bad requests.
- **`ParseIsapiTime` keeps the wall clock and drops the offset, and must not be "fixed".**
  The discovery pass turned that comment into a measurement.
- **Nx gets a clock and notes, and no writer.**
- **Tier 3 has no Apply button and refuses through a forward or relay.**
