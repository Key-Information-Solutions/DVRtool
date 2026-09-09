# Fisheye dewarp: the two renderers, and which one runs

Measured 2026-09-03 on a 24-core workstation with an RTX 5090 (driver 32.0.16.1656),
source 2560×2560 NV12 — Site C's actual main stream.

DVRTool dewarps a fisheye two ways, and **hardware is the default**. The CPU renderer
was written first, which made it easy to treat as the baseline; it is not. An operator
at a workstation is the ordinary case, and headless or remote sessions are the
exception.

| file | what it is |
| --- | --- |
| `FisheyeShader.cs` (Core) | `DewarpShaderConstants` — the shader's constant block, and a C# transcription of the HLSL |
| `FisheyeBackend.cs` (Core) | `DewarpBackendPolicy` — which renderer a machine gets, and why |
| `FisheyeRenderer.cs` (Core) | `IDewarpRenderer`, `DewarpFrame`, `CpuDewarpRenderer` |
| `Dewarp.hlsl` (Render.D3D11) | the pixel shader; the twin of `DewarpShaderConstants` |
| `Direct3DDewarpRenderer.cs` | upload, draw, present |
| `Direct3DProbe.cs` | adapter enumeration, WARP detection, remote-session detection |
| `DewarpSurface.cs` (App) | the WPF control: GPU first, live fallback |

## The numbers

| view (1600×900 pane) | CPU, 24 cores | GPU |
| --- | --- | --- |
| zoomed in, 20° | 2.15 ms | 1.58 ms |
| mid, 60° | 2.42 ms | 1.88 ms |
| zoomed out, 150° | 3.80 ms | 1.60 ms |
| 360° panorama | 2.5–12 ms | 1.86 ms |
| **dragging** (a new view every frame) | 7.95 ms | 0.77 ms |
| **16 panes of one frame**, 480×270 | 6.55 ms | 0.62 ms |

**Read the breakdown before optimizing anything here.** The draw is **0.02–0.04 ms**
for a 1600×900 pane. Everything else — about **1.6 ms** — is getting the 9.8 MB of
planes onto the adapter, most of it a memcpy on the calling thread. So:

- The steady-state single-pane margin on a *many-core* machine is modest, 1.2–2.4×.
  It is not modest on four cores, where the CPU figures are several times worse, and
  it is not modest for anything that draws more than one pane or moves the view.
- The cost is **per frame, not per pane**, which is why `Upload` is split from
  `RenderPane`. Sixteen panes of one fisheye cost 7.1 ms as sixteen `Render` calls and
  0.62 ms as one upload plus sixteen draws. Before that split the accelerated path
  *lost* on the grid case, 12.6 against 9.4 ms.
- The upload exists because LibVLC 3.0 returns frames in system memory through
  `vmem`, so they have to be pushed across. It is about 5 % of a 33 ms frame period and
  the live run below skipped nothing, so it is not a blocker. Deleting it means the
  decoder writing GPU-visible memory — LibVLC 4's Direct3D 11 output callbacks, a
  hardware decoder, or a mapped staging texture handed to `vmem` — which is a frame-source
  change, not a renderer one. See "The frame source" below.

Benchmark with the adapter warmed up. An idle GPU's first measured case came out
3–4× slower than the identical work measured a second later, which reads as "zoomed in
is expensive" and is really "the clocks had not ramped".

## Which renderer a machine gets

`DewarpBackendPolicy.Choose`. Under `Auto`, hardware unless one of:

- no Direct3D 11 device could be created;
- the adapter is WARP or the Microsoft Basic Render Driver (`AdapterFlags.Software`, or
  vendor id `0x1414`) — slower than the parallel CPU renderer it would replace;
- `SM_REMOTESESSION` is set;
- WPF's render tier is 0.

**Every one of those rules is about presentation, not arithmetic.** A remote session
often *does* have a hardware adapter — Windows has offered one to Terminal Services
sessions since WDDM 1.2 — and the shader would run fine. What does not run reliably is
getting the picture on screen. That is why an explicit `Gpu` preference overrides all
four, and why the CPU renderer is the answer for headless and RDP rather than a
slow-machine fallback.

## Traps

- **The HLSL and `DewarpShaderConstants` are twins and must be edited together.** Two
  copies of the projection in two languages; a mismatch does not crash, it ships as
  "the accelerated view looks slightly soft". `FisheyeShaderTests` holds the C#
  transcription against `DewarpGeometry` across every lens law, mount and view mode —
  100k+ pixels to 0.02 px — so the HLSL is a transcription of something proven. The one
  place the two texts legitimately differ: `SV_Position.xy` already arrives at the pixel
  centre, so the shader adds no half pixel where the C# does.
