# Device configuration: the endpoint set, observed

Discovery pass run **2026-09-09**, read-only, against three live recorders:

| target | what it is | reached over |
|---|---|---|
| the lab recorder | Hikvision DS-7716NI-I4/16P(B), V4.61.030 (build 240123) | LAN, HTTP 80 |
| Site C | Hikvision DS-9632NI-M8, M-series | forwarded HTTP |
| Site B | Dahua DH-NVR608H-128-4KS3/I, 4.000.0000000.6.R build 2024-07-21 | forwarded HTTP |
| Site D | Nx / DW Spectrum 6.1.1.42624 | DW Cloud relay, HTTPS 443 |

Every request below was a GET. **No write of any kind was issued.** The estimate in the
feasibility pass rested on recall; this document replaces the recalled paths with observed
responses, and the one path that pass flagged as unverified — Hikvision's port
configuration — is now pinned.

## Hikvision: the port path is `/ISAPI/Security/adminAccesses`

Not under `/ISAPI/System/Network/`. One node carries **all seven** service ports, and its
`/capabilities` sibling declares the legal range of each:

| id | protocol | port | writable range (from `/capabilities`) |
|---|---|---|---|
| 1 | HTTP | 80 | 1–65535 |
| 2 | RTSP | 554 | 1024–65535, def 554 |
| 3 | HTTPS | 443 (disabled) | 1–65535, plus `redirectToHttps`, `TLS1_1Enable`, `TLS1_2Enable` |
| 4 | `DEV_MANAGE` | 8000 | 2000–65535, default 8000 |
| 5 | WebSocket | 7681 | 7681–7681 (fixed) |
| 6 | IOT | 30999 | 1024–65535, def 30999 |
| 7 | `SDK_OVER_TLS` | 8443 | 2000–65535, default 8443, plus `streamOverTls` |

`DEV_MANAGE` is the vendor SDK port — the one `VendorPorts.Sdk` defaults to 8000 and the one
Hikvision live view actually rides (`docs/hikvision-sdk-live.md`). So all three ports DVRTool
records per device are readable from a **single** GET, which makes "the record says 8000, the
device says 8000" a cheap consistency check for `FleetAudit`.

Two traps in that table:

- **The min/max attribute is spelled two ways in one document.** ids 1, 2, 5, 6 use `def=`;
  ids 4 and 7 use `default=`. A parser that reads only one spelling silently loses the
  default on the SDK ports. Both spellings appeared identically on both firmwares.
- **`enabled` is per protocol and HTTPS ships false.** A config tab that renders the port
  without the switch invites "I set 443 and nothing happened".

Identical path set and identical protocol list on the M-series, so this is not a
firmware-of-the-week finding.

### Time and NTP

Confirmed as recalled:

- `GET /ISAPI/System/time` → `timeMode` (`opt="NTP,manual,platform"`), `localTime`,
  `timeZone`, `timeType` (`opt="local,UTC"`).
- `GET /ISAPI/System/time/timeZone` and `/localTime` are **plain-text scalars**, not XML —
  a shortcut worth having for a fleet sweep, and a shape that will crash an
  XML-only response reader.
- `GET /ISAPI/System/time/ntpServers` (list) and `/ntpServers/1` (single). Capabilities give
  `portNo` 1–65535 and `synchronizeInterval` **1–10080 minutes**.
- `/ISAPI/System/time/DSTMode` does **not** exist (403 `notSupport`) even though
  `/ISAPI/System/capabilities` says `isSupportDst=true`. DST is expressed inside the
  `timeZone` string, not as its own node.

**The M-series NTP document carries fields the I-series one does not**: `portType`,
`customPortNo`, and a `hostNameExampleList` of suggested servers. Consequence for the write
tier: a PUT must be **the read document with fields replaced**, never a hand-built minimal
one, or the M-series loses `portType`/`customPortNo` on every save. Same rule the recording
options work landed on for a different reason.

### Network interface

`GET /ISAPI/System/Network/interfaces` (list), `/interfaces/1` (one), and
`/interfaces/1/ipAddress` (just the addressing block). All three answer; the sub-nodes
`/interfaces/1/capabilities`, `/interfaces/1/ipAddress/capabilities` and
`/interfaces/1/ipAddress/dns` do **not** (403 `notSupport`).

