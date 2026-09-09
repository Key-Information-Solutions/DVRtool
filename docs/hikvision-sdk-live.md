# Live video over the SDK port — Hikvision

**Established:** 2026-08-24. Built because RTSP is the port sites do not forward, and the
SDK port is the port they do.

This doc records what was probed, why the SDK route was chosen over the two alternatives,
and the traps in the implementation. Read it before touching
`src/DVRTool.Vendors.HikvisionSdk`.

## 0. TL;DR

- **Live view no longer requires RTSP on Hikvision.** `NET_DVR_RealPlay_V40` with
  `dwLinkMode = 0` brings the media back over the same TCP session the login authenticated
  on, so nothing but the SDK port needs to be open. This is what iVMS-4200 does.
- **On our fleet that is not a fallback, it is the primary route.** RTSP was reachable at
  **3 of 17** sites; the SDK port at **14 of 17** — and the three misses were port-forward
  gaps, not device limitations.
- **The HTTP alternative does not exist on this hardware.** `httpPreview` was probed on all
  17 recorders — 10 models, 12 firmware builds, V3.1.18 through V5.04.081 — and answered
  **403 on every single one**. It is not a firmware gamble; stop designing around it.
- **Two surfaces:** the desktop app's Live tab has a transport dropdown (`RTSP <port>` /
  `SDK <port>`), and the CLI has `dvrtool live`, which records to a file. The Live tab also
  has a **Grid** mode — every camera of the system at once on sub streams, 16 per page,
  double-click for the main stream (§7), a **fullscreen** toggle and a **wheel zoom** (§8).
- **The wheel zoom is a crop, and VLC's documented crop syntax is wrong.** LibVLC 3.0.21
  parses the documented `WxH+X+Y` window form and then applies it as though it were the
  four-sided *border* form, so a real zoom computes a negative visible size and paints the
  pane black. DVRTool emits `left+top+right+bottom` (§8). Measured, not guessed.
- **Every live media needs `:avcodec-threads=1`** or a 20 fps stream shows its first frame
  and then nothing (§3, "LibVLC drops every frame after the first"). The bytes are fine; it
  is LibVLC's decoder latency against Hikvision's zero-lead timestamps. Applied by
  `MainWindow.AddLiveDecodeOptions` to SDK and RTSP media alike.
- **One TCP connection per preview**, plus one for the login. A 16-tile grid is 17
  connections to the SDK port, which is exactly what iVMS-4200 does (21 were observed from
  one iVMS grid). The SDK side of 16 streams costs nothing measurable; the viewer side does
  (§7).
- **Display channel ≠ SDK channel.** An NVR's first IP camera is display channel 1 and
  **device channel 33**. Getting this wrong shows no error — just no video, or on a hybrid
  DVR, a different camera. `SdkChannelMap` reads the mapping off the login response.
- **The serial the SDK returns names the same device as ISAPI's**, which is what lets an SDK
  login be checked against the same identity pin an HTTP login created — but on M-series
  firmware it drops a hyphen ISAPI includes, so the pin comparison strips hyphens (§3,
  "Identity"). Verified live.
- **Hikvision and OEM rebrands only**, Windows x64 only, and it needs `HCNetSDK.dll`.

## 1. Why not the other two routes

### HTTP (`/ISAPI/Streaming/channels/101/httpPreview`) — dead across the fleet

This was tried first because it would have needed no native dependency at all. It does not
work on anything we own:

| | Result |
|---|---|
| Recorders probed | 17 (10 distinct models, 12 firmware builds) |
| `httpPreview` succeeded on | **0** |
| Answer | `403 notSupport` on newer firmware, `403 invalidOperation` on older |

Digest auth passed on every attempt (401 → 403), so this is a firmware gap and not a
credentials problem. The capability document says so declaratively:
`/ISAPI/Streaming/channels/101/capabilities` returns
`<streamingTransport opt="RTSP">RTSP</streamingTransport>` — RTSP is the only transport the
firmware advertises. An OEM camera was probed directly too, in case it was a camera-side
feature; it answers `invalidOperation`, which kills the "point at the camera instead"
fallback as well.

### WebSocket preview (7681) — exists, but buys nothing

`/ISAPI/System/capabilities` reports `isSupportWebSocket: true` on 8 of the 17 recorders,
and it is real, plugin-free H.264/H.265 — it is what the modern web UI uses. But **7681 is
forwarded at zero remote sites**. It needs the same firewall change RTSP does, so it solves
nothing RTSP does not already solve, and it is a third protocol to implement. Worth knowing
it exists if a customer ever objects to the SDK port specifically.

