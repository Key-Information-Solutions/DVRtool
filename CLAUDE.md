**MANDATORY:** Workflows are allowed, but they MUST never use Fable unless asked. Opus should be used for implementing, Sonnet used for big research, and Haiku used for small tasks or local searching. Individual agents do not abide by these restrictions. ALWAYS commit changes, even to main, assuming they build and pass tests. Branches and worktrees should merge to main after commit.

**Positioning:** DVRTool is a GUI-first Windows desktop app (`src/DVRTool.App`, WPF) with a `dvrtool`
CLI (`src/DVRTool.Cli`) as its automation/scripting surface; both front ends ride the shared
`src/DVRTool.Core` engine, so export/remux/overwrite and name-enrichment behavior is identical between them.

**Installer:** `build-installer.ps1` (or `dotnet build installer\DVRTool.Installer.wixproj`)
produces a per-machine WiX MSI carrying both front ends self-contained; it installs to
`Program Files\DVRTool` (`app\` GUI, `cli\` CLI), puts `cli\` on the system PATH, writes the
`HKLM\SOFTWARE\DVRTool` discovery key, and supports `msiexec /qn` with `DESKTOP_SHORTCUT=0`.
The wixproj is deliberately not in `DVRTool.slnx` — see `docs/installer.md` (UpgradeCode and
component GUIDs are permanent; MSI versions are numeric x.y.z).

**Device ports:** The three ports an NVR record carries are per-vendor, not universal — the
vendor SDK port is 8000 on Hikvision and **37777** on Dahua (`VendorPorts.Sdk` in
`DVRTool.Core`, honoured by the GUI Add-NVR dialog, `dvrtool --sdk-port` and `dvrtool test`).
Read `docs/device-ports.md` before adding or changing a port field: it records why Dahua's UDP
port and Hikvision's Enhanced SDK port are deliberately absent, and which of the three ports
each feature actually needs. Note the recorder's SDK port is **load-bearing on Hikvision**
(live video, below) and pure recon on Dahua; the Access tab's port is a third field again,
for separate hardware.

**SDK live video:** Hikvision live view does not need RTSP. `NET_DVR_RealPlay_V40` with
`dwLinkMode = 0` brings the media back over the same SDK-port session the login authenticated
on, which across our fleet is the difference between live view working at 3 sites and at 14
(`src/DVRTool.Vendors.HikvisionSdk` — `HikvisionSdkSession`, the GUI Live tab's transport
dropdown, and `dvrtool live`). The HTTP alternative was probed on all 17 recorders and
answered 403 on every one, so do not design around `httpPreview`. Read
`docs/hikvision-sdk-live.md` before touching it — notably: an NVR's **display channel 1 is
device channel 33** and getting it wrong shows no error at all (`SdkChannelMap` reads the
mapping off the login response); the data callback runs on an SDK thread that must never be
blocked, so `SdkMediaStream` drops the oldest bytes rather than applying back-pressure; and
the SDK login is verified against the **web port's** identity pin, which works only because
the SDK's serial is byte-identical to ISAPI's.

**Device identity:** A successful login proves the credentials, not the hardware. Sites put
several systems behind one address on different forwarded ports, and one shared account logs
into any of them — so DVRTool pins each device's **serial** per `host:port`
(trust-on-first-use, `identities.json`, sibling of the cert pins) and refuses to act when a
different serial answers. `DeviceIdentity.cs` / `DeviceIdentityGuard.cs` in `DVRTool.Core`;
saved GUI devices also carry `ExpectedSerial`; panels are verified before reads and before
both legs of a `grant`/`revoke`; `FleetAudit` catches two records on one address and one
recorder saved twice. Read `docs/device-identity.md` before touching any of it — notably:
a blank serial is *unverifiable*, never a match; `AccessCard.PanelHost` is the
**port-qualified** label, so panel addresses are `ip[:port]` throughout; and
`DeviceIdentityException` is deliberately not an `NvrException`, so per-device "carry on with
the rest" handlers do not swallow it.

**Access control:** Hikvision/OEM door panels are surfaced primarily in the GUI Access tab
(`src/DVRTool.App`, `MainWindow.Access.cs`) for viewing rosters and importing cardholder names; the
`access` CLI command group provides the same reads **plus** the gated writes (`grant`/`revoke`) for
automation. Both go through `src/DVRTool.Vendors.HikvisionAccess`, which rides the shared
HCNetSDK P/Invoke surface in `src/DVRTool.Vendors.HikvisionSdk` (SDK port, 8000 by default). Reads and writes are both live-verified against Site A's
three OCB panels (writes via an approved canary round trip on a throwaway fob, rolled back
clean; the GUI tab stays read-only toward the panels).
Read `docs/hikvision-access-control-findings.md` before touching it — notably: these panels store **no
cardholder names**, and `dwModifyParamType` plus the door/right-plan pairing are the two traps that
silently produce a card that never opens a door.

**Name enrichment:** Because the panels hold no names, cardholder names are imported **one-way** from
iVMS/NVMS. The primary surface is the GUI Access tab's "Cardholder names (from iVMS)" group (Import CSV /
Import from iVMS database / Cache key / Clear map); the `access identity` verbs are the CLI/automation
equivalent (`src/DVRTool.Vendors.HikvisionIvms`): `--import-csv` (the supported plaintext Person export —
complete path), `--import-ivms` (reads the live SQLCipher DB and correlates names to fobs by unique expiry
— partial), `--capture-key`/`--where`/`--clear`. The map is cached in a DVRTool-owned file and applied
automatically to the roster/find/export views in both front ends. The per-install SQLCipher key is supplied
by the operator at runtime (flag / `IVMS_DB_KEY` / cached key file) — **never hardcoded**; no key capture
(debugger) or write-back into iVMS is implemented, and the `Card.CardNo` cipher is deliberately not used.
Read `docs/ivms-integration-findings.md` before touching it.
