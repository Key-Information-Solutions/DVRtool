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

**Fisheye dewarp:** two renderers, and **hardware is the default** — an operator at a
workstation is the ordinary case and headless/RDP is the exception. `DewarpBackendPolicy`
(`FisheyeBackend.cs` in Core) picks; every rule that declines hardware is about whether the pane
can be **presented** (remote session, software compositing, a WARP adapter), not about speed, so
an explicit GPU preference overrides all of them. The accelerated path
(`src/DVRTool.Render.D3D11`, Vortice + a runtime-compiled `Dewarp.hlsl`) has **no
`DewarpMap`**: the projection is evaluated per pixel from 176 bytes of constants, the mip level is
chosen per pixel, and the draw measures **0.02–0.04 ms** — the entire per-frame cost is the 9.8 MB
plane upload, which is why `Upload` is split from `RenderPane` (sixteen panes of one fisheye: 0.62
ms with one upload, 7.1 ms with sixteen). The GUI surface is `DewarpSurface` (`src/DVRTool.App`),
a DXGI swap chain on a hosted child window — so it paints **over** WPF content, like `VideoView` —
that falls back to `CpuDewarpRenderer` live on a device loss. Read
`docs/fisheye-dewarp-acceleration.md` before touching any of it — notably: the HLSL and
`DewarpShaderConstants` are twins that must be edited together (the tests hold the C#
transcription against `DewarpGeometry`, so the HLSL is a transcription of something proven);
`LensProjection`'s **enum ordinals are load-bearing** because the shader switches on them; an
integer source coordinate is a pixel *centre*; a wide rectilinear pane minifies hardest in the
**middle**, not the corners; and `Bilinear = false` does **not** disable mipmapping. The GUI
surface is the Live tab's **◎ Fisheye toggle** (`MainWindow.Dewarp.cs`, its own tab until
2026-09-09): dewarping is a way of looking at a live camera, not a place to go, so it takes over
whichever *single* camera is on screen — the single view, or a maximized grid camera — and
inherits device/channel/stream/transport from the Live toolbar. It is **disabled on a grid page**
because a dewarp wants the full-resolution picture and a page is sixteen sub streams; over a
maximized camera it **borrows the grid's SDK login** (a stream slot, not a session). Toggling
**restarts the picture** — `VideoView` rendering and `vmem` callbacks are different decoders and
one media cannot feed both — and anything that takes that camera off screen (leaving the grid,
Esc, another device) ends the mode, while another *channel* only stops the stream. It feeds from
`VlcFrameSource` — LibVLC 3's video callbacks
(`vmem`) into `DewarpFrameRing` (Core: three pinned I420 slots, newest frame wins, the presented
frame stays valid until the next so a drag redraws with no upload) — and was **verified live on
2026-09-03 on Site C channel 21 (2560×2560 H.265 over SDK 8000): 30 fps decoded, 30 shown, 0
skipped on the GPU, and 30/30 on the CPU renderer too**; the plane upload is ~5 % of a frame
period, not a blocker, and the next performance step is hardware decode (a frame-source change),
never DX12/Vulkan. `vmem` is strictly sequential (lock → copy → unlock → display, one buffer at a
time), which is why three slots suffice; never throw out of a callback. Mouse input reaches the
pane from the swap chain's **child window** (`SwapChainHost.WndProc` answers `HTCLIENT` and
translates the WM_ messages into pane-pixel events; WPF never sees them). Drag is `DewarpDrag`
(Core), a **damped least-squares re-aim**: on a ceiling mount the roll rule pins the nadir to the
pane's vertical centre line, so the exact-centre grab has no sideways solution and plain Newton
flung it; pitch 0 is a **fold** (negative pitch = opposite yaw = a different picture), so it is a
boundary, never a point to difference across. The **Test pattern** button (`FisheyeTestPattern`)
shows a tiled floor through the calibration with no camera. Still missing: per-device calibration
persistence, circle detection, Quad in the GUI, hardware decode.