### Snapshots — a different feature, not this one

`/ISAPI/Streaming/channels/N/picture` works on 15 of 17 and is a legitimate "is anyone in
the lobby" view, but polling JPEGs is not live video and would not replace what iVMS shows.
The two failures are both **V4.30.090 on DS-7608NI-Q2/8P**, which answers `400
badXmlContent` to every variant; the same model on V4.75.207 is fine, so it is that build.

One probe artifact worth remembering: a recorder can have **no channel 1**. One site's
channels start at 201, so anything that assumes track 101 exists is wrong — read
`/ISAPI/Streaming/channels` for real ids.

## 2. How the SDK route works

Port 8000 is not a "control" port. Hikvision's private protocol carries login, configuration,
live video, playback, downloads and the alarm stream, all multiplexed over one TCP session —
which is exactly why a firewall with only that port open is enough. `dwLinkMode` picks the
transport:

| `dwLinkMode` | Transport | Needs |
|---|---|---|
| **0** | **TCP, private protocol** | **nothing but the SDK port** |
| 1 | UDP | a UDP hole |
| 2 | multicast | multicast routing |
| 3 | RTP | a second port |
| 4 | RTP over RTSP | **port 554** — the one that is closed |
| 5 | RTP over HTTP | the HTTP port |
| 6 | HRUDP | a UDP hole |

iVMS-4200 uses 0. So does DVRTool (`HcNetSdk.LinkModeTcp`).

There were two ways to consume it, and the choice matters:

- **Let the SDK render.** Pass a window handle in `hPlayWnd` and `PlayCtrl.dll` decodes and
  paints into it. Fastest route to a picture, but it hosts a foreign renderer inside WPF via
  `HwndHost` and takes the stream away from LibVLC — no overlays, no snapshots, no frame
  access, and a second video pipeline to maintain alongside the RTSP one.
- **Take the callback.** `hPlayWnd = IntPtr.Zero` plus a `RealDataCallback`, and the SDK
  hands over the raw stream. **This is what was built.** The bytes are an MPEG program
  stream — the same container Hikvision's HTTP exports turn out to be whatever they are
  named — so LibVLC demuxes them through a `StreamMediaInput` exactly as it demuxes RTSP,
  and every existing player behaviour, the remux recipe included, keeps working.

The same door opens for recorded video: `NET_DVR_PlayBackByTime_V40` and
`NET_DVR_GetFileByTime_V40` also run over the SDK port, so playback and export could one day
stop depending on the HTTP port too. Not implemented — there is no site where HTTP is shut
and the SDK port is open, so nothing forces it yet.

## 3. The traps

### Display channel ≠ SDK channel

The one that silently shows the wrong camera. DVRTool's channel numbers come from ISAPI,
which numbers analog and IP inputs in one run, analog first. HCNetSDK numbers them in two
blocks, from `byStartChan` and `byStartDChan`. On a pure NVR that makes display channel 1
into **device channel 33**.

Ask a DS-7716NI for SDK channel 1 and it does not complain — there is simply no video. Ask a
hybrid DVR and you may get a completely different camera. So the mapping is read off
`NET_DVR_DEVICEINFO_V30`, which `NET_DVR_Login_V30` fills in for free, and never assumed:

| Field | Offset | Live DS-7716NI-I4/16P(B), V4.61.030 |
|---|---|---|
| `byChanNum` (analog count) | 52 | 0 |
| `byStartChan` | 53 | 1 |
| `byIPChanNum` (low byte) | 55 | 16 |
| `byStartDChan` | 66 | **33** |
| `byHighDChanNum` (high byte) | 68 | 0 |

Those offsets are verified against that unit, and they are wrong at every neighbouring
offset — a 16-channel NVR reporting exactly `16 IP channels from 33` is not a coincidence.
`SdkChannelMapTests` pins the arithmetic for the pure-NVR, hybrid-DVR, non-default-start and
above-255-channel cases.

**`byHighDChanNum` is not optional.** A 300-channel NVR is a real product, and dropping the
high byte would refuse every channel past 44.

**A channel the device does not have is refused locally**, not passed through — because the
SDK accepts an out-of-range channel and then sends nothing, which reads as a firewall
problem rather than a typo.

