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
| Bitrate write | `PUT /ISAPI/Streaming/channels/{track}` | full-document round trip |

All answered 200 on every recorder probed. Both XML namespaces
(`hikvision.com/ver20` and `isapi.org/ver20`) appear across the fleet — parsers are
namespace-agnostic and tested under both, like everything else in the client.

## Traps and units

- **Capacity and free space are decimal megabytes** (10^6 bytes): a "8 TB" WD85PURZ
  reports `7630885`. Bitrates are kbps (1000 bit/s). `maxFrameRate` is fps×100
  (`2000` = 20.0 fps); some channels omit it entirely.
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

## The planner

`StorageEstimator.PlanUniform` (Core, pure math, no I/O): required total kbps =
capacity bits ÷ target seconds; split evenly; snap down to 32 kbps steps; clamp each
camera to its writable range (`/capabilities` min/max, defaulting 32–16384); then
**re-estimate from the clamped sum** so the reported days are what the plan actually
achieves. When camera minimums push the total over budget the plan says plainly that
the target is missed rather than quietly promising it.
