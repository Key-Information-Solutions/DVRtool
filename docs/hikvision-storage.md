# Hikvision storage, retention and the bitrate planner

**Established:** 2026-09-01. Read this before touching `Storage.cs` (Core),
`HikvisionClient.Storage.cs`, the GUI Storage tab (`MainWindow.Storage.cs`) or
`dvrtool storage`. Everything below was verified on the wire against three live
recorders: Site C (DS-9632NI-M8, V5.04.081), Site F (DS-7616NI-M2/16P) and
the lab recorder (DS-7716NI-I4/16P, where the write path was canary-tested and rolled back).

## Endpoints

| What | Endpoint | Notes |
|---|---|---|
| Disk inventory | `GET /ISAPI/ContentMgmt/Storage` | required; per-bay `<hdd>` entries |
| Firmware disk ceiling | `GET /ISAPI/ContentMgmt/Storage/capabilities` | optional (`TryGetXmlAsync`) |
| Recording streams | `GET /ISAPI/Streaming/channels` | all tracks; main = id `x01` |
| Bitrate bounds | `GET /ISAPI/Streaming/channels/{track}/capabilities` | optional; `min`/`max` attrs |
| Oldest recording | `POST /ISAPI/ContentMgmt/search` | `maxResults=1`, everything window |
| Recording schedule | `GET /ISAPI/ContentMgmt/record/tracks` | optional; the RaCM `TrackList`, one `Track` per stream, main = id `x01` |
| Bitrate write | `PUT /ISAPI/Streaming/channels/{track}` | full-document round trip |

All answered 200 on every recorder probed. Both XML namespaces
(`hikvision.com/ver20` and `isapi.org/ver20`) appear across the fleet — parsers are
namespace-agnostic and tested under both, like everything else in the client.

## Traps and units

- **Capacity and free space are decimal megabytes** (10^6 bytes): a "8 TB" WD85PURZ
  reports `7630885`. Bitrates are kbps (1000 bit/s). `maxFrameRate` is fps×100
  (`2000` = 20.0 fps). **`maxFrameRate` 0 is "Full Frame Rate", not missing:** the
  channel's `/ISAPI/Streaming/channels/{id}/capabilities` lists `0` as a legitimate
  option (`maxFrameRate opt="0,3000,2500,…,6"`) and the camera streams at the highest
  non-zero entry, which is resolution-aware (2688-wide → 3000, a 4096-wide channel tops
  out at 2000). `GetMainStreamsAsync` fetches the capabilities for zero-rate channels
  only and reports the resolved rate with `FrameRateIsFull` set ("30.0 (full)" in both
  front ends). The bitrate write round-trips the channel document unchanged, so the `0`
  is preserved and Apply never turns a full-rate camera into a fixed-rate one. Verified
  on the lab recorder 2026-09-02, where 6 of 9 cameras are set to full rate.
- **Free space is permanently 0 on a healthy recorder.** Overwrite mode keeps the
  disks full forever, so retention math uses **total capacity ÷ configured max
  bitrates**, never free space. A nonzero free space usually just means a recently
  formatted disk.