**Still to verify on real hardware:** the hybrid-DVR case. The pure-NVR mapping is
live-verified; the analog-then-IP assumption for a HUHI-series DVR is derived from the
struct and unit-tested, but no hybrid unit has been in front of it yet.

### The callback thread must never block

`RealDataCallback` fires on an SDK-owned native thread that feeds the TCP session the whole
login shares. Stalling it does not just delay this stream — it stalls the connection. So
`SdkMediaStream.Append` has **no back-pressure**: when the reader falls behind, bytes are
dropped and counted.

Dropping takes the **oldest** chunks. A live viewer only wants now, so discarding the front
of the queue keeps latency bounded at roughly the buffer size; discarding the newest would
leave a viewer watching a delay that only grows. Either way the demuxer resynchronises at
the next keyframe, so the choice is about latency, not corruption. One exception: the chunk
just enqueued is never dropped, or a device whose chunks exceed the capacity would deliver
nothing at all, forever.

And the usual native-callback rules: the delegate is rooted for the life of the stream (a
collected delegate is a hard crash, not an error), and no exception may escape into native
code.

### LibVLC drops every frame after the first

Found on Site C's DS-9632NI-M8 (2026-09-01): the SDK preview showed one frame and froze,
while `dvrtool live` captured the same stream to a file that decoded perfectly — 207 HEVC
frames at 4256×1888, 20 fps, no errors. So the bytes were never the problem. Two things
about the stream and one about LibVLC combine:

- **Hikvision stamps each pack's SCR equal to the frame's own PTS**, so a frame reaches the
  decoder with no lead time beyond LibVLC's input cache (300 ms for a stream input).
- **The 0xBD private stream** (Hikvision smart metadata, sub-id `0x00`) is taken by VLC's PS
  demuxer for a VCD subtitle track, which trips a WinSubMux-era hack that discards the pack
  SCR and forces the clock from the video PES PTS ("force SCR" once per frame in the logs).
  Same outcome as the first point; it just makes it certain.
- **avcodec frame-threading** holds decoder output back by roughly one frame per thread. Ten
  threads at 20 fps is 500 ms, more than the cache, so the video output judges every frame
  late and drops it ("More than N late frames, dropping frame"). The keyframe that started
  the stream got through before the queue existed — hence exactly one picture.

The lab I-series recorder has the identical stream structure and only *looked* fine because
at 30 fps ten frames is 333 ms: it was dropping 108 of 532 frames in the same probe, which
reads as "slightly jerky" rather than "frozen".

**The fix is `:avcodec-threads=1` on the media** (`MainWindow.AddLiveDecodeOptions`; two
threads also works). Measured live against Site C, main stream, hardware decode:

| Media option | Frames displayed | Late-frame drops |
|---|---|---|
| default (10 threads) | 206 / 15 s | 118 |
| `clock-synchro=0` | 166 / 12 s | 89 |
| 1500 ms caching | 165 / 12 s | 89 |
| **`avcodec-threads=1`** | **297 / 15 s (full 20 fps)** | **0** |
| `avcodec-threads=2` | 294 / 15 s | 0 |

Single-threaded held for 30 s, on the sub stream, and even in software decode of the 8 MP
stream on one thread. Hardware decoding is unaffected: LibVLC 3 decodes through D3D11VA,
which the NVIDIA driver services with NVDEC (2–3 % decoder utilisation for the 8 MP stream,
0 % idle). Note that `--avcodec-hw=none` as a LibVLC argument is *ignored* by this build —
it still picks d3d11va — so a software-only control run needs a different lever.

The option belongs on RTSP media too: live555 delivers the same zero-lead timestamps.

### Stop before Complete, streams before logout

`NET_DVR_StopRealPlay` blocks until the SDK's receive thread joins — which is what
guarantees no callback is still running when the delegate goes out of scope. And
`NET_DVR_Logout` while a preview is running leaves those threads pointed at a dead session,
so `HikvisionSdkSession.Dispose` tears down its streams first.

### Only the preview plugins that are needed, and only when needed