**Capabilities live only at the list level**: `/ISAPI/System/Network/interfaces/capabilities`
is the one document that carries the `opt=` attributes — `addressingType opt="apipa,dynamic,static"`,
`ipVersion opt="v4,v6,dual"`, `DNSEnable opt="true,false"`, `Link/MTU min=500 max=1500`,
`speed opt="10,100,1000"`, `duplex opt="full,half"` — and it also folds in the `Discovery`
(UPnP, Zeroconf) and `Link` blocks that are separate resources on read. So the field/range
source and the value source are **different URLs at different depths**; a config tab must
read the list capabilities once and the per-interface node per device.

`/ISAPI/System/Network` itself is 403 — there is no index node, only named children.

### Also present, also read-only-verified

`/ISAPI/System/Network/capabilities` (the `isSupportX` roster — PPPoE, NTP, DDNS, HTTPS,
802.1x, SNMP, IP filter, UPnP, WebSocket, PoE configuration), `/DDNS`, `/PPPoE`, `/ipFilter`,
`/UPnP`, `/UPnP/ports`, `/ISAPI/Security/capabilities` (`supportUserNums` 32),
`/ISAPI/System/status` (uptime, CPU, memory), `/ISAPI/System/deviceInfo`.

`/ISAPI/System/Network/NAT`, `/serverPort`, `/http`, `/https`, `/RTSP` and
`/ISAPI/Streaming/capabilities` are all 403 `notSupport` — none of them exist. Port
configuration is `adminAccesses` and nothing else.

**`deviceInfo/capabilities` is a lie of a name**: it returns the same document as
`deviceInfo` with no `opt=`/`min=` on anything except `telecontrolID`. So a device-name
write has **no self-declared length or character rule** — that one has to be discovered by
attempting it, or bounded conservatively.

## Dahua: time zone is in `NTP`, not `Locales`, and there is no port config

All by `GET /cgi-bin/configManager.cgi?action=getConfig&name=<X>`, answering the flat
`table.X.Y=Z` text the storage work already parses.

- **`name=NTP`** — `Address`, `Enable`, `Port`, a three-entry `ServerList[n]`
  (`.Address`/`.Enable`/`.Port`), `UpdatePeriod` (minutes), and **`TimeZone=25`** with
  **`TimeZoneDesc=Easterntime`**. The zone is a vendor index with a text label beside it;
  there is no IANA name and no offset in the document.
- **`name=Locales`** — DST only, and expressed as absolute dates with a `Year`
  (`DSTStart.Year=2026`, `Month`, `Week`, `Day`, `Hour`) plus `DSTEnable`. Not a recurring
  rule. A recorder left alone past New Year has stale DST dates.
- **`name=Network`** — `DefaultInterface`, `Domain`, `Hostname`, then a block per interface
  (`eth0`, `eth1`, `bond0`–`bond3`) with `IPAddress`, `SubnetMask`, `DefaultGateway`,
  `DhcpEnable`, `DnsAutoGet`, `DnsServers[0..1]`, `MTU`, `PhysicalAddress`, `Type`. The
  128-channel chassis lists **six** interfaces, four of them unconfigured bonds — so
  "the LAN address" is `Network.<DefaultInterface>.IPAddress`, never a fixed key.
- **`name=RTSP`** — `Enable`, `Port`, and the RTP range (`RTP.StartPort` 20000,
  `RTP.EndPort` 40000).
- **`name=General`** — `MachineName` (the device name), plus the lockout policy in plain
  sight: `LockLoginEnable=true`, `LockLoginTimes=5`, `LoginFailLockTime=1800`. That is the
  1800 s lockout the probe rules exist for, readable rather than folklore.
- Also read: `name=DDNS`, `name=UPnP` (with its `MapTable[n]` of inner/outer ports),
  `name=Multicast`, `name=T2UServer` (P2P cloud), `/cgi-bin/netApp.cgi?action=getInterfaces`
  (link state and speed per NIC), `magicBox.cgi` `getSystemInfo`/`getDeviceType`/
  `getSoftwareVersion`, and `/cgi-bin/global.cgi?action=getCurrentTime` (bare
  `result=YYYY-MM-DD HH:MM:SS`, **no offset**).

Two negatives that change the plan:

- **`getConfigCaps&name=Network` returns the plain config, not a capability document.** It
  does not ignore only its *channel* parameter (`docs/dahua-storage.md`) — for this name it
  ignores the *caps* verb entirely. So Dahua declares no ranges for network fields, and
  every bound has to be hardcoded. Hikvision's self-describing `/capabilities` has no Dahua
  counterpart outside `encode`.
