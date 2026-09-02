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
the SDK login is verified against the **web port's** identity pin, which works because the
SDK reports the same serial as ISAPI — *not* always byte-identically (M-series firmware drops
a hyphen), which is why serial comparison strips hyphens. **Every live media gets
`:avcodec-threads=1`** (`MainWindow.AddLiveDecodeOptions`): Hikvision stamps frames with zero
clock lead and LibVLC's frame-threading latency then drops every frame after the first — the
"first frame only" symptom is LibVLC, never the SDK. The Live tab's **Grid** mode
(`MainWindow.LiveGrid.cs`, `LiveGridLayout` in Core) runs one SDK login with a preview per tile,
paged at 16 because independent LibVLC players hit a measured CPU cliff at 21; tiles come from
the ISAPI channel list, not the SDK channel count. Maximizing a tile keeps its sub stream up
full-size until the main stream has a **displayed picture** (`Media.Statistics` polled; the
hidden big view is `Visibility.Hidden`, never Collapsed, so its window handle exists) and only
then swaps — never a black pane while the main stream warms up. The **footer stats** (codec,
size, fps, bitrate for the selected/maximized camera; `MainWindow.LiveStats.cs`, pure math in
`LiveStats` in Core) are deltas of LibVLC's per-media counters, never its `InputBitrate` — and
VLC 3's `DecodedVideo` counts **twice per frame** (once per packet in, once per picture out),
`DisplayedPictures` counts 80 ms refresh re-renders, and the whole block is a 250 ms snapshot,
so fps is the decoded delta halved over a four-second `LiveStatsWindow`, never a one-second
delta and never the displayed counter; it reads "11/12 fps", measured over the configured rate
the encoder declares in the track header.

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
automation. Panels are saveable devices since 2026-08-26 (`SavedDevice.Kind = "panel"` — SDK port
only, own credentials, serial-bound on first roster read); the Access tab reads saved panels plus
ad-hoc typed addresses, and the GUI **Users tab** has two modes — DVR/NVR login accounts and
Access-control cardholders — each a read-only matrix (row per user/fob, column per selected
device, `FleetMatrix.cs` in Core) where an unreadable device shows "?" and is excluded from row
status, never read as "missing". Both go through `src/DVRTool.Vendors.HikvisionAccess`, which rides the shared
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
complete path), `--import-ivms` (reads the live SQLCipher DB and decodes each `Card.CardNo` directly to
its fob via `IvmsCardCipher`, falling back to the unique-expiry join only for the rare card that does not
decode — near-complete), `--capture-key`/`--where`/`--clear`. The map is cached in a DVRTool-owned file and
applied automatically to the roster/find/export views in both front ends. The per-install SQLCipher key is
supplied by the operator at runtime (flag / `IVMS_DB_KEY` / cached key file) — **never hardcoded**; no key
capture (debugger) or write-back into iVMS is implemented. Read `docs/ivms-integration-findings.md` before
touching it.

**Access provisioning:** DVRTool is the authority for Site A add/remove-user; iVMS is out of the
runtime flow (`docs/hikvision-access-provisioning-handoff.md`). `AccessPolicy` (`DVRTool.Core`) loads the
iVMS-pulled `access-control-policy.json` (kept under gitignored `artifacts/`, never committed) and
`ResolveGrants` unions a person's groups into per-panel door sets; `AccessProvisioner` plans onboards
(physical fob in, bounded validity window, plan-1/24x7 with a warning for any non-24/7 group) and offboards
(name→fob via the identity map, revoke on every panel); `AccessReconciler` is the read-only drift report.
CLI: `access reconcile | onboard | offboard`, `--dry-run` default and `--force` required for any write. **No
live write has been fired** — the `.223`/ocb2 canary is gated on operator go-ahead (handoff §10).

**Storage / retention:** the GUI Storage tab (`MainWindow.Storage.cs`) and `dvrtool storage
disks | retention | plan | set` cover disk inventory, per-camera oldest-footage/days-held, the
worst-case retention estimate, and the "we need X days" bitrate planner — Hikvision only, via
`IStorageClient` (`Storage.cs` in Core, `HikvisionClient.Storage.cs`; the estimator math is pure
and in Core). Read `docs/hikvision-storage.md` before touching any of it — notably: capacity is
decimal MB and **free space is permanently 0** on a healthy recorder (retention = capacity ÷
max bitrates, never free space); `status=notexist` disk rows are ghosts of removed drives, and
the capabilities hddList `size` is a firmware ceiling, not the chassis bay count; recording
search returns oldest-first, which is what makes per-camera oldest one cheap POST. One
firmware (Site E, DS-7716NI-I4/16P V4.61.030) rejects the everything-window search
with a nonsense 500 every time and fails wide windows intermittently, so on a non-401 failure
`FindOldestRecordingAsync` walks the playback calendar (`dailyDistribution`, per month)
to the earliest recorded day and searches only that day. Writes
(`plan --force`, `set --force`, the GUI Apply button — the deliberate exception to
"GUI writes stay in the CLI", since a bitrate change is reversible from the same tab) do a
full-document PUT, then read back and report what the device kept. Estimates are worst-case on
purpose (validated on Site C: estimated 16.5 days, held 24.1).