`HCPreview.dll` and friends announce themselves on **stdout** as they initialize. The
existing `SdkRuntime.Preload` deliberately avoids loading them so that `dvrtool access find`
can pipe a roster into a script without SDK banner text spliced in. Live view needs them, so
they load through `SdkRuntime.EnsurePreviewPlugins()` — idempotent, called only by a caller
that is about to stream, and never reset by `NET_DVR_Cleanup` (which unwinds the SDK's state,
not the process's loaded modules).

This is also why `dvrtool live` writes a **file** and has no stdout-streaming mode: a piped
stream would arrive with `Load HCPreview.dll success!` in the middle of the video.

### Identity: the SDK login is checked against the web port's pin

A successful SDK login proves the credentials, not the hardware — the same problem
`docs/device-identity.md` exists for, and worse here, because **the SDK port is a separate
forward from the web port and can point at a different recorder**. Two of our sites had
exactly that collision (one WAN address served two records for the same site; another WAN
address served two different customers' recorders).

So the SDK session verifies against `DeviceIdentityGuard.AddressOf(conn)` — the **HTTP**
port's key, not a second key of its own. That is deliberate: a per-transport pin would mean
two ways to be half-pinned, whereas one key means a re-forwarded SDK port surfaces as a
serial mismatch. It works only because the SDK's `sSerialNumber` names the same serial as
ISAPI's `<serialNumber>`, which was verified against live hardware before the design was
committed to:

```
SDK    DS-7716NI-I4/16P(B)0000000000AAAAAA0000000AAAA
ISAPI  DS-7716NI-I4/16P(B)0000000000AAAAAA0000000AAAA
```

**They are not always byte-identical, though.** On the M-series recorders (found on Site C's
DS-9632NI-M8 and Site F's DS-7616NI-M2/16P, 2026-09-01) the SDK drops the hyphen ISAPI
puts between the model prefix and the serial digits:

```
SDK    DS-9632NI-M80000000000BBBBBB0000000BBBB
ISAPI  DS-9632NI-M8-0000000000BBBBBB0000000BBBB
```

A byte-compare therefore refused live view on every M-series box as a "wrong device", the
exact false positive the pin must never produce. `DeviceFingerprint.Normalize` in
`DVRTool.Core` strips hyphens (along with whitespace and case) before comparing, so both
spellings pin and match as one identity; the stored pin keeps whichever spelling was seen
first, verbatim.

Verification happens **inside** `HikvisionSdkSession.Open`, not in the callers, so no code
path can stream first and check afterwards. A login before the check is harmless — it costs
a user session and reads nothing; a *stream* before the check is the failure the mechanism
exists to prevent.

`--trust-new-device` is refused by `dvrtool live` on purpose. Re-pinning a replaced recorder
is a deliberate act that belongs on a command whose job is identity (`dvrtool info
--trust-new-device`), not on a video command.

### Stream slots are finite

Every viewer burns an SDK user session on the recorder, and a tech's own iVMS-4200 has
usually taken several already. `NET_DVR_OVER_MAXLINK` (46) is named in `DescribeError` for
this reason, and both front ends release the preview rather than leaving it to a GC: the GUI
stops the old stream before starting a new one, on device switch, on **⏹ Stop**, and at
shutdown.

### Error 11 is an offline camera

Documented as "wrong data sent to, or returned by, the device". What it means in practice on
a preview start is a channel with nothing behind it — a DS-7716NI answers exactly this for an
IP channel whose camera is offline. `DescribeError` says so, because the documented wording
sends an operator hunting for the wrong problem.

A subtler version: the device can **accept** the preview and then send nothing at all. That
silence is bounded by `SdkMediaStream.StallTimeout` and reported — the CLI fails with "the
device accepted the request but sent no video", the GUI says the same on the status line
rather than leaving a black pane.

## 4. What was verified, and how

Against the local DS-7716NI-I4/16P(B) on V4.61.030 (`192.0.2.10`), 2026-08-24:

| | Result |
|---|---|
| SDK login, serial, channel layout | matches ISAPI exactly; `16 IP channels from 33` |
| `dvrtool live --stream sub` | 8.03 s, 145 KB, MPEG-PS → HEVC 640×360 30 fps, 241 frames, no decode errors |
| `dvrtool live` (main) + `--remux mp4` | HEVC 2688×1520, `hvc1` tag, plays natively |
| Channel 99 (nonexistent) | refused locally, names the real channel layout |
| Channel 12 (offline camera) | SDK error 11, named |
| LibVLC via `StreamMediaInput` | `Playing`, 640×360 @ 30 fps, snapshot decoded to a real frame |
| GUI Live tab, SDK transport | live picture, timestamp advancing, identical rendering to RTSP |
| GUI teardown | **⏹ Stop** and window close both release the session without hanging |

Not verified: Dahua (not implemented), hybrid DVRs (no unit on hand), and remote sites over
a WAN link — everything above is a LAN test.

Against Site C's DS-9632NI-M8 over the WAN (`dvrtool live` + a LibVLC probe harness),
2026-09-01:

| | Result |
|---|---|
| `dvrtool live` main, 10 s | 2.3 MB MPEG-PS → HEVC 4256×1888 20 fps, 207 frames, no errors |
| Sub stream | HEVC 1200×536 20 fps, ~400 kbps (two fisheye channels: 720×720 30 fps) |
| LibVLC default vs `avcodec-threads=1` | 118 late drops → 0; see §3 |
| 16 sub-stream tiles, one session | all at full rate, 6–9 Mbps aggregate, ~9 % CPU, NVDEC 2–3 % |
| Main-stream preview added beside 16 tiles | 49 ms to start, 20 fps, 0 lost frames |
| Channels 22–32 (login says 32 IP channels, 21 exist) | `illegal channel (4)` — use the ISAPI list |

## 7. Grid view

The Live tab's **Grid** toggle shows every camera of the selected system on sub streams,
paged at 16, and a double-click on a tile brings that camera up full-size on its main stream
(double-click again or Esc to return). `MainWindow.LiveGrid.cs`, with the arithmetic in
`LiveGridLayout` (Core, unit-tested). What the measurements decided:

- **One SDK login for the whole grid.** `HikvisionSdkSession.StartLive` is called once per
  tile on the same session; each preview is its own TCP connection to the SDK port. The
  identity check runs once, at login, before any tile streams.
- **The viewer, not the recorder, is the limit.** HCNetSDK carrying 16 streams: 117 threads,
  0 % CPU, no dropped bytes. Each LibVLC player: ~85 threads and 35–40 MB. Measured with
  real rendering on an RTX 5090 workstation:

  | Tiles | CPU (whole machine) | Threads | Working set |
  |---|---|---|---|
  | 8 | ~1 % | 770 | 537 MB |
  | 16 | ~9 % | 1,453 | 803 MB |
  | 20 | ~9 % | 1,795 | 930 MB |
  | 21 | 35–53 % | ~1,880 | 985 MB |

  The cliff at 21 reproduced twice and is not a particular camera (the same channels at 20
  tiles were fine); with a dummy video output it halves, so about half of it is the per-tile
  Direct3D window. It was not pinned further because the answer is the same either way:
  **page at 16**, which is also iVMS-4200's default 4×4. A field laptop hits its own cliff
  earlier; if one does, the page size is the one number to lower.
- **Tiles start 100 ms apart.** Sixteen previews opening in the same instant is sixteen
  keyframes: 25–34 Mbps for a second on a stream that averages 7 Mbps, enough to stall a
  10 Mbps site uplink. Spreading them is invisible to the operator.
- **Maximize adds a preview, it does not rebuild.** Starting a main-stream preview on the
  running session took 49 ms and played at 20 fps with no loss beside 16 tiles, so the tiles
  keep running underneath and the way back is instant. A tech flipping between cameras never
  waits for a page of keyframes.
- **Maximize never shows black.** A main-stream preview needs a keyframe plus LibVLC's input
  cache before it has anything to draw — a couple of seconds on some cameras — so the
  double-clicked tile first grows to fill the panel on the sub stream it is already
  decoding (the other tiles collapse and the `UniformGrid` goes 1×1), while the main-stream
  player starts in the big view held at `Visibility.Hidden`. Hidden, **not Collapsed**: a
  hidden `VideoView` is still laid out, so its template and the window handle the player
  renders into exist at full size (on the very first maximize they are created by that layout
  pass), the window is just not shown, and LibVLC draws into it regardless. The swap happens
  when `Media.Statistics.DisplayedPictures` goes above zero — polled every 50 ms, because
  libvlc 3 has no per-rendered-frame event short of taking over rendering, and `Vout` fires
  at output *creation* (first decoded frame), before anything is drawn. Fallbacks:
  decoding (`VoutCount > 0` or `DecodedVideo > 0`) with nothing counted for 2 s swaps anyway,
  in case an output does not count into a hidden window (showing it paints the next frame);
  `Error`/`Ended`, or no picture at all in 20 s, releases the preview and leaves the sub
  stream up full-size with the reason on the tile's label. Double-click on the warming tile,
  or Esc, returns to the grid at any point.
- **Collapsed tiles must have their overlays cleared.** LibVLCSharp's WPF `VideoView` puts
  its content in a separate transparent top-level window and stops repositioning it once the
  host is zero-size, so a collapsed tile's overlay would sit over the maximized picture and
  eat the double-click. Emptied, the overlay is transparent and click-through. The reverse
  applies when restoring. **Empty it with a fresh element, never with `null`:** `VideoView`
  moves assigned content into the overlay window and resets its own `Content` to null while
  doing so, so a later `Content = null` is not a change and nothing happens — the first cut
  did exactly that and the screenshot showed sixteen labels painted over the big picture.
  `ClearOverlay` assigns an empty `Grid`.
- **Tiles come from the ISAPI channel list**, never the SDK's channel count (see §4). A
  channel ISAPI marks offline is shown but not started, saving a stream slot.
- **Tiles carry `:no-audio`** as well as the decoder option. Sixteen sites' worth of audio
  is noise.
- **RTSP grids work too** but say so when they fail: LibVLC's `EncounteredError` is
  surfaced on the tile, because at most sites the RTSP port is the one that is closed.
- **The footer describes the selected camera** (`MainWindow.LiveStats.cs`, arithmetic in
  `LiveStats` in Core, unit-tested): codec, picture size, decoded fps and received bitrate,
  sampled once a second from `Media.Statistics` on whichever player is "selected" — the
  maximized camera (main stream once it has a picture, sub until then), else the tile the
  operator single-clicked (blue border), else the single view while it plays. The rates are
  deltas of `DemuxReadBytes` / `DecodedVideo` between samples, not libvlc's `InputBitrate`,
  whose units are version-dependent — and not `ReadBytes`, which is **0 for the whole session
  over RTSP** (live555 is an access-demux; nothing passes through the stream layer) while
  `DemuxReadBytes` counts the bytes leaving the demuxer on every transport, within 1 % of
  `ReadBytes` on the SDK route. Both are 32-bit truncations that wrap at 4 GB, which the delta
  undoes, and a counter that went *backwards by less than that* means the player
  was given a new media, so the first reading after a restart is discarded. Three facts about
  those counters, found when the footer showed "over 60 fps" on 30 fps cameras (Site C,
  2026-09-02, VLC 3.0.21) and confirmed in VLC's source:
  - **`DecodedVideo` counts twice per frame.** `src/input/decoder.c` bumps it in
    `DecoderDecode` for every packet the decoder accepts and again in `DecoderQueueVideo` for
    every picture it outputs, and a program stream is one packet per frame. Measured: 39–40/s
    on the 20 fps HEVC main stream, 24/s on a 12 fps H.264 sub stream, both frame-counted
    independently with ffmpeg on a raw capture. The fps figure is the delta **halved**
    (`LiveStats.DecodedCountsPerFrame`).
  - **`DisplayedPictures` is not a frame rate either.** The video output re-renders the
    current picture every 80 ms (`VOUT_REDISPLAY_DELAY`) and counts each render; the 12 fps
    camera "displayed" about 20 a second. Nothing derives a rate from it. Pictures the vout
    skips because the *next* one is already due are counted neither as displayed nor as lost.
  - **The statistics block is a snapshot refreshed at most every 250 ms** (`MainLoopStatistics`
    in `src/input/input.c`, after each demux iteration), so two readings one second apart
    cover anywhere from about 700 to 1300 ms of stream — a steady 20 fps read anywhere between
    14 and 26 from one second to the next. Rates are therefore taken over the last **four
    seconds** (`LiveStatsWindow` in Core) and fps is shown as a whole number. The same window
    stops the once-a-GOP keyframe from making the bitrate jump every few seconds.
  - **The denominator is the camera's configured rate**, read off the video track's
    `FrameRateNum/Den` — the encoder's declared timing (SPS VUI), which on Hikvision is the
    frame rate the camera is set to (Site C: 90000/4500 → 20, 12000/1000 → 12; the lab recorder over
    RTSP 30000/1000 → 30). So "11/12 fps" is measured over configured with no extra device
    call, and a stream that omits the timing shows the measured figure alone.

  It was not the Direct3D plane: the double count and the jitter reproduce identically with
  hardware decode, software decode (`:avcodec-hw=none`) and no window at all. The codec comes
  from the media's video track (`h264`/`hevc` fourcc → "H.264"/"H.265") and the size from
  `MediaPlayer.Size`, which is what is actually on screen. `LostPictures` shows as
  "N dropped" only when non-zero, and on the SDK route `SdkMediaStream.BytesDropped` shows
  as "dropped by viewer" — both are viewer-side problems and are worded as such, because the
  question a tech is answering is "camera, link, or this PC?".

## 8. Fullscreen and digital zoom

**Established:** 2026-09-09, on the lab recorder (16 channels, 9 live), verified over RTSP in
the single view, in a 16-tile grid and on a maximized tile.

Two additions to the Live tab: a fullscreen toggle (the toolbar button, `F11`, `Esc` to come
back) and a wheel zoom on whichever pane the pointer is over, with a drag to pan and a
right-click to fit. `MainWindow.LiveFullScreen.cs` and `MainWindow.LiveZoom.cs`, with the
zoom arithmetic in `LiveZoom` (Core, unit-tested).

### Fullscreen: hide the chrome, never move the video

Fullscreen is the same window with everything but the video collapsed — the device list and
its splitter (panel collapsed *and* the columns set to zero, since a collapsed child does not
shrink a fixed-width column), the Live toolbar, the status bar and the tab headers — plus
`WindowStyle.None` and `WindowState.Maximized`. `Normal` first and then `Maximized`, because
changing the style while already maximized leaves the window sized to the work area and keeps
a taskbar-shaped gap.

**Nothing is reparented, and that is the load-bearing part.** A `VideoView` is a hosted child
window that LibVLC renders into by handle; moving the host into a borderless window of its own
destroys that handle and recreates it, which for a full grid is sixteen players pointed at
dead windows. Collapsing the chrome in place costs a layout pass instead. Verified: a live
2688×1520 H.265 stream stayed up across both transitions with no reconnect.

Hiding the tab strip took two tries, both worth recording:

- **Collapsing the `TabItem`s does not work.** A `TabControl` whose selected item is
  `Visibility.Collapsed` has no selected content to present, so the whole tab went blank —
  video, toolbar and all.
- **Swapping the `TabControl`'s template would work and must not be used.** It rebuilds the
  template's content presenter, which reparents the tab's content — the very thing the design
  avoids.
- **What is used:** each `TabItem` gets an empty `ControlTemplate`. A tab's own template draws
  only its header; the selected tab's *content* is presented by the `TabControl`'s template,
  which this never touches. Restoring is `ClearValue(TemplateProperty)`, **not** `Template =
  null` — a local null is still a local value and beats the theme style's setter, which leaves
  every tab permanently headerless (that was a real bug, caught on screen).

### Zoom: a crop on the decoded picture

The zoom is `libvlc_video_set_crop_geometry` on the pane's own player. Nothing is asked of the
recorder — no second stream, no PTZ, no extra bandwidth — so it works over both transports and
on all three vendors, and it cannot invent detail: a sub-stream tile at 4× is sub-stream pixels
four times the size, which is why the useful move is to maximize a camera (that switches it to
the main stream) and zoom that.

**The crop geometry must be the border form.** VLC documents three forms — a ratio `W:H`, a
window `WxH+X+Y`, and four borders `left+top+right+bottom` — and in LibVLC 3.0.21 the window
form is broken. Probed against the shipped LibVLC with `--verbose=3` on a 1280×720 source,
reading the vout's own `CROPPED` line:

| geometry | vout reports | wanted |
|---|---|---|
| `640x360+320+180` | `of (320,180), vsz 320x180` | `vsz 640x360` |
| `320+180+320+180` | `of (320,180), vsz 640x360` | ✔ |
| `0+0+640+360` | `of (0,0), vsz 640x360` | ✔ |
| `1:1` | `of (280,0), vsz 720x720` | ✔ (ratio form is fine) |

The window form's `X`/`Y` land as the left/top borders and its `W`/`H` as the right/bottom
ones, so the visible size comes out as `(width − x − W)` — which for any real zoom is negative
and paints the pane black. That black pane is what the first live test showed, and it is the
only reason `LiveZoom.CropGeometry` emits borders. Clearing is the empty string, which is what
libvlc itself passes down for `NULL`.

Everything else about the zoom is arithmetic in `LiveZoom`, kept pure so it could be tested
without a window:

- **State is a factor and a centre in picture coordinates** (0–1 across the decoded frame),
  never pane pixels: panes get resized, letterboxed and in fullscreen reshaped, while the
  thing being looked at is a place on the camera's picture. The centre is clamped as it is
  set, so the visible window can never hang off the frame.
- **A notch anchors on the point under the cursor**, which needs the letterboxing undone
  first (`LiveZoom.Pick`) — treating pane coordinates as picture coordinates puts the anchor
  off by the width of the bars, a sixth of the pane for a 16:9 camera in a 4:3 tile. On the
  bars themselves there is no anchor, so the zoom works about the current centre instead.
- **1.25 per notch, capped at 8×.** Ten notches crosses the range; six lands on 3.81, close
  enough to "read the plate" that nobody counts.
- **Sizes and offsets are rounded to even pixels** — the crop lands on a chroma-subsampled
  plane — and the window keeps the picture's aspect ratio, so the pane's letterboxing does not
  shift as it zooms.
- **The picture size is captured when a pane takes the zoom**, because with a crop applied
  `MediaPlayer.Size` reports what is being *displayed*; computing the next crop from that
  would compound it. For the same reason the footer reports the stream's size, not the crop's,
  while a zoom is on.

**One camera is zoomed at a time** — whichever pane the pointer is over, which also selects
that tile so the footer follows it. Any change of stream underneath (Play, Stop, a page turn,
a maximize, a restore, grid on or off) drops the zoom, because the crop belongs to the
*player* and would otherwise be inherited by the next camera to land there. The crop write
goes through the same `_playerLock` / `_playersDisposed` guard as every other native call on
these players: a crop set on a just-disposed player is a dereference of a released handle, not
an exception.

**Mouse input reaches the panes through their overlays.** A `VideoView` is a hosted child
window and WPF sees no input over it, so the wheel, the drag and the right-click are handled on
the almost-transparent overlay grid in front of each pane — the tiles' and the maximized view's
already existed for their labels and the double-click; the single view got one, which also
gave it a corner label (channel, stream, zoom factor) that is the only thing naming the camera
once the status bar is gone. That overlay is a layered window that stops tracking a collapsed
host, so anything that takes the screen from the plain view — grid mode, and the fisheye mode's
dewarp pane — empties it first: an empty overlay is fully transparent, and a fully transparent
layered window is not hit-tested at all. Left in place it would sit over the grid and swallow
the double-click, or over the dewarp pane and swallow the drag and wheel that aim it. Fullscreen
hides the fisheye toolbars and hint line with the rest of the chrome, and puts back whichever of
them were showing.

### Keys

`F11` toggles fullscreen from the Live tab. `Esc` unwinds one step at a time in the order the
operator got there: fullscreen first, then a maximized camera. Fullscreen hides the toolbar
along with everything else, so `PgUp`/`PgDn` stand in for the grid's paging buttons while it
is on. Leaving the Live tab leaves fullscreen with it.

## 5. Known limitations

- **Hikvision only.** Dahua's equivalent is `CLIENT_RealPlayEx` in `dhnetsdk.dll` on 37777.
  DVRTool does not link DHNetSDK at all, so `dvrtool live` refuses `--vendor dahua` with
  that explanation rather than failing obscurely.
- **Windows x64, with `HCNetSDK.dll` present.** `SdkRuntime` finds it via `--sdk-dir` /
  `DVR_SDK_DIR` / `OCB_SDK_DIR` and then the usual iVMS-4200 / HikCentral install paths. Every
  other command in the tool is cross-platform; this one is the exception, which is why the
  SDK route can never be the *only* live path.
- **The GUI still needs the web port** — not for the video, but to enumerate channels, which
  is where the channel list comes from. Acceptable because the web port is reachable at
  **17 of 17** sites while RTSP is reachable at 3; the SDK channel map could enumerate
  channels if a site ever shuts HTTP and leaves the SDK port open.
- **No audio.** `NET_DVR_AUDIOSTREAMDATA` is not requested and the muxed audio in
  `NET_DVR_STREAMDATA` is passed through untouched, which is all the RTSP path does either.
- **No SDK playback or export yet.** See §2.
- **The zoom is viewer-side only.** It crops what the stream already carries; it does not ask
  the camera for a region of interest and it is not PTZ. It also does not persist — the zoom
  is dropped whenever the stream underneath changes (§8).

## 6. Fleet consequence

The port survey that motivated this also turned up configuration errors worth keeping in
mind when reading any per-site result: two pairs of records pointing at the *same* SDK
endpoint, and one site with two recorders behind a single WAN address and no SDK forward at
all. The identity guard is what turns the first kind into an error instead of a wrong-camera
feed. Site-by-site addresses and open ports are deliberately not recorded here —
customer names and WAN ports do not belong in git.