- **`status=notexist` rows are ghosts, not disks.** The firmware keeps a row (with
  the removed drive's model and serial) for every bay it has ever seen a disk in.
  Display them as empty bays; never count them in capacity or "installed".
- **The capabilities `hddList size` is the firmware ceiling, not the chassis.** The
  8-bay DS-9632NI-M8 reports `size="16"`. Word it "firmware supports up to N";
  physical bay count comes from the model's spec sheet.
- **Recording search returns matches oldest-first**, which is what makes the
  single-result oldest-recording probe one cheap POST per camera. Firmware quirk:
  with `maxResults=1` the reply's `numOfMatches` echoes the *total* (e.g. 276) while
  `matchList` holds one item — parse the items, ignore the counts.
- **Times are device-local wall clock** dressed as UTC (`Z` suffix) — same lie as
  everywhere else in ISAPI; days-held math compares against operator-local now,
  which is honest to within a timezone across a Florida fleet.
- **Estimates are worst-case by design.** Every camera at its configured cap around
  the clock. VBR + smart codecs do better, so real retention lands at or above the
  estimate — the safe direction for SLA/insurance answers. Validated: Site C's
  30.52 TB ÷ 171 Mbps of caps estimated 16.5 days; the disks actually held 24.1.
  Per-camera oldest is worth reading: one Site C camera held 15.1 days while the
  rest held 24 — a per-system number would have hidden it.

## Firmware that cannot search the everything window (Site E)

**Found 2026-09-02** on Site E, a DS-7716NI-I4/16P on V4.61.030 build 240123. the lab recorder's
DS-7716NI-I4/16P(B) runs the same V4.61.030 and searches the everything window fine, so this
is a property of the unit (its index, disks or quota layout), not of the firmware version. Probed with ~150 hand-built
`POST /ISAPI/ContentMgmt/search` requests over digest auth:

- The 2000→2038 window is rejected **every time**, in ~0.16 s, HTTP 500
  `deviceError` with `subStatusString` "Tag 13 is invalid (two root tags)" (the tag
  number is one past the last element of our body — "Tag 11" without the
  `metadataList` — so the message is nonsense, not a parse error in our XML). Any
  start earlier than roughly 2023 is rejected the same way; an end of 2040 too (32-bit
  time), 2038-01-01 is fine.
- Windows that make the device enumerate thousands of segments (a year on a busy
  channel; `numOfMatches` caps at 4000) take ~0.3–0.6 s and fail intermittently —
  identical back-to-back requests alternate 200/500, roughly half fail, and on the
  busiest channel they failed 6 of 6. Reusing the `searchID`, spacing requests out
  to 8 s, paging a "MORE" search past its end and Basic auth changed nothing.
- Narrow windows are reliable: one day, one month, or anything with a small match
  count answered 200 in 0.13 s on every attempt (30+), including windows with zero
  matches back to 2024.
- The web UI's playback calendar,
  `POST /ISAPI/ContentMgmt/record/tracks/{track}/dailyDistribution` with
  `<trackDailyParam><year/><monthOfYear/></trackDailyParam>`, answers
  `<trackDailyDistribution><dayList><day><dayOfMonth/><record>true|false</record>…`
  in ~0.1 s, deterministically, for any month (2000 included, 200 with no days).

So `FindOldestRecordingAsync` keeps the one-POST everything window as the primary path
and, on any non-401 HTTP failure, **falls back to the calendar**: walk months backwards
from today, remember the earliest recorded day, stop after six empty months past it (or
36 empty months with nothing found → null), then search **that one day** with
`maxResults=1` (one retry on a 5xx) for the exact first segment. If the calendar says
the day recorded but the day search returns nothing, the day's midnight is reported.
401 never falls back (each retry burns a lockout attempt). Verified live on Site E:
channel 1 → 2025-12-31 11:10:42, matching the hand probe.

Also noted on this recorder: `workMode` is **quota**, not group. The retention
estimate here assumes one shared overwrite pool; under quota mode each camera is capped
at its own slice, so the per-camera oldest column is the honest number on such a unit
and the system-wide estimate is optimistic.

## Recording mode (the schedule)

**Established 2026-09-02** on the lab recorder, Site C, Site F and Site E. The Storage tab's
**Recording** column (tooltip: the week laid out and what is in effect now), the RECORDING
column of `dvrtool storage retention` and the whole of `dvrtool storage schedule` come from
`GET /ISAPI/ContentMgmt/record/tracks` — the RaCM `TrackList`, one `<Track>` per stream (x01
main, x02 sub, x03 third; only x01 is read), fetched once per `GetMainStreamsAsync` (145 KB for
16 channels) and parsed by `HikvisionClient.ParseTrackSchedules` into the shared
`RecordingSchedule` model (`RecordingSchedule.cs` in Core, carried as `CameraStream.Schedule`).

- Each `ScheduleAction` has a start and an end **day + time of day**, and the recorders write a
  whole day as `Monday 00:00:00 → Tuesday 00:00:00`; Sunday ends on `Monday 00:00:00`, which
  `RecordingSchedule.SpansBetween` reads as the end of the week, not as seven days. The RaCM
  spec's own example splits a day (`Monday 00:00 → Monday 08:00` EDR, then CMR), so both shapes
  are handled and a range that crosses midnight is split per day.
- `Actions/ActionRecordingMode` is the type: `CMR` (Continuous), `MOTION`, `ALARM`, `EDR` or
  `ALARMORMOTION` ("Motion | Alarm"), `ALARMANDMOTION` ("Motion & Alarm"), plus the event
  words newer firmware adds (`AllEvent`, `FieldDetection`, `LineDetection`, `facedetection`,
  `pir`, `POS`, …) — `MapRecordingMode` labels the ones we know and passes an unknown word
  through verbatim rather than dropping it. Across the four recorders only `CMR` and `MOTION`
  occur. An action with `Actions/Record=false` is not a recording span.
- **The on/off switch is `enableSchedule`** in `CustomExtensionList/CustomExtension`
  (`www.hikvision.com/RaCM/trackExt/ver10`), not the track's `<Enable>` — that element reads
  `false` on every recording track of all four units and means nothing here. A main track with
  `enableSchedule=false`, or with an enabled but **empty** `ScheduleBlock`, records nothing: it
  reads "Off" / "Off (nothing scheduled)", its `CameraStream.Enabled` is false and it leaves
  the retention total. Third-stream tracks show both shapes routinely (Site F:
  `enableSchedule=false`; Site E: enabled and empty).
- `HolidaySchedule` (in the same extension; an empty block everywhere probed) is not read;
  `ScheduleDSTEnable` and `DefaultRecordingMode` (always `CMR`) are ignored — the latter is
  only the fallback for an action that names no mode.
- When the endpoint is refused the schedule is **unknown**: the column shows `?` and
  `Enabled` stays what the stream settings say — never "records nothing".

What the fleet looks like: the lab recorder 9/9 continuous; Site C 48 tracks all CMR; Site F all
CMR with its unused third streams switched off; **Site E records 13 of 14 cameras on
motion** (BDC alone is continuous) — which is why its worst-case estimate sits far under the
days it actually holds, and why the retention report now says so: "13 of 14 enabled camera(s)
record on events only … they will hold more than it says."

## The write path (`SetMaxBitrateAsync`)

GET the channel document, set `vbrUpperCap` **and** `constantBitRate` where present
(VBR records under the former, CBR under the latter; writing both keeps the channel
consistent whichever mode it is in), PUT the whole document back, check the
`ResponseStatus` (`statusCode` 1 = OK — a rejection can arrive under HTTP 200), then
**GET again and return the read-back value**. NVR-managed cameras may snap the value
to their own steps, so callers report what stuck, not what was asked. Canary-verified
on the lab recorder ch4: 3072 → 3104 → 3072, read-back exact both ways.

Writes are gated the house way: `dvrtool storage plan`/`set` are dry-run by default,
`--force` applies, an explicit `--dry-run` wins over `--force`. The GUI's Apply
button is the one deliberate exception to "GUI writes stay in the CLI" (door access):
a bitrate change is reversible from the same tab, and it still re-verifies device
identity on a fresh client (`DeviceIdentityGuard.Ensure`) and confirms via dialog
before the first PUT.

## Recording-mode notation (all vendors)

**Established 2026-09-03.** `RecordingSchedule.Summary` is the one string the Recording column,
`retention`'s RECORDING column and `storage schedule` all print, and it uses the same three
marks on Hikvision, Dahua and Nx (`RecordingSchedule.Notation` is the legend, printed under each
of those tables so it never has to be remembered):

| Mark | Means | Example |
| --- | --- | --- |
| `\|` | triggers sharing one span | `Motion \| Alarm` — one Hikvision `ALARMORMOTION` action |
| `+` | modes splitting the week | `Continuous* + Motion*` |
| `*` | **that mode does not run the whole week** | `Continuous*` |

The star is the point of the whole scheme. A fleet is normally 24/7, so the interesting camera
is the one that is *nearly* normal — set to continuous like the others, with a hole in its
week — and the previous wording ("Continuous (50 h/wk)", "Motion 118h, Continuous 50h") buried
that in arithmetic. `Continuous` now means 24/7 and nothing else does. It is a mark rather than
more words because **"Continuous + Motion" is a plausible-looking mode name in its own right**
(newer firmware does mix types per span), and an operator must never have to work out which
reading is meant; with the stars, `Continuous* + Motion*` cannot be read as a single mode.

- Starring is per mode, decided by the **union** of that mode's own spans, not their sum: a
  recorder that writes one day as two adjacent entries still reads `Continuous`, and one that
  answers with overlapping entries cannot inflate its way past 168 h and lose the star.
- Because it is per mode, every mode in a mix is starred — a week split between continuous and
  motion has no mode that runs all week, even though *something* records at every hour.
- "Nothing records at all" is therefore a **separate** question: `HasDeadTime` /
  `DeadTime`, surfaced as "nothing records for 6 h/wk" in `HoursText` (the GUI's Recording
  tooltip, and a line under each camera in `storage schedule`) and counted in both front ends'
  summaries. Dead air is footage nobody has; a mode boundary is not.
- The hours did not disappear, they moved: `HoursText` per camera, `DescribeWeek` for the week
  day by day.
- Nx's combined cell is labelled **"Motion & low-res always"**, not "+ low-res always" — a mode
  name carrying its own `+` would make the mix separator unreadable.

Live on the lab recorder 2026-09-03: 9/9 `Continuous`, no stars — the ordinary case reads as ordinary.

## The planner

`StorageEstimator.PlanUniform` (Core, pure math, no I/O): required total kbps =
capacity bits ÷ target seconds; take off the fixed part (an Nx secondary stream) and the
**pinned** cameras; split what is left evenly across the cameras the planner is free to decide
for; snap down to 32 kbps steps; clamp each camera to its writable range (`/capabilities`
min/max, defaulting 32–16384); then **re-estimate from the clamped sum** so the reported days
are what the plan actually achieves. When camera minimums or pinned rates push the total over
budget the plan says plainly that the target is missed rather than quietly promising it.

### Pinned cameras

**Added 2026-09-03**, live-verified on the lab recorder the same day. A uniform plan is the wrong answer
for a site where one camera watches a licence plate and another was set to 8 Mbps because an
insurer asked — so `ChannelPinStore` (`ChannelPins.cs` in Core,
`%APPDATA%\DVRTool\channel-pins.json`, keyed by `host:port` beside `pins.json` and
`identities.json`) records the cameras the planner may not decide for. GUI: select rows in the
Storage tab and **Pin** / **Unpin** / **Clear all pins**, with a Pin column and the pin summary
under the planner. CLI: `dvrtool storage pin [--channel n [--kbps k] [--reason text]] [--unpin]
[--clear]`, plus `plan --ignore-pins` and `set --pin`.

- A pin either **names a rate** (held, and written if the camera has drifted off it) or is
  **"keep current"** (`kbps: null`, resolved against what the device reports at plan time).
  Two different promises: the number survives someone changing the camera by hand.
- Pinned rates come off the budget **before** the split, exactly like Nx secondary streams, and
  a pin above what the camera accepts is clamped and flagged — only what the camera will really
  record is spent. Live: pinning Front Door at 16,384 took the other eight cameras from 3,904
  to 2,368 kbps and still made 30.0 days.
- A pin is a promise about a **camera**, but it can only be stored against a channel number —
  and on Nx a channel number is positional (the client numbers the camera list sorted by name),
  so adding a camera shifts every number after it. The camera's **name** is therefore recorded
  with the pin and checked: a channel now answering to a different name is **reported, not
  applied**. A rename costs one re-pin; a renumber would otherwise cost a retention commitment,
  discovered months later. Either name unknown (a recorder that will not list its channels)
  skips the check rather than guessing.
- The device's **serial** is recorded too, and pins recorded against another serial at the same
  address are carried but never applied ("clear them if the recorder was replaced").
- `BitratePlan.MissReason` names the pins **before** "camera minimums": an operator reading
  "minimums keep the total up" on a system they over-pinned themselves would go price disks
  for their own decision. Live at `--days 200`: "the 1 pinned camera(s) alone want 16,384 kbps,
  and 200.0 days needs the whole system under 5,299 kbps — the 8 unpinned camera(s) were
  floored at 32 kbps and it still does not fit."
- Failures here are **loud**, the opposite of the identity store's write policy: an unreadable
  or corrupt pin file propagates (`InvalidDataException` naming the file) and the planner
  refuses, and a save error is reported instead of swallowed. Silently reporting "nothing is
  pinned" would let the next plan overwrite every pinned camera — the one outcome a pin exists
  to prevent. `--ignore-pins` is a dry-run view for the same reason and refuses `--force`.
- Pinning writes nothing to the recorder, and changing pins discards any previewed plan (it was
  costed against the old ones).