- **`LensProjection`'s ordinals are load-bearing.** The shader switches on them as
  integers. Reordering the enum compiles, passes everything else, and dewarps through
  the wrong lens law on the GPU only. Pinned by a test.
- **An integer source coordinate is a pixel *centre*,** matching the CPU sampler. A
  texel centre is at `(index + 0.5) / size`, so the half pixel goes on the UV and
  nowhere else.
- **The circle may reach past the edge of the frame** (a full-frame fisheye's does), and
  those pixels have no picture behind them. The shader's `InsideFrame` check is what
  keeps the rim black; without it the sampler's clamp address mode answers with the
  nearest edge texel and smears the last row outward.
- **Do not use `Sample()`'s free screen-space derivatives for the mip level.** Across
  the rim, one pixel of the 2×2 quad is outside, its coordinate is whatever the branch
  left behind, the derivative explodes, the hardware picks the coarsest level, and the
  edge of the pane comes out a blurred halo one or two pixels thick. Three explicit taps
  cost ~60 flops and are exact.
- **A wide rectilinear pane minifies hardest in the *middle*,** not at the corners. A
  flat pane spreads angle as `tan θ`, so a peripheral pixel covers `cos²θ` as much of the
  circle as a central one — eightfold at 140°, measurably 1.8 against 0.3 in level. This
  was written down backwards first and a test caught it.
- **Mip generation is lazy** and a false negative must be harmless. `Minifies` samples a
  17×17 grid; when it says no, the draw uses a sampler clamped to level 0, so a missed
  minifying region costs sharpness and can never sample a level that was never
  written — which would put last frame's contents in a live view.
- **`Bilinear = false` does not disable mipmapping** on the GPU; it switches the sampler
  from linear to point and still reads the chain. To force the top level, use a large
  negative `LodBias`. A mip test written against `Bilinear = false` passes while proving
  nothing.
- **The two renderers legitimately differ under minification** — 134 levels apart on
  high-contrast content — because the GPU picks a fractional level per pixel where the
  CPU box-halves the whole pane once. Agreement tests must run magnifying; filtering is
  tested separately.
- **Chroma is sampled one mip level up its own chain** (`lod - 1`), because the plane is
  already half resolution. Sampling it at the luma level double-blurs the colour.
- **Colour conversion happens after filtering on the GPU and before it on the CPU.**
  That is safe rather than sloppy: YUV-to-RGB is affine and an affine map commutes with a
  weighted average. It is what lets the mip chain live on the planes.
- **The accelerated pane paints over WPF content.** It is a child window with a swap
  chain, so overlays go beside it, not over it — the same constraint `VideoView` already
  imposes. `RenderTargetBitmap` cannot capture it; a screenshot needs the screen.

## The frame source, the Live tab's dewarp, and what was seen on screen

Added 2026-09-03, later the same day, after the question "DW Spectrum and iVMS do mainstream
dewarping, why can't we?" The answer was that nothing was in the way: DW Spectrum's own hardware
spec says its client decodes on the CPU and dewarps in OpenGL shaders — the same shape as this
pipeline — and the only missing piece here was the frame source. It is now in, and the whole
chain has been seen working.

| file | what it is |
| --- | --- |
| `FisheyeFrameRing.cs` (Core) | `DewarpFrameRing` — three pinned I420 slots between the decoder thread and the UI |
| `FisheyeDrag.cs` (Core) | `DewarpDrag` — grab-and-drag and wheel zoom, solved as least squares |
| `FisheyeTestPattern.cs` (Core) | a tiled floor through the calibration; the no-camera check |
| `VlcFrameSource.cs` (App) | a `MediaPlayer` with LibVLC's video callbacks, feeding the ring |
| `MainWindow.Dewarp.cs` (App) | the Live tab's dewarp mode — the ◎ Fisheye toggle |
| `DewarpSurface.cs` (App) | now also: `Redraw`, `ClearFrame`, and pointer events in pane pixels |

### Verified live, 2026-09-03

Site C, channel 21 "Lobby" (device channel 53), 2560×2560 H.265 main stream over the
SDK port 8000, on the RTX 5090 workstation:

| | decoded | shown | skipped |
| --- | --- | --- | --- |
| GPU renderer | 30 fps | 30 fps | 0 |
| CPU renderer (24 cores) | 30 fps | 30 fps | 0 |

The floor tiles come out straight in the flat view; drag and wheel work on the live picture;
Stop releases the stream slot and the window closes cleanly through the normal shutdown path.
Before the recorder, the same was proven with the test pattern: GPU and CPU renderers,
PTZ and 360° panorama, drag, wheel, and the renderer switch mid-session.

### Where it lives in the GUI, and why it moved (2026-09-09)

It shipped as its own **Fisheye** tab, which was the wrong shape. A tech watching a fisheye has
already chosen the device, the channel, the stream and the transport in the Live tab; a separate
tab made them choose all four again to see the same camera undistorted, and then choose them a
third time to go back. Dewarping is a **way of looking at a live camera**, not a place to go.

So it is now a **◎ Fisheye toggle in the Live tab's toolbar**, over whichever *single* camera is on
screen: the single view, or a camera maximized out of the grid. The toggle is disabled on a grid
page, and that is a property of the input rather than a policy — a dewarp resamples the
full-resolution picture and a grid page is sixteen sub streams, so "no single camera" is the same
condition as "nothing to dewarp". Maximizing a tile is exactly when the grid acquires a main
stream, and exactly when the toggle lights up.

Turning it on **restarts the picture**, because the two paths are different decoders: the plain
view is LibVLC rendering into a `VideoView`'s window, and the dewarp needs the decoded planes in
memory (`vmem`). One media cannot feed both. The toggle therefore stops one and starts the other
on the same channel, releasing the recorder's stream slot before taking another — never holding
two. Over a maximized grid camera it borrows the **grid's own SDK login** and starts a preview on
it, so dewarping a grid camera costs a stream slot, not a session; that preview is stopped by this
side, while the session stays the grid's to log out. Turning the toggle off puts the plain view
back: ▶ Play again in the single view, or the ordinary maximize (sub stream up while the main
stream warms) over the grid.

One trap found live while moving it: `VlcFrameSource.Stop` blocks until LibVLC's threads join, so
it runs on a worker — and a `Play` issued before that worker has finished is stopped by it a
moment later. On the old tab the SDK login gave the stop time to land; switching cameras over RTSP
does not, and the symptom is a stream that connects and then silently never delivers a frame. Every
start now awaits the previous stop. The companion fix is that "has this stream shown a frame yet?"
is its own flag, not a zero in the frame counters: the frame source outlives any one stream, so
its counts are cumulative and never return to zero for the second camera of a session.

While the dewarp is up the **footer stats block goes quiet**. A maximized camera's tile is still
running its sub stream underneath, and reporting that beside the dewarp's own status line is two
contradictory readings of one camera.

Anything that takes that one camera off screen ends the mode rather than leaving a stale picture
labelled as live — leaving the grid, Esc or a double-click back out of a maximize, and picking
another device. Picking another **channel** stops the stream but keeps the mode: the last picture
stays aimable, and ▶ Play opens the newly-selected camera, because opening a stream slot is what
Play is for.

**The upload is not the blocker it was described as.** The 1.6 ms plane upload is about 5 % of a
33 ms frame period, and the live run above skipped nothing. Deleting it means the decoder writing
GPU-visible memory — LibVLC 4's Direct3D 11 output callbacks (still preview builds), a hardware
decoder such as ffmpeg's `d3d11va` producing NV12 textures, or handing `vmem` a mapped staging
texture as its buffer — and the decode itself, not the upload, is where the CPU time goes. That
is the next performance step, and it is a decoder swap behind `DewarpFrame`, not a new renderer.
DirectX 12 or Vulkan would buy nothing here: the draw is 0.03 ms, and Direct3D 11 is the API
that WPF hosting, DXGI, hardware video decode and LibVLC's callbacks all speak.

### How LibVLC's video callbacks behave (VLC 3.0)

- **Strictly sequential, one buffer at a time.** The video-output thread calls *lock* for a
  buffer, copies the decoded picture into it, calls *unlock*, then *display* when the clock says
  so. So one slot is ever being written, and the frame handed to *display* is complete. Three
  slots — writing, ready, presenting — are exactly enough; `DewarpFrameRing` has a test that runs
  20 000 frames through a writer and a reader on two threads and checks the reader's slot is never
  touched.
- **The format callback's `pitches` and `lines` are arrays**, reached in LibVLCSharp as
  `ref uint` to the first element. `MemoryMarshal.CreateSpan(ref pitches, 3)` writes the other
  planes without `unsafe`. The chroma fourcc is four raw bytes; write `I420` into it regardless of
  what the decoder offers — software H.264/H.265 produce it natively, so it costs nothing on our
  streams. Pitches are rounded up to 32 bytes.
- **The return value of the format callback is a buffer count**; 0 means failure. It sizes
  nothing we own.
- **Never throw out of a callback**: an exception crossing into native code ends the process with
  no dialog. Every callback catches and records the message in `LastError`.
- **Frames reach the UI coalesced.** *display* marks the slot ready and posts one dispatcher
  invoke at render priority if none is outstanding; a UI that is behind sees the newest frame and
  `FramesSkipped` counts the ones it missed. The frame handed to the sink stays valid until the
  next one — the writer never gets that slot — which is what lets `DewarpSurface.Redraw` re-aim
  a paused or stalled picture. On the accelerated path a redraw is a draw with no upload.
- `:avcodec-threads=1` is kept from the Live tab (the first-frame-only bug is LibVLC's frame
  threading, not the output module) and `:no-audio` is added. The frame dimensions LibVLC reports
  are the buffer's, not the visible crop; on the fleet's streams they are the same.

### Pointer input on a child window

The swap chain's child window takes the mouse messages for its rectangle and WPF never sees
them. The predefined `static` class answers `WM_NCHITTEST` with `HTTRANSPARENT`, which would pass
the mouse to the WPF window behind — where WPF hit-tests a tree with nothing drawn there. So
`SwapChainHost.WndProc` answers `HTCLIENT` and translates `WM_LBUTTONDOWN` / `WM_MOUSEMOVE` /
`WM_LBUTTONUP` / `WM_MOUSEWHEEL` into `DewarpSurface`'s pointer events, in pane pixels, with
`SetCapture` for the drag. Wheel coordinates are screen coordinates, unlike the button messages.
The CPU path raises the same events from WPF's mouse events, converted from DIPs, so the tab has
one handler set for both renderers.

### Drag: why it is a least-squares solve, and the trap at the pole

The gesture is "grab the picture": the source pixel under the pointer at button-down should stay
under the pointer. That is a two-unknown problem in yaw and pitch, and **it does not always have
a solution.** The mount roll rule that keeps people upright pins where things can appear: on a
ceiling mount the nadir can only ever sit on the pane's vertical centre line, so a pointer that
grabbed the exact centre of the default view and moved sideways is asking for something no view
shows. The first solver — plain Newton — flung a 250-pixel drag to a 76° tilt from exactly that
gesture. `DewarpDrag` is Levenberg–Marquardt: damped steps, accepted only when the residual
falls, with a Jacobian column that is numerically zero frozen rather than solved for. Away from
the centre it is exact to a hundredth of a pixel in three or four steps; at the centre it tilts
to the pointer's height and leaves the yaw where it was. Off-centre grabs near the nadir do what
they can without running off. Two more facts that cost time:

- **Pitch zero is a fold, not a point.** `ViewOrientation.Normalized` turns a negative pitch into
  a positive one at the opposite yaw, and under the mount roll that is a *different* picture (the
  two differ by a half-turn roll). So the residual is V-shaped in raw pitch at the pole and a
  central difference across it reads zero. Pitch is treated as a half-line: one-sided difference
  at zero and no step below it.
- **The centre of an 883×580 pane is (441.0, 289.5), not (441, 290).** Pixel centres are at +0.5.
  A test that pressed at (441, 290) and then measured the nadir was measuring a source pixel a
  pane-pixel away from the one grabbed, and reported a 0.6-pixel "stall" that was not there.

### Driving the app from a script (for the next person who verifies on screen)

UI Automation found the tab and buttons, but three things did not work as expected: a WPF
`ToolBar` overflow hides its items from automation (the first cut of the tab overflowed at the
default window width, which is why there are two toolbars); a `ListBox` row with
`DisplayMemberPath` exposes its *text* as a Text element, so find the text and walk up to the
`ListItem`; and a `ContentControl` hosting an `HwndHost` reports an empty bounding rectangle, so
the pane's screen position has to be derived from its neighbours. Mouse gestures were simulated
with `SetCursorPos` and `mouse_event`.

### Still not done

Per-device calibration persistence (the numbers reset with the app), automatic circle detection,
Quad in the GUI (`DewarpSurface` draws one view; `DewarpView.Panes` is ready for four),
hardware decode, and anything on Dahua or Nx fisheyes — the frame source is vendor-neutral
(RTSP works through the same player) but only Hikvision has been fed through it.

## Also fixed here

`DewarpSampler`'s bilinear blend truncated instead of rounding — a systematic half-LSB
bias downward wherever the source was brighter to the right or below. Invisible alone;
it showed up the moment there was a second renderer to compare against, as two thirds of
all samples differing by exactly one. A `+ 128` before each shift took the mean
CPU-versus-GPU difference from 0.97 to 0.28.