**Storage / retention:** the GUI Storage tab (`MainWindow.Storage.cs`) and `dvrtool storage
disks | retention | schedule | plan | set | pin` cover disk inventory, per-camera
oldest-footage/days-held, the worst-case retention estimate, and the "we need X days" bitrate
planner with its pinned cameras — Hikvision and Dahua,
via `IStorageClient` (`Storage.cs` in Core, `HikvisionClient.Storage.cs`,
`DahuaClient.Storage.cs`; the estimator math is pure and in Core). Read
`docs/hikvision-storage.md` before touching any of it — notably: capacity is
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
purpose (validated on Site C: estimated 16.5 days, held 24.1). **Recording mode** (2026-09-02):
`CameraStream.Schedule` (`RecordingSchedule` in Core — spans per weekday with a vendor label and
`RecordingTrigger` flags; `Summary`, `IsEventOnly`, `DescribeNow`, `DescribeWeek`) is the
Storage tab's **Recording** column (tooltip: the week), `retention`'s RECORDING column and
`dvrtool storage schedule`. Hikvision reads it from `GET /ISAPI/ContentMgmt/record/tracks` — a
whole day is written `Monday 00:00 → Tuesday 00:00`, the on/off switch is `enableSchedule` in
the vendor extension and **never the track's `Enable`** (false everywhere); Dahua from
`configManager.cgi?action=getConfig&name=Record` — `TimeSection[day 0–6 Sun–Sat, row 7 =
holiday][n]="mask hh:mm:ss-hh:mm:ss"`, bits 1 regular / 2 motion / 4 alarm / 8 card / 16 Intel /
32 MD&Alarm (inferred from Site B's 39) / 64 POS, `RecordMode` 1 = "Continuous (manual)";
Nx from the schedule cells (`always` / `metadataOnly` / `metadataAndLowQuality` + `metadataTypes`,
`dayOfWeek` 1 = Monday). A camera whose schedule is off or empty leaves the retention total;
`IsEventOnly` cameras earn the "will hold more than the estimate says" caveat in both front ends
(Site E: 13 of 14 on motion; Site D: 48 of 64). **Notation** (2026-09-03, one scheme for
every vendor, legend in `RecordingSchedule.Notation`): `|` joins triggers sharing one span, `+`
joins modes splitting the week, and **`*` marks a mode that does not run the whole week** — so
`Continuous` is 24/7, `Continuous*` has a gap, and `Continuous* + Motion*` is a mix (a mark, not
words, because "Continuous + Motion" is itself a plausible mode name). Starring is **per mode
off the union** of its spans, so overlapping vendor entries cannot inflate past 168 h; every
mode in a mix is therefore starred, which makes "hours when *nothing* records" a separate
question (`HasDeadTime`/`DeadTime`, reported by both front ends). The hours moved to
`HoursText` (GUI Recording tooltip, a line per camera in `storage schedule`); Nx's combined
cell is **"Motion & low-res always"** because a mode name may not carry a `+`. **Pinned
cameras** (2026-09-03, live on the lab recorder): `ChannelPinStore` (`ChannelPins.cs`,
`%APPDATA%\DVRTool\channel-pins.json`, keyed `host:port` like the cert/identity pins) holds the
cameras the planner may not decide for — Pin/Unpin/Clear in the Storage tab, `storage pin` in
the CLI (`plan --ignore-pins` refuses `--force`; `set --pin` moves a pin). A pin names a rate or
says "keep current" (resolved at plan time); pinned rates come off the budget **before** the
split, like Nx secondary streams. A pin records the camera's **name** and the device's
**serial**, and a channel that now answers to a different name (Nx channel numbers are
positional) or a different serial is **reported, not applied**. `BitratePlan.MissReason` blames
the pins before "camera minimums", and an unreadable/corrupt pin file or a failed save is
**loud** — planning refuses rather than treating "no pins" as a fact.

**Dahua storage** (`docs/dahua-storage.md`, verified 2026-09-02 on Site B, a
DH-NVR608H-128-4KS3/I): disks come from `storageDevice.cgi?action=getDeviceAllInfo` as
**float byte counts** summed over `list.info[N].Detail[M]` partitions, with no model/serial/
health over CGI; streams from the 0-based `Encode[ch].MainFormat[t]` table, which lists only
bound channels, and the reported max bitrate is the **highest of General/Motion/Alarm** (`t`
0/1/2) — the write sets all three in one `setConfig`; `RecordMode[ch].Mode=2` marks a channel
as not recording; `encode.cgi?action=getConfigCaps` **ignores its channel parameter** and
returns `caps[N]` for every channel (`BitRateOptions=min,max` kbps). **`mediaFileFind`'s
`condition.Channel` is 1-based** (0 → 400 Bad Request) while `items[].Channel` is 0-based —
this was a live bug in the Dahua search/download path until 2026-09-02; 400 from `findFile`
means "no recordings / no camera", and results are oldest-first so the everything window with
`count=1` is the oldest recording. A 403 `Authority:check failure.` is a per-config permission
denial, not the lockout (that is 403 JSON with `RmLock`). The Dahua bitrate **write has not
been fired live**; Site B2's saved credentials are rejected (401) and it has not been read.

**Nx Witness / DW Spectrum storage** (`src/DVRTool.Vendors.NxWitness`, `docs/nx-witness-storage.md`,
2026-09-02; `Vendor.NxWitness`, `--vendor nx`, "DW Spectrum / Nx Witness" in the Add-NVR dialog):
a software recorder whose REST API (bearer-token sessions from `POST /rest/v3/login/sessions`),
plain HTTP and RTSP all share **port 7001** over a self-signed cert — so the record's web and RTSP
ports are the same number, TLS defaults on, and there is **no SDK port** (`VendorPorts.HasSdkPort`
false, `SdkPort` 0; the dialog hides the row and `dvrtool test` probes two ports). The identity pin
is the server GUID from the anonymous `/api/moduleInformation`, but the login still runs first so a
wrong password is reported as one. Nx has **no channel numbers**: the client numbers the camera list
sorted by name, keeps that list per instance, refuses a write when the list changed between the
read and the write (the GUI reads and writes on different clients), and its URL builders throw
until some call has read the list (`ConnectivityProbe.StartAll` tolerates that; the CLI's URL
commands read channels first). "Disks" are storage volumes: capacity = size − `spaceLimitB`
reserve, and backup / not-used-for-writing volumes are listed but excluded via
`HddInfo.RecordsFootage`. A camera's "max bitrate" is its **busiest schedule cell** — the preset
`bitrateKbps`, or Nx's own quality formula `(0.1+0.9·q/4)·0.009·(w·h)^0.7·fps·codec` (`NxBitrate`) —
and **Nx archives the secondary stream too** unless `dontRecordSecondaryStream`, so
`CameraStream.SecondaryRecordedKbps` / `RecordedBitrateKbps` and `PlanCamera.FixedKbps` (Core) carry
it into every total and the planner spends it before splitting. The write PATCHes every recording
cell to `preset` + the bitrate and **refuses up front** when the site's `cameraSettingsOptimization`
is off or the camera keeps its own profile (`options.isControlEnabled` false), because Nx would then
store the number and never send it to the camera. Times are UTC ms rendered in the operator's zone.
**Verified live 2026-09-02 on Site D** for every read: `mediaStreams` is a bare
array with an `encoderIndex` −1 transcoding pseudo-stream to skip; the "don't record" switches live in
the string-typed `parameters` bag (absent = false); `parameters.space` carries a volume's size;
`schedule.maxArchiveDays` is negative when disabled and becomes `CameraStream.ArchiveCapDays` when
positive; footage `limit=1` answers `[]` so oldest footage is a coarse pass then an exact pass before
it. **The PATCH has not been fired.** The site forwards nothing, so it is reached through the **DW Cloud
relay** (`NxCloudRelay`, `docs/nx-witness-storage.md` §relay): host `<cloudSystemId>.relay.vmsproxy.com`,
HTTPS 443, a 307 to a regional node that `NxRelayHandler` follows once keeping the Authorization
header, chain validation instead of the cert pin (Let's Encrypt wildcard, rotates), the same local
login, **no RTSP** (`GetLiveUri`/`GetPlaybackUri` throw `NotSupportedException` with a message the
front ends show; the dialog and `dvrtool test` skip the RTSP row).

**Which tracks reach the disk** (2026-09-08, `IRecordingOptionsClient` in Core
`RecordingOptions.cs`, `NxWitnessClient.RecordingOptions.cs`, `dvrtool recording show | set`;
Nx only — the appliance vendors' sub-stream is never archived): the per-camera "record the
secondary stream" and audio switches. **Audio is TWO independent switches**:
`options.isAudioEnabled` (General tab "Enable audio" — whether audio is pulled at all, off
unless somebody turned it on) and `parameters.dontRecordAudio` (Expert tab "Do not record
audio" — a *property*, absent by default, and the **durable** one: it keeps audio off the disk
even if capture is later enabled). `AudioReachesDisk` is the conjunction. Capability is
separate and *numeric*, `parameters.isAudioSupported` (1/0), which
`forcedIsAudioSupported` overrides. **On this API "the key is absent" never means "the setting
does not exist"** — every property-bag switch is absent until set, so the audio bar was invisible
in a read of all 64 and was found only by ticking the box in the DW client and diffing the
device document (filter out `bitrateInfos`, `storageInfo`, `deviceAgentManifests`,
`availableProfiles`, `status` — they churn every read, and the diff shows *other people's*
edits too).
`dontRecordSecondaryStream` is a **property, absent on every default camera**, so the write
tries the `parameters` bag (the strings "1"/"0", as the DW client writes them) then `options`
(bool), verifies by read-back,
remembers which bag stuck, and reports **rejected** rather than success if neither did. It is
**not** `isDualStreamingDisabled`: 63 of 64 Site D cameras run
`parameters.motionStream = "secondary"`, so disabling dual streaming would take motion
detection with it — "don't record" keeps the stream pulled and analysed and only off the disk.
The load-bearing rule: a camera on `metadataAndLowQuality` ("Motion & low-res always") records
the **secondary continuously**, so turning it off makes that camera motion-only and empties the
quiet hours — `recording set --secondary off` holds those back unless
`--include-lowres-always`. Fired live on Site D 2026-09-08: **28 changed, 0 failed, 36 held back** —
`parameters` is the bag that takes the property on 6.1.1.42624 (verified by a raw read
independent of the client), worth ~64 GB/day and 28 fewer concurrent archive files on an
IOPS-saturated array. Then `--record-audio off --all`: **63 changed, 0 failed**, giving
`dontRecordAudio` on all 64 (the 64th was the operator's own console tick). Schedules there
move under you: seven cameras changed mode during an 18-minute window
that day, edited from a remote client — re-read modes immediately before any batch.

**Device config / the clock audit** (2026-09-09, `docs/device-config.md`, specced in
`docs/device-config-spec.md` off the observed endpoint set in `docs/device-config-discovery.md`):
the GUI **Config** tab and `dvrtool config show | clock | audit | set` read a recorder's own
settings — clock, time source, service ports, LAN address, device name — for all three vendors
(`IDeviceConfigClient` / `IDeviceConfigWriter` in `DeviceConfig.cs`; `ConfigAudit.cs` holds the
pure audit plus the `ClockSweep` both front ends use). **The product is the fleet clock audit**:
footage search, `TimelineWindow`/`PlaybackClock` and `ExportNaming` all run on NVR-local wall
clock, and the first sweep of the saved fleet (21 recorders, 0 failures) found four wrong clocks —
one Dahua an hour out with NTP syncing and `DSTEnable=false`, one 35 min out with NTP off, two
Hikvision with no time source or minutes of drift. **Drift is a difference of wall-clock digits,
never of instants**: Hikvision reports `localTime` as `-05:00` while standing in `-04:00`, so
`ParseIsapiTime`'s "keep the wall clock" is now load-bearing and must not be "fixed"; a
genuinely-other-zone recorder is handled by `SavedDevice.ExpectedOffsetMinutes` (Add/Edit device →
Clock offset), never by inference. `ClockDrift.Measure` takes both ends of the round trip so the
error bar is real. **Out of scope is not a failed read**: `ConfigScope.ClockOnly` (Nx — a
distributed clock master by GUID, not an NTP client; zone and address belong to Windows) renders
`n/a` + the reason while a failed read renders `?`, and `TimeSourceStatus.NtpEnabled` is null
there rather than false. Hikvision's ports are one document (`/ISAPI/Security/adminAccesses`,
`DEV_MANAGE` = the SDK port, and the default attribute is spelled **`def=` and `default=` in the
same capabilities document**); `time/localTime` is a **plain-text scalar**; network ranges exist
only at the interfaces **list** level. Dahua answers **only RTSP**'s port — every other name
returns a 403 `Authority:check failure.` indistinguishable from a real denial, so **config names
are never guessed at runtime** — declares no ranges (`ServicePortRange.Fallback`, labelled as
ours), keeps the zone in `NTP` not `Locales`, and reports 65535 Mbps for an unconfigured bond.
Writes are **read-modify-write, enforced**: refuse without a prior read, re-read and refuse if the
document moved underneath, then read back and report what the recorder kept (the M-series NTP
document's `portType`/`customPortNo` are why). `--sync-now` is refused on a recorder that syncs
from NTP, and a Dahua `--timezone` given as text is refused because its zone is an index — there
is no IANA mapping on either vendor. **Tier 3 (LAN address and service-port writes) is
deliberately unbuilt** — spec §7 — since a network write cannot be verified on the socket that
issued it. Fired live: the Hikvision NTP-interval write on the lab recorder (30 → read back →
back to 60). Not yet fired: any Dahua write, the Hikvision zone/name/sync-now writes, and the GUI
Apply button.

**Recorded playback** (2026-09-04, `docs/playback.md`): the GUI Playback / Export tab is a
day-per-camera **timeline** (`TimelineControl`, geometry in `TimelineWindow`/`FootageCoverage`/
`PlaybackClock` in Core, all tested) — click seeks, drag selects an export range, wheel zooms,
right-drag pans — and the video comes over the **web port, never RTSP**: `IPlaybackClient`
(`*Client.Playback.cs`) hands the export body to LibVLC through a `StreamMediaInput`, a seek is
a new request, pause pauses the download. Hikvision's download body opens with a **64-byte IMKH
envelope** before the first pack header, which VLC's PS demuxer refuses and ffmpeg silently
skips — `SkipTo` drops it. Dahua's `loadfile.cgi` refuses any window **over ~6 h** (400; 6 h
verified, 8 h refused, midnight irrelevant) so `MaxLoadfileWindow` caps requests and the tab
continues from where a body ends; and the shipped LibVLC has **no avformat plugin**, so DHAV
goes through `ContainerPipe` (ffmpeg `-f dhav … -c:v copy -an -f mpegts`, back-pressured end to
end). Nx plays `/media/{id}.mkv` and so works through the DW Cloud relay. **LibVLC's `Time` is the demuxer's read position, not the picture's**, and the body arrives
faster than real time: a window spanning several motion clips is concatenated by the recorder
and the clock jumps minutes per second across the gaps (Site E stairway) — so a body is
requested for **one run of footage at a time** (`FootageCoverage.SpanAt`) and `EndReached`
carries playback to the next run; never compare that clock against the footage map to decide
to skip. Every face peeks the
first 16 KB so "accepted, sent nothing" fails up front. `dvrtool footage --probe` opens exactly
the body the GUI plays and reports what arrived; verified 2026-09-04 on Site C, Site B
and Site D (relay). Hikvision SDK playback (`NET_DVR_PlayBackByTime_V40`) is approved but
unbuilt; Dahua export does not chunk past the 6 h ceiling.
