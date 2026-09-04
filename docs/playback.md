# Recorded playback over the web port

**Established:** 2026-09-04. Read this before touching `Playback.cs` or
`ContainerPipe.cs` (Core), the three `*Client.Playback.cs` faces, the GUI's
`MainWindow.Playback.cs` / `TimelineControl.cs`, or `dvrtool footage`. Everything
below was checked on the wire against Site C (Hikvision DS-9632NI-M8, web port 80),
Site B (Dahua DH-NVR608H-128-4KS3/I, web port 80) and Site D (DW Spectrum,
through the DW Cloud relay on 443).

## Why not RTSP

The obvious playback transport is the RTSP playback-by-time URI every vendor has and
`GetPlaybackUri` already builds. It needs the RTSP port forwarded, and across the
fleet it is not: the web port is forwarded everywhere (it is how the recorder is
administered), the Hikvision SDK port at most sites (iVMS-4200 needs it), RTSP at a
handful. The DW Cloud relay carries no RTSP at all. So the Playback tab's RTSP
"Play selected" button worked in the office and nowhere else.

The transport that does work everywhere is the **export body**. Every vendor streams
a time range over its web port for `dvrtool download`; handed to LibVLC through a
`StreamMediaInput` instead of written to a file, that body plays at the footage's own
pace — the same way the Live tab's SDK route feeds LibVLC a program stream. A seek is
a fresh request from a new time; a pause stops the decoder reading, which fills the
socket and makes the recorder wait. Nothing seeks inside the body.

The Hikvision SDK's `NET_DVR_PlayBackByTime_V40` remains the natural second
transport (native pause/speed/seek over port 8000) and is approved for sites where the
web-port route fails; it is not built.

## Per vendor

| Vendor | Body | Container | Notes |
|---|---|---|---|
| Hikvision | `GET`/`POST /ISAPI/ContentMgmt/download` with a time-window `playbackURI` (`/Streaming/tracks/{ch}0{stream+1}?starttime=&endtime=`) | MPEG-PS | **64-byte envelope first** — see below. Whole-day window accepted. 2 MB in 0.5 s on Site C. |
| Dahua | `GET /cgi-bin/loadfile.cgi?action=startLoad&channel=&startTime=&endTime=&subtype=` | DHAV | **Max 6 h per request** — see below. LibVLC cannot demux DHAV: goes through ffmpeg. |
| Nx / DW | `GET /media/{id}.mkv?pos=&endPos=` | Matroska | Works through the relay. 2 MB in 2.1 s on Site D via relay. Stream parameter ignored — Nx archives the primary. |

`IPlaybackClient.OpenPlaybackAsync` returns a `PlaybackStream` (body + container +
requested window). Each face **peeks the first 16 KB** (`PrefixedStream.PeekAsync`)
so an accepted request that sends nothing — no footage, a channel with no camera — is
an `NvrException` up front rather than a decoder that sits on black. Hikvision's peek
also decides the GET-then-POST fallback the export already does, and the export now
shares that code (`OpenDownloadAsync`).

### Hikvision: the IMKH envelope

The download body does **not** start with a pack header. Site C sends 64 bytes of its
own first — `00 00 00 40 00 00 00 13 … "IMKH" …` — and the first `00 00 01 BA` is at
offset 64. ffmpeg scans past that, which is why exports remux fine and why nobody
noticed; VLC's PS demuxer probes the first bytes and refuses anything that is not a
start code. `HikvisionClient.Playback` therefore `SkipTo`s the first pack header in the
peeked prefix. This is the same family of fact as "Hikvision downloads are mislabeled
MPEG-PS" and the SDK live path's `avcodec-threads=1`; every Hikvision playback media
gets the live decode options too.

### Dahua: six hours, and ffmpeg

`loadfile.cgi` on Site B answers a 6 h window with footage and an 8 h window
with `400 Bad Request` — whether or not it crosses midnight (10:00→23:59:59 same day is
refused, 23:00→01:00 accepted), so it is a length ceiling, somewhere between 6 h and
8 h. `DahuaClient.MaxLoadfileWindow` is the verified 6 h; the GUI asks again from where
the body ended. **The export path has the same ceiling and does not chunk** — a
`dvrtool download` longer than ~6 h on Dahua will be refused; that is a separate fix.
Percent-encoding the colons in the times (`%3A`) is fine; that was a red herring while
finding this.

