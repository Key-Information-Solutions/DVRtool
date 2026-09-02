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

The live target is Site D's DW Blackjack E-Rack `a DW Blackjack E-Rack` (DW Spectrum **6.1.1.42624**,
198.51.100.10:7001, 64 devices, one server; see the 2026-09-02 field notes). Verified on the
wire, anonymously:

- `GET /api/moduleInformation` — the verbatim reply is a fixture in `NxWitnessStorageTests`:
  `reply.id` `{11111111-…}` (the server GUID), `brand` `dwspectrum`, `customization`
  `digitalwatchdog`, `name` `TESTRACK1`, `systemName` `Site D`, `version` `6.1.1.42624`,
  `type` `Media Server`, `sslAllowed` true.
- `GET /rest/v3/system/info` — 64 device ids, one server id, `restApiVersions` v1–v4.
- `GET /rest/v3/devices` without a session → 401 (nothing else is anonymous).
- RTSP on the same port: `OPTIONS rtsp://198.51.100.10:7001/` → `RTSP/1.0 307 Temporary
  Redirect`, `Server: DW Spectrum/6.1.1.42624` — a well-formed RTSP status line, which is
  all the connectivity probe needs.
- TLS: self-signed, `O=Digital Watchdog, CN=DW Spectrum, C=US`, valid to 2027-10-01,
  TLS 1.2. The trust-on-first-use pin applies exactly as for Hikvision HTTPS.

**Not yet exercised live**: every authenticated call — login, the device list, storages,
`/api/storageSpace`, footage, the site settings — and the schedule PATCH. Their shapes follow
the Nx REST v3 documentation and the field notes; the parsers are deliberately tolerant
(numbers as strings, `reply` wrappers, braced ids, `mediaStreams` as an object or a string,
codec as an id or a name) so that the first live read is a confirmation rather than a
rewrite. Credentials for the Site D server must come from the operator; DVRTool has made **no
write** to it. See the checklist at the end.

## Endpoints

