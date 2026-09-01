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
  `SDK <port>`), and the CLI has `dvrtool live`, which records to a file.
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

One probe artifact worth remembering: a recorder can have **no channel 1**. one site's
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
exactly that collision (`203.0.113.50:8000` served two Acura records;
`203.0.113.51:8000` served both Site H and a different customer).

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

## 6. Fleet consequence

The port survey that motivated this also turned up configuration errors worth keeping in
mind when reading any per-site result: two pairs of records pointing at the *same* SDK
endpoint, and one site (one site, two recorders behind `203.0.113.52`) with no SDK
forward at all. The identity guard is what turns the first kind into an error instead of a
wrong-camera feed. Site-by-site addresses and open ports are deliberately not recorded here —
customer names and WAN ports do not belong in git.