The shipped LibVLC (VideoLAN.LibVLC.Windows 3.0.x) has **no avformat plugin at all** —
`plugins/demux` holds VLC's own PS/TS/MKV/MP4 demuxers and nothing that reads DHAV. So
Dahua playback runs `ffmpeg -f dhav -i pipe:0 -c:v copy -an -f mpegts pipe:1`
(`ContainerPipe`) between the body and the decoder: a stream copy, audio dropped
(Dahua records G.711, which MPEG-TS cannot carry usefully). Back-pressure runs the
whole way — decoder → ffmpeg stdout → ffmpeg stdin → HTTP socket — so pause still
pauses the download. ffmpeg on PATH is already a requirement of the export's remux.

## The timeline

`TimelineWindow` (Core) is the geometry — pixel↔time, zoom levels 24 h … 5 min about
an anchor, pan, both clamped to one calendar day, tick spacing chosen from round clock
steps so labels never collide. `FootageCoverage` merges the day's `RecordingSegment`s
into runs (seams under 2 s are joined: recorders split continuous days into files) and
answers `NextFootageAt`, which is how a click in a gap becomes a request that starts at
footage. `PlaybackClock` is anchor + the decoder's media time; the anchor is the
**snapped** request time, because the recorder clips a request to what exists and a
clock anchored in a gap would drift by the gap. All of it is in
`PlaybackTimelineTests`.

`TimelineControl` (App) only paints and translates mouse: click seeks, drag selects a
range for export (a drag under 4 px is a click), wheel zooms about the cursor,
right-drag pans. Footage colour follows `RecordingType` — continuous blue,
event-triggered amber, mixed/unknown green.

The tab loads **one day** per camera (`SearchAsync` over the day, merged), plus the
month's recorded days for the date row (`GetRecordedDaysAsync`: Hikvision
`dailyDistribution`, Nx footage at 1 h detail, Dahua one `mediaFileFind` over the month
bucketed). When a body ends (`EndReached`) or the playhead runs off known footage, the
next run is requested automatically; when the day's footage ends, playback stops and
says so. Speed is `MediaPlayer.SetRate`; pause is `SetPause`, which pauses the download.

## Verification

`dvrtool footage --channel N [--month yyyy-MM] [--probe "yyyy-MM-dd HH:mm"]` lists the
recorded days and, with `--probe`, opens exactly the body the GUI plays for a few
seconds and reports the container it sniffed — and for Dahua, whether ffmpeg produced
MPEG-TS from it. 2026-09-04: Site C → `MPEG program stream (000001BA…)` after the skip;
Site D via relay → `Matroska (1A45DFA3…)`; Site B → `DHAV (44484156…)` raw, and
526 KB of MPEG-TS with the sync byte out of ffmpeg in the same 8 s.

### The clock against the picture

The tab's clock is the requested start plus the decoder's media time, so it can run
ahead of the timestamp burned into the picture. Seen on 2026-09-04: the lab recorder (Hikvision,
LAN) showed a camera OSD of 07:00:23 against a clock of 07:00:35 — the recorder starts
the body at the keyframe before the requested time, and opening took a second or two;
Site D (Nx via relay) showed an OSD of 07:03:07 against 07:00:39, which is too large
for a GOP and is most likely the camera's own clock disagreeing with the server's (the
OSD is the camera's; the archive is indexed by the server's). The clock is therefore
"where the recorder was asked to play from, plus how long it has played", not a reading
of the stream; a wall-clock track parsed out of the body would fix the first and not the
second. GUI verification the same day: the lab recorder, Site B (through ffmpeg) and Site D
(through the relay) all played from a timeline click; pause held the clock, resume moved
it, a click past the last footage was refused with the time named.

## Not done

- Hikvision SDK playback (`NET_DVR_PlayBackByTime_V40`) — approved, unbuilt.
- Dahua export chunking past the 6 h ceiling.
- Multi-camera synchronized playback; thumbnails on the timeline; audio on Dahua.
