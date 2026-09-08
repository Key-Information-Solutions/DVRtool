# Nx Witness / DW Spectrum storage, retention and the bitrate planner

**Established:** 2026-09-02. Read this before touching `src/DVRTool.Vendors.NxWitness`, and
read `docs/hikvision-storage.md` first — the Core model (`Storage.cs`), the estimator, the
CLI (`dvrtool storage`) and the GUI Storage tab are shared, and the Hikvision doc explains why
retention is capacity ÷ configured max bitrate and never free space. This doc covers what a
Network Optix media server (Nx Witness, and its OEMs DW Spectrum and Wisenet WAVE) does
differently: it is a **software recorder**, not an appliance, and three things follow from
that — its "disks" are storage volumes with a reserve, its "max bitrate" is whatever the
busiest schedule cell asks for, and it archives **two** streams per camera.

## What is and is not verified

The live target is Site D's DW Blackjack E-Rack `a DW Blackjack E-Rack` — "Site D" in the fleet — (DW
Spectrum **6.1.1.42624**, 198.51.100.10:7001 on the LAN, 64 cameras, one server; see the
2026-09-02 field notes). Its router forwards nothing, so everything below was verified
**through the DW Cloud relay** (next section) on 2026-09-02, with the local `admin` account:

- Anonymous: `GET /api/moduleInformation` (the verbatim reply is a fixture in
  `NxWitnessStorageTests`: `reply.id` `{11111111-…}` the server GUID, `brand` `dwspectrum`,
  `systemName` `Site D`, `version` `6.1.1.42624`, `cloudSystemId`), `GET /rest/v3/system/info`
  (64 device ids, one server, `restApiVersions` v1–v4); `/rest/v3/devices` without a
  session → 401. On the LAN, RTSP shares the port: `OPTIONS rtsp://198.51.100.10:7001/` →
  `RTSP/1.0 307`, `Server: DW Spectrum/6.1.1.42624`. The server's own certificate is
  self-signed (`O=Digital Watchdog, CN=DW Spectrum`, to 2027-10-01) and gets the usual pin.
- `POST /rest/v3/login/sessions` with the local account → `token` `vms-…`, `expiresInS`
  8 640 000 (100 days — which is why the client deletes its session on dispose).
- `GET /rest/v3/devices` (742 KB for 64 cameras), `/rest/v3/servers/{id}/storages`,
  `/rest/v4/servers/{id}/storages`, `/api/storageSpace`, `/rest/v3/system/settings`
  (`cameraSettingsOptimization: true`), and `/rest/v3/devices/{id}/footage` at detail
  levels 1 and 3 600 000 and with `limit=1`. What they answered is folded into the traps
  below and into the test fixtures.
- `dvrtool info | channels | storage disks | storage retention | storage plan (dry run) |
  storage set (dry run)` end to end: 64 cameras numbered by name (`KoV-…` then `Site D-…`),
  69.68 TB of recording pool, per-camera oldest footage back to 2026-06-26 (68 days held),
  the 90-day plan at 800 kbps per camera.

**Not exercised live**: the schedule PATCH. No write has been made to this server. The
canary below stays gated on the operator's go-ahead.

## The DW Cloud relay

DW Cloud is Nx Cloud under another name, and a cloud-connected system is reachable through
Nx's proxy with **no port forward**: `https://<cloudSystemId>.relay.vmsproxy.com` (the id is
`cloudSystemId` in `moduleInformation`, and the last path segment of the site's URL in the
DW Cloud portal). Verified on Site D:

- The relay answers every path with `307 Temporary Redirect` to a regional node —
  `Location: https://<id>.relay-us-mia-1-prod-dp.vmsproxy.com:443/<path>` — and the node
  proxies straight to the server: `/api/moduleInformation` through it is byte-identical to
  the LAN reply. Round trip ≈ 0.35 s.