- **The web/HTTP port is not reachable by name.** `HTTP`, `HTTPS`, `HTTPD`, `Telnet`,
  `ClientPort` and `NetPort` all answered `403 Authority:check failure.` — the same body
  a genuinely permission-denied read returns. **On Dahua a 403 `Authority:check failure.`
  is indistinguishable from an unknown config name**, which makes discovery by
  name-guessing a dead end: a wrong guess and a real denial look alike. Of the three ports
  DVRTool records for a Dahua device, only RTSP is readable.

## Nx / DW Spectrum: there is no device config, and that is the whole answer

`GET /rest/v3/system/settings` returns 115 flat system-wide keys. Time-related, in full:

    timeSynchronizationEnabled = true
    primaryTimeServer = {00000000-0000-0000-0000-000000000000}
    syncTimeEpsilon = 200
    syncTimeExchangePeriod = 600000
    osTimeChangeCheckPeriodMs = 5000
    maxDifferenceBetweenSynchronizedAndInternetTime = 2000
    maxDifferenceBetweenSynchronizedAndLocalTimeMs = 2000

That is a *distributed-clock* configuration, not an NTP client: `primaryTimeServer` names
**which server in the VMS system is the clock master** by GUID (all-zero = none, follow the
internet). There is no NTP address, no time zone, no port, no IP — the zone and the address
belong to Windows on the box, and `/rest/v3/system/time` and `/rest/v3/servers/this/time`
are both 404.

What is readable and worth showing: `synchronizedTimeMs` (the VMS clock, on both
`/rest/v3/system/info` and `/rest/v3/servers/this/info`), `version`, and the server's
`endpoints` list from `/rest/v3/servers` — four `ip:7001` addresses on Site D, which is the
closest thing Nx has to "the LAN address" and is *observed*, not configured.

So the cross-vendor Config tab shows Nx a **clock** and a **version** and nothing else. The
feasibility pass called this a hole that needs a designed state; the shape of the state is
now known — it is not "unreadable", it is "this recorder has no device config, by design",
which is a different cell from `?`.

## What the pass actually found: two clocks are wrong

Read within the same second as the local machine (`2026-09-09T07:24:30-04:00`, i.e.
`11:24:30Z`):

| recorder | reported | verdict |
|---|---|---|
| the lab recorder | `2026-09-09T07:24:29-05:00` | wall clock right, **declared offset wrong** |
| Site B (Dahua) | `2026-09-09 06:24:26` | **58 minutes behind** |
| Site D (Nx) | `synchronizedTimeMs` ≈ `11:23:43Z` | correct |

**Site B is an hour behind.** Its NTP is enabled and pointed at `time.windows.com`, and it
is syncing fine — but `NTP.TimeZone=25` / `Easterntime` with `Locales.DSTEnable=false` means
the device sits at UTC−5 year-round. Every recording on that 128-channel NVR is currently
stamped an hour early, and nothing in DVRTool would have said so. Footage search, timeline
geometry and export naming all run on that clock.

**The Hikvision recorders lie about their offset.** `localTime` came back
`07:24:29-05:00` — the digits are correct EDT, but the offset says −05:00 while the actual
local offset is −04:00, even though the device's own `timeZone` string carries a DST rule
(`CST+5:00:00DST01:00:00,M3.2.0/02:00:00,M11.1.0/02:00:00`) that is active on 9 September.
So `DateTimeOffset.Parse(localTime)` yields an instant one hour off. `ParseIsapiTime` in
`HikvisionClient.cs` already keeps the wall clock and throws the offset away — that comment
("keep the wall clock") is now an observed requirement rather than a defensive guess, and
**it must not be "fixed" into honouring the offset.**

This is the read tier justifying itself before a line of it is written: a fleet time audit
would have caught Site B's hour, and a config tab that renders `localTime` verbatim would
have propagated the Hikvision offset bug into the UI.

## Consequences for the estimate

Unchanged in size; three details are now settled rather than assumed.

- **The Hikvision port path exists, is single, and self-describes its ranges.** The
  read tier can render ports with legal bounds from the device, and `FleetAudit` can check
  the saved record against them. Budget the two `def=`/`default=` spellings.
- **Writes must be read-modify-write documents, not synthesized ones** — proven by the
  M-series NTP fields absent from the I-series.
- **Dahua ports are mostly unreachable and Dahua declares no ranges.** The port column is
  Hikvision-only in practice (RTSP aside), and Dahua's bounds are ours to hardcode.
- **Nx's "not applicable" is most of its row**, and its clock is the one cell it does fill.

The address/port write tier is untouched by this pass: nothing here weakens the
"a network write cannot be verified on the socket that issued it" argument, and
`adminAccesses` being a single document makes a port change *easier to issue* and no easier
to recover from.
