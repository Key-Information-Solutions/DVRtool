# Dahua storage, retention and the bitrate planner

**Established:** 2026-09-02. Read this before touching `DahuaClient.Storage.cs`, and read
`docs/hikvision-storage.md` first — the Core model (`Storage.cs`), the estimator, the CLI
(`dvrtool storage`) and the GUI Storage tab are shared, and the Hikvision doc explains why
retention is capacity ÷ configured max bitrate and never free space. This doc covers only
what Dahua does differently. Everything below was verified on the wire against one live
recorder: Site B, a **DH-NVR608H-128-4KS3/I** (device type N98A7N, firmware
4.000.0000000.6.R build 2024-07-21, 51 cameras bound of 128 channels, two disks). The
second saved Dahua unit (Site B2) rejects its saved credentials with 401 "Invalid
Authority!" and was not read.

## Endpoints

| What | Endpoint | Notes |
|---|---|---|
| Disk inventory | `GET /cgi-bin/storageDevice.cgi?action=getDeviceAllInfo` | required; `list.info[N]` (live) or `list[N]` (spec) |
| Disk names only | `GET /cgi-bin/storageDevice.cgi?action=factory.getCollect` | `list[0]=/dev/sda`; not used |
| Storage caps | `GET /cgi-bin/storage.cgi?action=getCaps` | `SupportMode[]=Group,Quota`, `RAID=true`; not used |
| Recording streams | `GET /cgi-bin/configManager.cgi?action=getConfig&name=Encode` | 0-based `table.Encode[ch].MainFormat[t]` |
| Record on/off | `GET /cgi-bin/configManager.cgi?action=getConfig&name=RecordMode` | `Mode` 0 auto, 1 manual, **2 stop** |
| Bitrate bounds | `GET /cgi-bin/encode.cgi?action=getConfigCaps&channel=N` | `caps[ch].MainFormat[0].Video.BitRateOptions=min,max` |
| Recording schedule | `GET /cgi-bin/configManager.cgi?action=getConfig&name=Record` | optional; `table.Record[ch].TimeSection[day][n]`; 375 KB on 128 channels |
| Oldest recording | `mediaFileFind.cgi` create → findFile → findNextFile count=1 → close/destroy | 1-based `condition.Channel` |
| Bitrate write | `GET /cgi-bin/configManager.cgi?action=setConfig&Encode[ch].MainFormat[t].Video.BitRate=N…` | several keys per call, answers `OK` |