- The node is a different host, and .NET (like every HTTP stack) drops `Authorization` on a
  cross-host redirect — so `NxRelayHandler` resolves the node once with an anonymous probe,
  sends every request there directly with its headers intact, remembers the node
  process-wide per relay host, and re-learns it if a GET is redirected again (a POST or
  PATCH in that window surfaces the 307 and the caller's retry lands on the new node).
- The certificate is a Let's Encrypt wildcard for `*.relay.vmsproxy.com` that rotates every
  90 days, so relay hosts get normal chain validation, **not** the trust-on-first-use pin.
  The identity pin is unaffected — same server GUID — and is keyed by the relay host, which
  is stable, never by the node.
- A **local** account logs in through it exactly as on the LAN. No DW Cloud account, no OAuth
  and no 2FA handling are needed. (Nx also offers cloud-account tokens from
  `POST https://dwspectrum.digital-watchdog.com/cdb/oauth2/token` with `grant_type=password`,
  `client_id=3rdParty`, `scope=cloudSystemId=<id>`; not implemented — the local account
  covers every DVRTool use.)
- HTTPS only: there is **no RTSP** through the relay. `GetLiveUri` / `GetPlaybackUri` throw
  `NotSupportedException` with a message both front ends show; `/media/` exports, search,
  storage and the planner all work. `dvrtool test` and the dialog's Test probe the web
  row alone.

Front ends: `--vendor nx --host <id>.relay.vmsproxy.com` (port 443 and TLS are implied);
in the Add-NVR dialog, typing a relay host as an Nx record's Host switches the port to 443,
turns on HTTPS and hides the RTSP row. `NxCloudRelay.IsRelayHost` is the one place that
decides what a relay host looks like (`*.vmsproxy.com`, so a pasted node name counts too).

## Endpoints

| What | Endpoint | Notes |
|---|---|---|
| Identity, model, version | `GET /api/moduleInformation` | anonymous; `reply.id` = server GUID → the serial pin; `brand` → "DW Spectrum" / "Nx Witness" / "Wisenet WAVE" |
| Login | `POST /rest/v3/login/sessions` | `{"username","password","setCookie":false}` → `token`; every later request carries `Authorization: Bearer <token>`; `DELETE /rest/v3/login/sessions/{token}` on dispose |
| Cameras | `GET /rest/v3/devices` | `id`, `name`, `physicalId`, `mac`, `url`, `serverId`, `deviceType`, `status`, `schedule`, `options`, `parameters`, `mediaStreams`, `mediaCapabilities` |
| One camera | `GET /rest/v3/devices/{id}` | the read-modify-write source for the bitrate PATCH |
| Storage volumes | `GET /rest/v3/servers/{serverId}/storages` | `path`, `spaceLimitB` (reserve), `isUsedForWriting`, `isBackup`, `type`, `status` ("Online"), **`parameters.space`** = size in bytes |
| Volume free space | `GET /api/storageSpace` | legacy; `reply.storages[]` with `storageId`, `url`, `totalSpace`, `freeSpace`, `reservedSpace`, `isOnline` — byte counts **as strings**; optional (a refusal leaves free space unknown, never invented) |
| Footage | `GET /rest/v3/devices/{id}/footage?startTimeMs=&endTimeMs=&detailLevelMs=` | `[{startTimeMs, durationMs, serverId}]`, UTC ms; the open period **has no `durationMs`**; `limit=1` answers `[]` on 6.1 — never use it |
| Site setting | `GET /rest/v3/system/settings` (v4: `/rest/v4/site/settings`) | `cameraSettingsOptimization` — "Allow Site to optimize device settings"; one flat object of ~115 keys |
| Bitrate write | `PATCH /rest/v3/devices/{id}` `{"schedule": {…}}` | every recording cell → `streamQuality: preset`, `bitrateKbps: N`; read back with GET |
| Export | `GET /media/{id}.mkv?pos=<ms>&endPos=<ms>` | Matroska, bearer-authenticated; `ExportNaming` names the raw file `.mkv` |
| Live / playback | `rtsp://host:7001/{id}?stream=0|1[&pos=<ms>&endPos=<ms>]` | same port as the API on the LAN; `stream=1` is the secondary; not through the relay |

Device ids are used without their braces in every path; the server accepts either.

## Traps and units

- **One port, no SDK port.** 7001 carries HTTPS, HTTP and RTSP; there is nothing to forward
  for an SDK. `VendorPorts.HasSdkPort(NxWitness)` is false, the record carries `SdkPort` 0,
  the Add-NVR dialog hides the row, and `dvrtool test` reports two ports. TLS defaults on
  (`VendorPorts.DefaultsToTls`), and `--host ip` alone means `ip:7001` over HTTPS.
- **The login is part of `GetDeviceInfoAsync`.** `moduleInformation` is anonymous, but the
  connectivity probe and the identity guard use `GetDeviceInfoAsync` as proof that the
  credentials work — so the session is opened first, and a wrong password is a 401
  `NvrException` ("credentials rejected"), never a healthy-looking identity. Nx has no
  Dahua-style lockout, but it does slow repeated failures down; the one-attempt-then-stop
  rule for probes stands.
- **Nx has no channel numbers.** Cameras are GUIDs. The client numbers the device list
  1..N sorted by **name, then id** (the Nx client's tree order), skipping I/O modules, and
  keeps that list for the life of the instance so `GetMainStreamsAsync`,
  `FindOldestRecordingAsync` and `SetMaxBitrateAsync` agree on what a number means. Three
  consequences: `dvrtool channels` is the authority on the numbering; the synchronous URL
  builders (`GetLiveUri`, `GetPlaybackUri`) throw `InvalidOperationException` on a fresh
  client until some call has read the list (`ConnectivityProbe.StartAll` tolerates this and
  stops at "an RTSP server answered OPTIONS"; the CLI's `live-url` / `playback-url` read the
  channel list first and therefore need a password on Nx); and because the desktop app reads
  with one client and writes with another, the client remembers the last list it saw per
  address, process-wide, and **refuses the write** when the camera at that position has
  changed ("the camera list changed since it was read — reload"). A camera added between
  Load and Apply would otherwise shift every number by one.
- **Times are UTC milliseconds.** Every other vendor speaks recorder-local wall clock, so the
  client renders Nx times in `Zone` — the operator's own time zone by default, which is what
  the Nx desktop client shows too and is honest to within a zone across a Florida fleet.
  Windows typed in the CLI/GUI are read the same way. Tests pin `Zone` to UTC or a fixed
  offset.
- **"Disks" are volumes, and the reserve is not archive.** Each storage keeps `spaceLimitB`
  free (Nx's own reserve — 300 GiB on Site D's 70 TB array), so `HddInfo.CapacityMB` is
  total − reserve and free space is free − reserve — which hovers at 0 on a full recorder,
  exactly like the appliance vendors (the server deletes the oldest footage whenever free
  space dips under the reserve). A backup volume duplicates footage and a volume that is not
  "used for writing" holds none — Site D lists its Z: and C: system volumes that way — so
  both are listed (Property "backup" / "not used for writing") with `RecordsFootage` false,
  `StorageInfo.TotalCapacityMB` sums the recording pool only, and both front ends name the
  volumes that were left out. The size comes from v3's `parameters.space` and the free space
  from `/api/storageSpace`; no model or serial per volume. `status` is "Online" (or the
  flag words map to ok / offline / checking / rebuilding), and a volume whose size nothing
  reports says "size unknown" and is not counted.
- **The "max bitrate" is the busiest schedule cell.** Nx schedules per hour per weekday
  (`schedule.tasks[]`, `dayOfWeek` 1–7, `startTime`/`endTime` seconds, `recordingType`
  always | metadataOnly | never | metadataAndLowQuality — the legacy `RT_*` spellings are
  accepted). Cells that record are compared and the costliest wins, because over a whole day
  the disks pay for the busiest hour. A cell with `streamQuality` **preset** costs its
  `bitrateKbps`; a quality cell (lowest … highest) costs what Nx will ask the camera for,
  computed with Nx's own rule (`NxBitrate.SuggestKbps`, from the open-source
  `CameraBitrateCalculator`): `(0.1 + 0.9·q/4) · 0.009 · (w·h)^0.7 · fps · codec`, floored at
  192 kbps, codec 1.0 for H.264, 0.8 for H.265, 2.0 for MJPEG, at the primary stream's
  resolution from `mediaStreams`. 1080p at 15 fps "high" ≈ 2.8 Mbps; 2688×1520 at 15 fps
  "highest" ≈ 5.8 Mbps. Confirmed against the server's own figure: `mediaCapabilities.
  streamCapabilities.primary.maxBitrateKbps` is 10 666 for a 2560×1440 camera at 30 fps,
  and the rule gives 10 657. Site D has **no** preset cell — every camera records by
  quality (`low` … `highest`, `bitrateKbps` 0 everywhere) at 12–30 fps, mostly
  `metadataAndLowQuality` (motion + lo-res), so its worst-case estimate assumes motion
  around the clock and lands far under the 68 days actually held. That is the design; on a
  motion-heavy Nx site the per-camera oldest column is the honest number. The MODE column
  reads MIN / LOW / NORM / HIGH / BEST for the qualities and KBPS for a preset;
  `FixedQuality` carries the level 0–4. A schedule that is disabled, or has only "never"
  cells, reports the camera as not enabled. A camera whose resolution Nx has not probed yet
  (`"*"`) gets no bitrate rather than a guess.
- **`mediaStreams` is a bare array** in v3 — `[{codec, encoderIndex, resolution, transports}]`
  — with a third entry, `encoderIndex` −1 / codec 0 / resolution `"*"`, that is the server's
  transcoding pseudo-stream and is ignored; index 0 is the primary, 1 the secondary. Codecs
  are FFmpeg ids (27 H.264, 173 H.265; Site D has both, up to 7552×3776 on the multi-sensor
  units). The `{"streams": […]}` wrapper and the string-encoded form are still accepted.
- **Two settings bags.** `options` is typed and always present: `isControlEnabled`,
  `isDualStreamingDisabled`, `isAudioEnabled`, `backupPolicy`, … `parameters` is the
  resource property bag — strings only, listing just what has been set (`bitratePerGOP`,
  `hasDualStreaming`, `keepCameraTimeSettings`, `primaryStreamConfiguration`, …). The
  "don't record the primary/secondary stream" switches are *properties*, so a camera that
  records both (the default, all 64 on Site D) has no such key at all; `NxCamera.Parse`
  reads either bag and takes absent as false.
- **Nx archives the secondary stream too.** Unless `dontRecordSecondaryStream` is set (or
  `isDualStreamingDisabled`, or the camera has no second stream), the low-quality stream is
  written to disk alongside the primary — Site D's array was writing ~205 Mbps of hi-res and
  ~17 Mbps of lo-res. The client estimates it with the same rule at "low" quality, the
  secondary's own resolution (320×320 to 960×432 on Site D) and the schedule's frame rate
  (640×480 at 12 fps H.265 ≈ 300 kbps; Site D measures ≈ 265 kbps per camera, so this is
  worst-case in the right direction) and reports it as `CameraStream.SecondaryRecordedKbps`.
  The Core model grew for this: `CameraStream.RecordedBitrateKbps` (main cap + secondary) is
  what the CLI and GUI totals sum, `PlanCamera.FixedKbps` / `PlannedCamera.FixedKbps` carry
  it into the planner, and `StorageEstimator.PlanUniform` spends the fixed part before
  splitting the budget and re-estimates from main + fixed. The table marks such cameras with
  `+` (CLI) or "4096 (+300)" (GUI). Hikvision and Dahua leave it null and nothing changes
  for them.
- **The number in the schedule is not necessarily the number on the camera.** Nx only pushes
  quality/fps/bitrate to a camera when the site setting "Allow Site to optimize device
  settings" (`cameraSettingsOptimization`, true on Site D) is on **and** the camera's Expert
  setting "Keep camera stream and profile settings" is off (`options.isControlEnabled` true —
  the live spelling; `controlEnabled` is accepted too). Otherwise the camera streams whatever
  its own profile says and the schedule's figure is fiction — which is why
  `SetMaxBitrateAsync` reads both first and **refuses with the reason** rather than PATCHing
  a value nobody will see. Reads do not refuse: the estimate still reflects the schedule, and
  the caveat belongs in the doc rather than in every row.
- **Per-camera age caps.** `schedule.maxArchiveDays` (and `maxArchivePeriodS`) prune a
  camera's footage past that age regardless of disk space; Nx stores a *disabled* cap as a
  negative number (Site D: −30 on most cameras, **31** enforced on some). A positive value
  becomes `CameraStream.ArchiveCapDays`, and the DAYS column reads "30.9 (cap 31)" so a
  camera holding less than the estimate promises explains itself. `minArchiveDays` (−1 =
  off) is not modelled.
- **Bitrate range** comes from `mediaCapabilities.streamCapabilities.primary` (`minBitrateKbps`
  192, `maxBitrateKbps` e.g. 10 666) — Nx's own bounds for the schedule slider on that camera;
  a camera without the block gets 192–65 536.
- **Oldest footage is two passes.** The exact list (`detailLevelMs=1`) is one object per
  continuous run — ~150 KB per motion-recorded camera, 10 MB for 64 cameras through the relay.
  So: a coarse pass merging anything closer than an hour (`detailLevelMs=3600000`, a few
  hundred bytes; verified to keep the same first start on Site D), then an exact pass over
  `[0, coarseStart)` only, because a coarse detail level also *drops* chunks shorter than
  itself and a lone old clip is exactly what must not be lost; an empty coarse pass falls
  back to the exact everything-window. The earliest `startTimeMs` is taken explicitly, not
  trusted to ordering. `limit=1` is not the shortcut: on 6.1 it answers `[]`.
- **Exports are Matroska.** `/media/{id}.mkv` is the one container the server streams without
  seeking back to finish an index, so a transfer cut short still plays; `--remux` turns it
  into MP4 like every other vendor's raw export. RTSP for third-party players (VLC, and the
  probe's DESCRIBE) needs **digest authentication enabled for the user** — off by default on
  Nx 5+ — so a DESCRIBE that answers 401 on an otherwise healthy server is a user setting,
  not a port problem.
- **Not implemented for Nx:** the Users tab (no `IUserManagementClient`; the tab shows "does
  not expose a user list"), SDK live view (there is no SDK), and `dvrtool live`.

## Recording mode (the schedule cells)

**Established 2026-09-02** on Site D. The same `schedule.tasks[]` that gives the bitrate also
says *when* and *how* a camera records, and `NxWitnessClient.BuildSchedule` turns it into the
shared `RecordingSchedule` (Core) behind the Storage tab's **Recording** column,
`dvrtool storage retention` and `dvrtool storage schedule`:

- `dayOfWeek` is **1–7 Monday–Sunday** (Qt's numbering), `startTime` / `endTime` seconds from
  midnight (86400 = 24:00); `isEnabled: false` is "Off" whatever the cells say; a `never` cell
  is white space, and a week of nothing but `never` reads "Off (nothing scheduled)".
- `recordingType` is the DW client's cell type: `always` → **Continuous**; `metadataOnly` →
  **Motion**, **Objects** or **Motion | Objects** by `metadataTypes` (`motion`, `objects`,
  `motion|objects`; absent or `none` means motion — an Nx 4.x cell); `metadataAndLowQuality` →
  the same **& low-res always**, because the secondary stream then records around the clock
  while the primary waits for the trigger (`RecordingTrigger.LowResContinuous`). The legacy
  `RT_*` spellings are accepted. The label says "&" and not "+" deliberately: `+` joins the
  modes that split a week in `RecordingSchedule.Summary`, and a mode name carrying its own `+`
  would make the mix unreadable — see the notation table in `hikvision-storage.md`, which also
  covers the `*` that marks a mode not running the whole week.
- Site D, live: 64 cameras — 16 continuous, 45 "Motion & low-res always", 3 "Motion", none
  mixed, none off — so the retention report's "48 of 64 enabled camera(s) record on events
  only" caveat is exactly why the worst-case estimate sits so far under the 68 days held.

## The write path (`SetMaxBitrateAsync`)

Resolve the channel (and refuse if the camera list moved), refuse if the camera keeps its own
profile or the site does not push settings, then GET the device document, set every recording
cell of `schedule.tasks[]` to `streamQuality: preset` and `bitrateKbps: N` — leaving "never"
cells and every other field untouched — PATCH `{"schedule": …}` back, GET again and return
the highest preset bitrate among the recording cells. Every recording cell is written on
purpose: an hour left at "high" would keep recording at Nx's computed rate and the retention
math would be wrong for that hour. The read-back is what the schedule holds; Nx does not
report what the camera did with it.

**Not fired live.** The gates are the house ones — `dvrtool storage plan` / `set` are dry-run
by default and take `--force`; the GUI Apply button re-verifies identity on a fresh client
and asks. The canary, on operator go-ahead only: one Site D camera whose schedule is already a
preset, 3072 → 3104 → 3072, confirming the PATCH is accepted, the read-back matches, and the
camera's actual stream (the Nx client's camera statistics) follows within a minute.

## Which tracks reach the disk: the secondary-stream and audio switches

`IRecordingOptionsClient` (Core `RecordingOptions.cs`, Nx side
`NxWitnessClient.RecordingOptions.cs`, CLI `dvrtool recording show | set`) covers the two
per-camera switches that decide *what* is written, as opposed to the schedule (when) and the
bitrate (how much). Verified live on Site D 2026-09-08 (6.1.1.42624), reads **and** the write.

- **Audio is TWO switches, in two different tabs and two different bags.**
  `options.isAudioEnabled` is the **General** tab's "Enable audio" — a typed bool, whether the
  server pulls audio at all, off unless somebody turned it on (all 64 Site D cameras read
  `false`). `parameters.dontRecordAudio` is the **Expert** tab's "Do not record audio" — a
  *property*, so absent on a default camera, exactly like `dontRecordSecondaryStream`. They are
  independent, and the second is the **durable** one: it keeps audio off the disk even if
  capture is later enabled, which is why it is worth setting on a camera whose capture is
  already off today. `CameraRecordingOptions.AudioReachesDisk` is the conjunction (capture on
  **and** bar clear); either switch alone keeps audio off. Do not assume the General switch is
  the whole story — that mistake was made here first, and only a diff of the device document
  before and after ticking the box in the DW client found the second property.
  Capability is separate again and numeric — `parameters.isAudioSupported` is `1`/`0` (52 of 64
  support audio), and `forcedIsAudioSupported` is the operator's override for a camera whose
  ONVIF answer was wrong, so it wins. The report says `off (none)` for a camera that has no
  audio to record, which is not the same as one that has audio switched off.
- **`dontRecordSecondaryStream` is a property, and absent on every default camera** — all 64 on
  Site D. Which bag a *write* has to land in is not documented and differs by build, so
  `SetRecordingOptionsAsync` writes `parameters` first (the strings `"1"`/`"0"`, which is what
  the DW client itself puts there), reads back, and falls back to `options` (a real bool) if the
  property did not stick — then remembers which bag worked for the life of the client, so a
  60-camera batch pays the discovery cost once. If neither sticks the change is reported
  **rejected**, never as a success. **Settled live 2026-09-08: `parameters` is the bag that
  takes it** on 6.1.1.42624 — the first attempt stuck on all 28 cameras written, and a fresh
  raw read shows the key present in `parameters` and absent from `options` on exactly those 28.
- **This is not `isDualStreamingDisabled`.** That one stops the server *pulling* the second
  stream; 63 of 64 Site D cameras carry `parameters.motionStream = "secondary"`, so disabling
  dual streaming would take motion detection with it. "Do not record the secondary stream"
  leaves the stream pulled and analysed and only keeps it off the disk. A camera with no
  second stream, or with dual streaming already off, reports `RecordSecondary = null` rather
  than `false` — there is nothing to archive, which is a different fact from being told not to.
- **The secondary stream is not spare capacity on every camera.** A camera whose schedule is
  `metadataAndLowQuality` ("Motion & low-res always") records the primary on motion and the
  **secondary continuously** — that low-res track is the only thing covering the gaps between
  motion events. Turning the secondary off there does not shrink the camera's footage, it makes
  the camera motion-only and leaves the quiet hours empty. `dvrtool recording set --secondary
  off` therefore **holds those cameras back by default**, lists them, and changes them only
  under `--include-lowres-always`. On an `always` or `metadataOnly` camera the secondary stream
  is pure overhead and turning it off is free.
- **The schedule mix moves.** Site D was read twice 18 minutes apart on 2026-09-08 and seven
  cameras had gone from `metadataAndLowQuality` to `always` in between (18 → 25 continuous),
  with no DW client running on the server itself — somebody was editing from a remote client
  while the reads were happening. Re-read the modes immediately before any batch that depends
  on them; a preview more than a few minutes old is not evidence.

### The first live write (2026-09-08)

`dvrtool recording set --secondary off --all --force` on Site D: **28 cameras changed, 0
failed, 36 held back by the low-res rule.** The 28 are the `always` (25) and `metadataOnly` (3)
cameras; every `metadataAndLowQuality` camera was left alone, which is the whole point of the
rule. Verified afterwards by a raw `/rest/v3/devices` read independent of the client:
`dontRecordSecondaryStream` present and true in `parameters` on exactly those 28, absent on the
other 36, `isAudioEnabled` false on all 64.

Worth about **64 GB/day** off the array (5.91 Mbps of measured secondary bitrate, summed from
`parameters.bitrateInfos.streams[encoderIndex=secondary].actualBitrate` across the 28) against
a total write load near 1 TB/day — so ~6% of the bytes. The **file-count** effect is the bigger
prize: Site D's RAID5 is IOPS-saturated with roughly 134 concurrent archive files, and this
removes 28 of them.

Audio needed no write at all: Nx's audio switch was already off on all 64.

### The audio bar, and how it was found (2026-09-08)

The first pass here concluded Nx had no "do not record audio" — wrong. `options.isAudioEnabled`
was the only audio key **present** on any of the 64 cameras, and the Expert-tab checkbox is a
property that is simply absent until set, so nothing in a read of the fleet revealed it. The
operator ticked the box on SiteD-Cashier in the DW client and a before/after diff of
`/rest/v3/devices` showed exactly one settings change: `parameters.dontRecordAudio` added, as
the number `1`.

The lesson generalises: **on this API, "the key is not in the document" is not evidence the
setting does not exist.** Every property-bag switch is absent by default. To discover one,
change it in the DW client and diff the device document — filtering out `bitrateInfos`,
`storageInfo`, `deviceAgentManifests`, `availableProfiles` and `status`, which churn on every
read.

`dvrtool recording set --record-audio off --all --force` then set it on the other 63:
**63 changed, 0 failed**, and a raw read shows `dontRecordAudio: "1"` on all 64 — the same
representation the DW client wrote, since the client now writes `"1"`/`"0"` rather than
`"true"`/`"false"`. (The 28 `dontRecordSecondaryStream` values written earlier that day are
stored as bool `true`; the server normalised the `"true"` string it was given. Both forms read
back correctly — `NxJson.Bool` takes bools, `"true"`/`"false"`, `"1"`/`"0"` and numbers — so
they were left alone rather than rewritten.)

The same diff caught something unrelated: SiteD-Cashier's schedule had moved from
`metadataTypes: motion` to `objects` on all seven days, from a remote client, during the same
window. Worth knowing that a diff of this document sees *everyone's* edits, not just yours.

## Open items

1. The canary write above, on explicit go-ahead. Note Site D has no preset cell today: the
   first `set --force` turns one camera's quality cells into presets, which is a visible
   change in the DW client's schedule grid — say so before firing it.
2. Live view and playback through the relay. The relay carries HTTP media (`/media/{id}.mp4`,
   `.mpegts`, HLS), so LibVLC could play those instead of RTSP — but they need the bearer
   token on the request, which LibVLC cannot add. Untested; not started.
3. `deviceType` values seen so far: `Camera`, `MultisensorCamera`. An I/O module has not
   been seen live; the `IOModule` exclusion follows the documentation.
