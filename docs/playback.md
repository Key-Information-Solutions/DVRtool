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
clip for export (a drag under 4 px is a click), wheel zooms about the cursor,
right-drag pans. Footage colour follows `RecordingType` — continuous blue,
event-triggered amber, mixed/unknown green. One trap, found 2026-09-11 after the drag had
been silently a click since the tab shipped: **`ReleaseMouseCapture` raises
`LostMouseCapture` synchronously**, and that handler resets the gesture flags — so the
release handler must read "was this a drag" *before* it releases capture, or every drag
ends as a click and a seek. The clip can also be **typed**: the Clip boxes on the
toolbar take `HH:mm` / `HH:mm:ss` on the day shown (`24:00` = midnight after it) and mirror
the dragged selection; `ClipRange` in Core parses and formats them (tested), refuses a
reversed pair rather than swapping it, and Export commits whatever is in the boxes first.

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

### One run of footage per body: LibVLC's clock is the demuxer's

`MediaPlayer.Time` is **where the demuxer has read to, not what is on screen**. The body
arrives far faster than real time, so the demuxer is only held back by the playback clock
— and a timestamp discontinuity resets that clock. Ask a Hikvision recorder for a window
spanning several motion clips and it concatenates them with the gaps removed; the
demuxer then reads across each gap in no time while the picture is still on the first
clip. Measured on Site E's motion-only stairway (channel 1, 2026-09-04): opened at
12:28:41, the clock read 12:40:58 one second later and 13:33:05 a second after that. The
first cut of the tab compared that clock with the footage map and "skipped" to the next
clip — after a few frames of each, which is what made the tab useless on a motion camera.

So a body is requested for **one run of footage** (`FootageCoverage.SpanAt`, seams under
2 s joined), ending at the run's end, and the recorder's own end-of-stream carries
playback to the next run (`EndReached`). Inside one run the clock is real time; the
playhead is clamped to the requested end so read-ahead cannot show it past the run; and a
demuxer clock more than 3 s past the requested end with no end event is treated as the
end, so a recorder that never signals one cannot freeze the tab. When a vendor ceiling
cut the body short of the run (Dahua's 6 h) the continuation resumes at the body's
requested end rather than the run's, so a long continuous day plays through — but only
when the body actually reached that end. A body that stopped **early** resumes where the
picture got to (`PlaybackResumePlan` in Core, tested), because resuming a cut-short body
at the end of its request skips every minute it never delivered. Re-measured
after the change: each clip played at 1× and handed off to the next. Note the clips are
**shorter than the search says**: the 12:28:41→12:29:10 segment (29 s) is a 22.4 s body
and the 12:33:45→12:34:41 one (56 s) is 51.8 s, measured with ffmpeg on the exact
download — the search's end time carries post-record padding the file does not. The tab
plays what the recorder holds and moves on when it ends, which is right.

### The body must be paced, or the picture skips forward

**A body LibVLC believes is live is a body it races through.** The symptom is footage
that keeps jumping forward while it plays, on a camera whose recording is continuous —
reported on the lab recorder's channel 2, 2026-09-10, and reproduced exactly.

libvlc decides whether it may control an input's pace from **one thing: whether a seek
callback was registered**. LibVLCSharp's `StreamMediaInput` sets `MediaInput.CanSeek`
from `Stream.CanSeek`, so a forward-only HTTP body registers none — and libvlc then
treats the input as a live source that paces itself, reads it flat out, and slaves its
clock to the arrival rate. A playback body arrives about seventy times faster than real
time, so the clock runs seventy times too fast, every picture is late the moment it is
decoded, and the video output shows the handful that land.

Measured on channel 2 (4096×1840 HEVC, 10 fps, ~1.9 Mbps), body 06:00:00 → 07:47:04:

| input | read in 30 s | decoded | lost | clock |
|---|---|---|---|---|
| `StreamMediaInput` (forward-only) | **1279.8 MB** of a 1492 MB body | 42117 | 615 | pictures 135 s late |
| seekable — the same bytes from a file | 4.3 MB | 403 | **0** | 1.00× |
| `PlaybackMediaInput` (seek registered, refused) | 6.3 MB | 605 | **0** | 0.99× |

Over 90 s the last row is 18.9 MB, 1.00×, zero lost, zero late. So the fix is
`PlaybackMediaInput`: register the seek callback and refuse every seek. That says the
true thing about an HTTP body — it cannot be seeked, but it certainly can be paced,
because a reader that stops reading fills the socket and the recorder waits. It also
makes the speed buttons real: at 4× libvlc now pulls four times the bytes and the clock
runs at 3.97×, where before it was already reading as fast as the network allowed and
had nothing left to give.

The cost is that a demuxer which genuinely needs a seek now gives up on the media
instead of limping on — a loud failure, no picture at all. It does not arise for the
bodies we play, and the one case that provokes it is instructive: leave Hikvision's
64-byte IMKH envelope on the front and libvlc seeks looking for the pack header, is
refused, and shows nothing. That envelope is already dropped (above), which is why the
program stream needs no seek at all.

Ruled out along the way, on the same body: `avcodec-threads`, the audio track, the
reported input size, and the read chunk size all make no difference — the racing is
identical with and without each. The stream itself is clean: the full 1h47m body
downloads complete, ffmpeg reads it end to end, and the pack SCRs and PTSs step
uniformly by 0.1 s with no discontinuity across the recorder's own ~80-minute file
boundaries. **This is not a container problem and never was.**

Live video keeps `StreamMediaInput`, because live really is live: it arrives in real
time and libvlc's live handling is the right one.

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