Not available on this firmware (501 Not Implemented): `storageDevice.cgi?action=getCaps`,
`storageDevice.cgi?action=getDeviceInfo&name=…`, `LogicDeviceManager.cgi?action=getCameraState`.
`LogicDeviceManager.cgi?action=getCameraAll` works (camera[i].Channel 0-based, DeviceInfo
with the IPC's serial/address) but is not needed: the Encode table already lists only the
channels that have a camera bound.

## Traps and units

- **Disk sizes are bytes, as floats.** `TotalBytes=2495680086016.000000`. The parser
  accepts integers (the spec) and floats (the wire) and converts to the decimal megabytes
  the Core model carries, so a Dahua disk and a Hikvision disk read the same in every table.
  Each physical disk is split into partitions, `list.info[N].Detail[M]`, and the bay is the
  sum: Site B's two disks are four ReadWrite partitions each (~2.5 TB), ~9.9 TB per disk.
- **`UsedBytes == TotalBytes` on a healthy recorder** — the same permanently-full picture
  as Hikvision's `freeSpace=0`; retention comes from capacity ÷ bitrate.
- **No model, serial, or health per disk** over CGI on this firmware (`HealthDataFlag=0` is
  all it says); those columns are blank for Dahua. `State=Success` maps to `ok`, anything
  else is reported verbatim (lower-cased) and counted as unhealthy; a `Detail` with
  `IsError=true` forces `error`. Dahua does not keep ghost rows for removed disks, so
  "empty bay" never appears — the physical bay count comes from the spec sheet.
- **Work mode is not readable by a non-admin account.** `storage.cgi?action=getCaps` says
  the unit supports Group and Quota, but every config name that might hold the current
  mode (`StorageMode`, `StoragePolicy`, `RecordQuota`, …) answered 403
  `Authority:check failure.` for the saved account. The Storage tab shows no work mode for
  Dahua. That 403 body is a **permission** denial for one config, not a lockout — the
  lockout is a different reply (below).
- **`Encode[ch]` is 0-based and lists only bound channels.** The 128-channel Site B unit
  answers 51 `table.Encode[N]` rows. `MainFormat[0]` is the General (schedule) stream,
  `[1]` Motion, `[2]` Alarm; the recorder switches between them by what triggered the
  recording, so `CameraStream.MaxBitrateKbps` is the **highest of the three** (worst case,
  like everything in the estimator) and codec/resolution/fps/mode are described from `[0]`.
  `BitRate` is kbps; `BitRateControl` is `CBR`/`VBR`; `FPS` arrives as `12` or
  `30.000000`. `ExtraFormat[n]` are the sub streams and are ignored, exactly as Hikvision's
  x02/x03 tracks are.
- **`RecordMode[ch].Mode=2` means the channel records nothing** even with
  `VideoEnable=true`, so it is reported disabled and excluded from the totals. The table is
  optional — a unit that lacks it just reports every stream as it stands.
- **`getConfigCaps` ignores the channel parameter** on this NVR and returns `caps[N]` for
  every bound channel (0-based). Ranges differ per camera (`256,3584` on most Site B
  cameras, `1280,6144` on the 4K units), so the client reads its own row and falls back to
  `headMain.Video.BitRateOptions` (single-channel firmware) or a lone `caps[0]` row. The
  format is a literal `min,max` pair in kbps — not an option list.
- **`condition.Channel` is 1-based and `items[i].Channel` is 0-based.** `Channel=0` is
  rejected with 400 Bad Request; `Channel=1` answers display channel 1 with `Channel=0` in
  every item. The driver used to send display−1 (the python-amcrest convention) — that
  searched one channel low and returned nothing for channel 1 on this firmware. Fixed
  2026-09-02 in `SearchAsync` and the storage search alike.
- **400 Bad Request from `findFile` means "nothing to find"**: a window with no
  recordings, or a channel with no camera (`Channel=52` on the 51-camera unit). The
  driver returns an empty result / null, never an error.
- **`mediaFileFind` lists oldest-first**, across pages (2026-08-12 17:00 → 2026-08-16 on a
  100-item page), and `condition.Order` is silently ignored. The everything window
  (2000-01-01 → 2038-01-01) is accepted and answers in ~5 s the first time per channel
  and ~0.5 s afterwards, so the oldest recording is one finder round trip — no calendar
  fallback needed here, unlike Site E. Times are device-local wall clock, as everywhere.
- **Account lockout.** Roughly four bad logins lock the account for 1800 s and the recorder
  answers 403 with `{"ErrorCode":268632081,"Result":false,"RmLock":1800,"RmLogin":0}`;
  a plain wrong password is 401 "Invalid Authority!". The saved-device store keeps
  passwords DPAPI-protected (`ProtectedPassword`), so a probe script must decrypt them —
  reading the JSON naively sends a blank password and locks the customer out for half an
  hour (this is how 2026-09-02 started). Never retry a 401.

## Recording mode (the schedule)

**Established 2026-09-02** on Site B. `configManager.cgi?action=getConfig&name=Record`
answers, per channel, `table.Record[ch].TimeSection[day][n]="mask hh:mm:ss-hh:mm:ss"` plus
`Enable`, `Format`, `HolidayEnable`, `MaxRecordTime`, `PreRecord`, `Redundancy` and `Stream`.
`DahuaClient.ParseRecordSchedule` turns it into the shared `RecordingSchedule` (Core), which the
Storage tab's **Recording** column, `dvrtool storage retention` and `dvrtool storage schedule`
show. The read is optional (a refusal shows `?`), and the write path's read-back skips it — the
table is 375 KB for 128 channels.

- **`day` 0–6 is Sunday–Saturday** (the spec's words) and **row 7 is the holiday schedule**,
  which is not read. Six sections per day on this firmware (the spec allows 24); unused
  sections carry mask 0 and `00:00:00-24:00:00`; a whole day is written `00:00:00-23:59:59`,
  which the parser reads as 24:00.
- **The mask's bits are the record types** the web UI's checkboxes set, and any of them starts
  a recording: bit 0 regular → "Continuous", 1 motion, 2 alarm, 3 card, 4 intelligent →
  "Intel", 6 POS (all documented), and **bit 5 "MD&Alarm"** — the one type the UI offers that
  the doc leaves out, inferred from Site B's mask **39** = 1+2+4+32 being exactly the four
  classic checkboxes (General, Motion, Alarm, MD&Alarm) with Intel and POS unchecked. An
  unknown bit shows as "bit N" rather than disappearing. Site B reads
  `Continuous | Motion | Alarm | MD&Alarm` on every camera all week, which is why its estimate
  matches its real retention so closely (below).
- **`table.Record[ch].Enable` is false on every recording channel** and means nothing here. The
  switch is `RecordMode[ch].Mode`: 0 the schedule decides, **1 manual → "Continuous (manual)"**
  (the schedule is not consulted), 2 stop → "Off" (already excluded from the totals).
  `ModeExtra1` / `ModeExtra2` are the sub streams' modes (2 = stop on Site B) and are
  ignored, like the sub streams themselves.

## The write path (`SetMaxBitrateAsync`)

One `setConfig` with `Encode[ch].MainFormat[t].Video.BitRate=N` for every record type `t`
the channel has (General, Motion, Alarm) — capping only the schedule stream would leave
motion-triggered footage recording at the old rate. The recorder answers the bare string
`OK` (anything else is a rejection), then the Encode table is re-read and the highest of the
three bitrates is returned as the read-back. **Not yet fired live**: Site B is a customer
site and the session was scoped to reads; the CLI/GUI gates (`--force`, the Apply dialog)
are the same as for Hikvision. A canary on a Dahua unit we own — write 1280 → 1312 → 1280
on one camera and confirm the read-back both ways — is the open item before trusting it.

## What the numbers looked like

Site B, 2026-09-02: 51 cameras, 41 CBR / 10 VBR, 38 × 1280 + 7 × 2048 + 6 × 4096 kbps =
87.6 Mbps configured, ~19.8 TB installed → worst-case estimate ≈ 20.9 days; oldest
footage on channel 1 was 2026-08-12 17:00, i.e. ~21 days held. Mostly CBR, so the estimate
and reality agree almost exactly — on a VBR-heavy Dahua site expect reality to beat the
estimate, as on Hikvision.
