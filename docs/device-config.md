# Device configuration and the fleet clock audit — what shipped

Built 2026-09-09 from [device-config-spec.md](device-config-spec.md), which was itself built
on the observed endpoint set in [device-config-discovery.md](device-config-discovery.md). Read
those two first for *why*; this document is *what exists and what it was verified against*.

Tiers 1 and 2 shipped: reading a recorder's own configuration, the fleet clock audit, and the
reversible writes (NTP, time zone, DST, device name, set-clock-from-this-PC). **Tier 3 —
changing a LAN address or a service port — is deliberately absent**, and the spec's §7 records
the shape it would take if it is ever wanted. A network write cannot be verified on the socket
that issued it, and one made through a port forward is unrecoverable by definition.

## The product is the clock audit

Footage search, timeline geometry (`TimelineWindow`, `PlaybackClock`) and export naming
(`ExportNaming`) all run on NVR-local wall clock. A recorder whose clock is out therefore
stamps the wrong time onto everything it writes, and until today nothing in DVRTool would have
said so.

The first sweep of the saved fleet — 21 recorders, **2026-09-09**, no failures — found **four**:

| recorder | what it read | verdict |
|---|---|---|
| Site B (Dahua, 128-channel) | 1 h behind, NTP syncing, `DSTEnable=false`, zone `25 (Easterntime)` | `1 h behind; NTP is syncing; DST is disabled` |
| another Dahua NVR | 35 min ahead, NTP off | `35 min ahead; no time source` |
| a Hikvision NVR | clock right, NTP off | `clock right, reports the wrong offset; no time source` |
| a Hikvision NVR | 2 min ahead | `2 min ahead` |

Fourteen Hikvision recorders read *clock right, reports the wrong offset*: their wall clock is
correct and their declared offset is not (see below). That is a report, not a fault.

## The one rule everything else follows

**Drift is a difference of wall-clock digits, never of instants.** Hikvision firmware reports
`localTime` as e.g. `2026-09-09T07:24:29-05:00` while actually standing in −04:00 — with a DST
rule in its own `timeZone` string that was active that day — so `DateTimeOffset.Parse` of that
value yields an instant an hour wrong. `ClockDrift.Measure` compares the digits the recorder
shows against the digits on the workstation, which needs no zone database on either side and
cannot be wrong in a new way per firmware. `HikvisionClient.ParseIsapiTime` already threw the
offset away; that comment is now a measurement, and **must not be "fixed"**.

The cost of the choice: a recorder deliberately set to another zone reads as drifted by the
zone difference. That is handled by naming it — `SavedDevice.ExpectedOffsetMinutes`, an
optional field on the saved record (Add/Edit device → **Clock offset**), which shifts the
reference rather than the reading — and never by inference. The verdict says so out loud when
it is set.

`ClockDrift.Tolerance` is one minute: below that, NTP jitter, the read's own round trip and the
recorder's second-granularity reporting are indistinguishable from drift; above it an export's
filename minute is wrong. `Measure` takes **both ends of the round trip**, so `ReadLatency` is
real and the reference is its midpoint — a relay read through DW Cloud takes about a second, and
a one-second error bar on a one-minute tolerance has to be visible rather than assumed away.

## Where the code is

| piece | file | notes |
|---|---|---|
| model | `DVRTool.Core/DeviceConfig.cs` | `DeviceClock`, `ClockDrift`, `TimeSourceStatus`, `ServicePort`, `NetworkInterfaceConfig`, `ConfigScope`, `DeviceConfiguration`, `IDeviceConfigClient` / `IDeviceConfigWriter` |
| audit | `DVRTool.Core/ConfigAudit.cs` | `ClockAuditRow`, `ConfigAudit` (pure), `ClockSweep` (the two-or-three-read sweep both front ends use) |
| saved records | `DVRTool.Core/SavedDevices.cs` | `SavedDevice` / `DeviceStore`, **moved out of the App project** so the CLI can sweep the same fleet the GUI stores |
| Hikvision | `HikvisionClient.DeviceConfig.cs` | full read + writes |
| Dahua | `DahuaClient.DeviceConfig.cs` | full read + writes |
| Nx / DW | `NxWitnessClient.DeviceConfig.cs` | clock and notes only; **no writer** |
| CLI | `DVRTool.Cli/ConfigCommands.cs` | `config show \| clock \| audit \| set` |
| GUI | `DVRTool.App/MainWindow.Config.cs` + the Config tab | fleet panel, one-device view, the writes |

`SavedDevice` moving into Core is the one structural change beyond the spec: the fleet sweep is
a CLI verb (`config audit --all-saved`) and the records live in `%APPDATA%\DVRTool\devices.json`,
so both front ends now read one definition of a saved device instead of two. `CreateClient()`
stayed behind in the App as `SavedDeviceClients` (Core knows no vendors), the CLI got
`VendorClients`, and Core picked up the `System.Security.Cryptography.ProtectedData` package so
the DPAPI-protected password still decodes from a plain `net10.0` build.