| What | Endpoint | Notes |
|---|---|---|
| Identity, model, version | `GET /api/moduleInformation` | anonymous; `reply.id` = server GUID → the serial pin; `brand` → "DW Spectrum" / "Nx Witness" / "Wisenet WAVE" |
| Login | `POST /rest/v3/login/sessions` | `{"username","password","setCookie":false}` → `token`; every later request carries `Authorization: Bearer <token>`; `DELETE /rest/v3/login/sessions/{token}` on dispose |
| Cameras | `GET /rest/v3/devices` | `id`, `name`, `physicalId`, `mac`, `url`, `serverId`, `deviceType`, `status`, `schedule`, `options`, `mediaStreams` |
| One camera | `GET /rest/v3/devices/{id}` | the read-modify-write source for the bitrate PATCH |
| Storage volumes | `GET /rest/v3/servers/{serverId}/storages` | `path`, `spaceLimitB` (reserve), `isUsedForWriting`, `isBackup`, `type`, `status` flags |
| Volume sizes | `GET /api/storageSpace` | legacy; `reply.storages[]` with `storageId`, `url`, `totalSpace`, `freeSpace`, `reservedSpace`, `isOnline` — byte counts **as strings**; optional (a 403 leaves sizes unknown, never invented) |
| Footage | `GET /rest/v3/devices/{id}/footage?startTimeMs=&endTimeMs=&detailLevelMs=1` | `[{startTimeMs, durationMs}]`, UTC ms; `durationMs` −1 = still recording |
| Site setting | `GET /rest/v3/system/settings` (v4: `/rest/v4/site/settings`) | `cameraSettingsOptimization` — "Allow Site to optimize device settings" |
| Bitrate write | `PATCH /rest/v3/devices/{id}` `{"schedule": {…}}` | every recording cell → `streamQuality: preset`, `bitrateKbps: N`; read back with GET |
| Export | `GET /media/{id}.mkv?pos=<ms>&endPos=<ms>` | Matroska, bearer-authenticated; `ExportNaming` names the raw file `.mkv` |
| Live / playback | `rtsp://host:7001/{id}?stream=0|1[&pos=<ms>&endPos=<ms>]` | same port as the API; `stream=1` is the secondary |

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
  free (Nx's own reserve), so `HddInfo.CapacityMB` is total − reserve and free space is free
  − reserve — which reads 0 on a full recorder, exactly like the appliance vendors. A backup
  volume duplicates footage and a volume that is not "used for writing" holds none: both
  are listed (Property "backup" / "not used for writing") with `RecordsFootage` false, so
  `StorageInfo.TotalCapacityMB` sums the recording pool only. No model or serial per volume;
  `status` flags map to ok / offline / checking / rebuilding, and a volume whose size could
  not be read says "size unknown" and is not counted.
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
  "highest" ≈ 5.8 Mbps. The MODE column reads MIN / LOW / NORM / HIGH / BEST for the
  qualities and KBPS for a preset; `FixedQuality` carries the level 0–4. A schedule that is
  disabled, or has only "never" cells, reports the camera as not enabled. A camera whose
  resolution Nx has not probed yet (`"*"`) gets no bitrate rather than a guess.
- **Nx archives the secondary stream too.** Unless `options.dontRecordSecondaryStream` (or
  `isDualStreamingDisabled`, or the camera has no second stream), the low-quality stream is
  written to disk alongside the primary — Site D's array was writing ~205 Mbps of hi-res and
  ~17 Mbps of lo-res. The client estimates it with the same rule at "low" quality, the
  secondary's own resolution and the schedule's frame rate (704×480 at 30 fps ≈ 650 kbps;
  Site D measures ≈ 265 kbps per camera, so this is worst-case in the right direction) and
  reports it as `CameraStream.SecondaryRecordedKbps`. The Core model grew for this:
  `CameraStream.RecordedBitrateKbps` (main cap + secondary) is what the CLI and GUI totals
  sum, `PlanCamera.FixedKbps` / `PlannedCamera.FixedKbps` carry it into the planner, and
  `StorageEstimator.PlanUniform` spends the fixed part before splitting the budget and
  re-estimates from main + fixed. The table marks such cameras with `+` (CLI) or
  "4096 (+650)" (GUI). Hikvision and Dahua leave it null and nothing changes for them.
- **The number in the schedule is not necessarily the number on the camera.** Nx only pushes
  quality/fps/bitrate to a camera when the site setting "Allow Site to optimize device
  settings" (`cameraSettingsOptimization`) is on **and** the camera's Expert setting "Keep
  camera stream and profile settings" is off (`options.controlEnabled` true). Otherwise the
  camera streams whatever its own profile says and the schedule's figure is fiction — which
  is why `SetMaxBitrateAsync` reads both first and **refuses with the reason** rather than
  PATCHing a value nobody will see. Reads do not refuse: the estimate still reflects the
  schedule, and the caveat belongs in the doc rather than in every row. Also unmodelled:
  per-camera `minArchivePeriodS` / `maxArchivePeriodS` (Min/Max archive days) prune footage
  regardless of disk space, so a camera capped at 7 days shows 7 in the oldest-footage column
  however generous the estimate.
- **Oldest footage** is `footage?startTimeMs=0&endTimeMs=<2100>&detailLevelMs=1`, taking the
  earliest `startTimeMs` explicitly rather than trusting order. `detailLevelMs` is left at 1
  on purpose: a larger detail level merges *and drops* chunks shorter than itself, and a lone
  old motion clip is exactly what must not be dropped. The reply is one entry per continuous
  run, which on a continuous archive is a handful of objects; on a motion-only camera it can
  be thousands, which is still fine. `limit=1` would make it one object — worth confirming
  live that the server keeps the *oldest* period under `limit`, then adopting it.
- **Exports are Matroska.** `/media/{id}.mkv` is the one container the server streams without
  seeking back to finish an index, so a transfer cut short still plays; `--remux` turns it
  into MP4 like every other vendor's raw export. RTSP for third-party players (VLC, and the
  probe's DESCRIBE) needs **digest authentication enabled for the user** — off by default on
  Nx 5+ — so a DESCRIBE that answers 401 on an otherwise healthy server is a user setting,
  not a port problem.
- **Not implemented for Nx:** the Users tab (no `IUserManagementClient`; the tab shows "does
  not expose a user list"), SDK live view (there is no SDK), and `dvrtool live`.

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

## Live-verification checklist (needs Site D credentials)

1. `dvrtool info --vendor nx --host 198.51.100.10 --user … ` — login, identity pin
   `11111111-2222-3333-4444-555555555555`, "DW Spectrum Media Server 6.1.1.42624".
2. `dvrtool channels` — 64 cameras, sorted by name; confirm `deviceType` values and that I/O
   modules (if any) are excluded.
3. `dvrtool storage disks` — confirm `/api/storageSpace` still exists on 6.1 and its numbers
   match the E-Rack (63.7 TiB RAID 5, ~375 GB free on 2026-09-02, ~10 % reserve); check
   whether v3 `storages[]` already carries size fields and, if so, prefer them.
4. `dvrtool storage retention` — confirm `mediaStreams` arrives as an object with integer
   codec ids, `options.controlEnabled` is the "Keep camera stream and profile settings"
   field (if the key differs, `NxCamera.Parse` is the one place to fix), `schedule.tasks`
   spellings, `fps` type, and that "Site D-Parking North West" (H.265, ~69 Mbps measured) comes
   out with a plausible worst-case. Compare the estimate with the measured 205 + 17 Mbps.
5. Oldest footage on a continuous camera and on a motion-only one; try `limit=1`.
6. `GET /rest/v3/system/settings` — confirm `cameraSettingsOptimization` is the key and is
   readable by the account used.
7. Only then, with explicit go-ahead: the canary write above.