## Out of scope is not a failed read

The distinction the whole feature turns on. `ConfigScope` says which parts a recorder *has*:

- `ConfigScope.Appliance` — Hikvision, Dahua: all of it.
- `ConfigScope.ClockOnly` — Nx / DW Spectrum: a clock, and a reason for everything else. Its
  time keys configure a *distributed clock* (`primaryTimeServer` names which server in the VMS
  is the clock master by GUID; all-zero = follow the internet), not an NTP client, and the zone
  and address belong to Windows on the box.

Both front ends render **`n/a` plus the reason for out of scope, and `?` only for a read that
failed** — never the same glyph, never a blank. This is the Users tab's precedent (an
unreadable device shows "?" and is excluded from row status, never read as "missing") applied
to a tab that would otherwise show an Nx server an empty Network grid and imply a third of the
fleet is broken. `TimeSourceStatus.NtpEnabled` is `null` on Nx for the same reason: "no NTP
client" is not "NTP off", and the audit must not accuse it of having no time source.

A per-part read failure inside one recorder lands in `DeviceConfiguration.Failures` rather than
throwing: a recorder that answers its clock and refuses its port list is worth a partial answer,
because the clock is the part that matters.

## Per-vendor, and the traps

### Hikvision

- **Ports are one document**: `GET /ISAPI/Security/adminAccesses`, all seven protocols, with
  `/capabilities` declaring each range. `DEV_MANAGE` is the SDK port DVRTool records per device
  (8000), which is what makes the GUI's *Saved record* column — "matches (8000)" versus "record
  says 8000" — cost no extra request.
- **The default attribute is spelled two ways in one document**: `def=` on ids 1/2/5/6,
  `default=` on 4/7, on both firmware families. A parser reading one spelling silently loses the
  SDK ports' defaults. `ReadRange` reads both, and a test asserts all seven survive.
- **`enabled` is per port and HTTPS ships false.** The switch renders beside the number.
- **`/ISAPI/System/time/localTime` is a plain-text scalar**, which is the cheap read
  `GetClockAsync` uses for the fleet sweep and a shape that crashes an XML-only reader. Firmware
  that answers XML there is still served.
- **Capabilities live at a different depth from values**: network ranges and `opt=` lists exist
  only at `/ISAPI/System/Network/interfaces/capabilities`. The per-interface `/capabilities`
  sub-nodes, `/ISAPI/System/Network` itself, `time/DSTMode`, `/serverPort`, `/http`, `/https`,
  `/RTSP` and `Streaming/capabilities` are all 403 `notSupport` and **are not probed**.
- DST has no switch here: it is inside the `timeZone` string, so `DeviceClock.DstEnabled` is
  null and a `--dst` write is refused with that explanation rather than silently dropped.

### Dahua

- **Only RTSP's port is readable.** `HTTP`, `HTTPS`, `HTTPD`, `Telnet`, `ClientPort` and
  `NetPort` all answer `403 Authority:check failure.` — the same body a genuine permission
  denial returns, so **a wrong config name and a real denial are indistinguishable**. The client
  asks only for names the discovery pass observed answering and **never guesses one at
  runtime**; the box locks out after five failed requests. `Scope.Ports` is true and the list
  holds one entry, which is a fact about the vendor rather than a failed read.
- **No declared ranges anywhere** (`getConfigCaps&name=Network` returns the plain config), so
  every bound is `ServicePortRange.Fallback` and both front ends label it as ours.
- **The zone is in `NTP`** (`TimeZone=25`, `TimeZoneDesc=Easterntime`), not in `Locales`, which
  holds only `DSTEnable` and the DST dates. That pairing — NTP syncing, DST off, a DST zone — is
  the fault that had a 128-channel recorder an hour out, and `ConfigAudit` names it as the cause
  when the drift is within tolerance of exactly an hour. A recorder *two* hours out does not get
  that explanation.
- The DST window is reported as the device's own fields verbatim (`Year=2026 Month=3 Week=2
  Day=0 Hour=2`): it is stated as absolute dates rather than a recurring rule, and the live
  608H fills `Week` and `Day` in a combination its documentation does not explain.
- **`netApp.cgi` reports 65535 Mbps for an unconfigured bond.** That is a sentinel, not a
  reading; it is dropped rather than printed, or the whole link-speed column becomes untrustworthy.
- The **lockout policy is readable** (`LockLoginEnable`, `LockLoginTimes=5`,
  `LoginFailLockTime=1800`) and becomes a note — the 1800 s the probe rules already respect from
  folklore, now visible.

### Nx / DW Spectrum

`/rest/v3/system/info` → `synchronizedTimeMs`, rendered in the operator's zone like every other
Nx time. Notes carry the distributed-clock settings, the version, and the server's **observed**
`endpoints` — labelled "Observed addresses", because presenting them as configuration would
invite an operator to try to change them here. `/rest/v3/system/time` and
`/rest/v3/servers/this/time` are 404 and are not asked for. There is no writer.

## Writes: read-modify-write, enforced

The M-series NTP document carries `portType`, `customPortNo` and `hostNameExampleList` that the
I-series one does not, so a hand-built minimal PUT drops them on every save. Therefore:

1. A writer **refuses to write before it has read** (`NvrException`: "read the configuration
   before writing it").
2. The write is the device's own document with the requested fields replaced, serialized whole.
3. The document is **re-read immediately before the write** and the write is refused if the
   device's copy moved in between — somebody editing the same recorder from a vendor console is
   a real hazard (schedules were seen changing under an operator mid-batch on 2026-09-08).
   Whitespace-only reformatting is not a change.
4. The write is **read back**, and what gets reported is what the recorder now holds — a
   recorder that accepts a field it does not honour is the failure mode worth catching, and only
   the read-back catches it (`ConfigChange.Rejected`).

Two refusals are deliberate and worded rather than silent:

- **`--sync-now` on a recorder that syncs from NTP.** A hand-set clock there is overwritten at
  the next sync and hides the real cause; the Dahua message names the zone and DST state to fix
  instead.
- **A Dahua `--timezone` given as text.** Its zone is a vendor index; a text zone is refused
  with the device's current value quoted. There is no IANA mapping on either vendor and
  inventing one would mis-set clocks silently — the exact fault the feature exists to catch — so
  `config show` prints the current value in copyable form and the value moves between units of
  the same vendor.

## Front ends

```
dvrtool config show   [connection options | --device <saved name>]
dvrtool config clock  [...]
dvrtool config audit  [--all-saved | --device <name> ...]
dvrtool config set    [--ntp-server <host>] [--ntp-port <n>] [--ntp-interval <min>]
                      [--ntp on|off] [--timezone <vendor value>] [--dst on|off]
                      [--sync-now] [--name <text>] [--force]
```

`--device <saved name>` works from the GUI's saved record — its DPAPI-protected credentials and
its expected serial — so a field verb needs no retyped password; `audit` takes it repeatedly.
House gates unchanged: dry run is the default, an explicit `--dry-run` beats `--force`, every
applied write is read back.

The GUI **Config** tab has the fleet-clocks panel at the top (reachable without picking a
device), the one-recorder clock block, the ports grid with the saved-record comparison, network
and notes, and the write row at the bottom. Its Apply is the same deliberate exception the
Storage tab's earned — **reversible from the same tab** — and it writes only the fields the
operator actually changed. Identity is verified before every read and before the fleet sweep's
rows: `host:port` names a socket, not a recorder, and a config view is where reading the wrong
box misleads worst.

## Verified live, 2026-09-09

- **Fleet audit, 21 saved recorders, 0 failures** — CLI and the GUI tab, same numbers. Four
  clock problems found (table above).
- **Hikvision full read** (DS-7716NI-I4/16P(B) V4.61.030): clock, zone, NTP, all seven ports with
  their declared ranges, the LAN interface with its MTU bounds, DDNS/PPPoE/uptime notes.
- **Dahua full read** (DH-NVR608H-128-4KS3/I): clock, zone index + label, DST off, NTP, the RTSP
  port with its RTP range, **six** interfaces with the named default, hostname, DST window.
- **Hikvision write, fired live on the lab recorder**: `config set --ntp-interval 30 --force`
  → read-back "every 30 min", then restored to 60 the same way. The PUT is
  `/ISAPI/System/time/ntpServers/1` as the device's own document.
- Dry-run plans on both vendors, including the Dahua DST fix that is still pending an
  operator's go-ahead.

**Not yet fired live:** any Dahua write (`setConfig` / `setCurrentTime`), the Hikvision zone,
name and sync-now writes, and the GUI Apply button (its code path is the same `IDeviceConfigWriter`
the CLI exercised). Nx has no write path at all.

## Still open

- The Dahua DST fix on the recorder that is an hour out is a **customer-device change** and waits
  on a go-ahead; the dry run is ready.
- Two recorders run with **no time source at all** (NTP off). Setting one is one
  `config set --ntp-server … --ntp on --force`, also a customer-device change.
- `config set` cannot add or remove NTP servers, only rewrite the first; Dahua's `ServerList`
  entries beyond it are read-only here.
- Tier 3 (address and port writes) is specified and unbuilt, on purpose.
