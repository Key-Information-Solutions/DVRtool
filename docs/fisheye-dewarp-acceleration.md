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
- The upload is architectural. LibVLC 3.0 returns frames in system memory through
  `vmem`, so they have to be pushed across. The way to delete it is LibVLC 4's
  Direct3D 11 output callbacks — the decoder writing into a texture we own. Worth doing
  when that API is available; not worth designing around before then.

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

## Verified, and not

Verified on hardware: the swap-chain path is pixel-identical to an offscreen target
including across a resize; the GPU and CPU renderers agree to a mean of 0.10–0.13 of a
level per channel on smooth frames with no pixel off by more than one; the rim goes
black on both; both chroma shader permutations agree; all three mounts render in every
view mode; 2560×2560 renders and reuses its textures.

**Not verified: anything on screen.** Whether `HwndHost` places and sizes the child
window correctly inside a WPF layout needs the app running with a dewarp tab and a
frame source, and neither exists yet — nothing feeds `DewarpFrame` from LibVLC's `vmem`
callbacks. A standalone WPF harness written to close that gap could not be built on the
development machine: **Splashtop Endpoint Security Service locks freshly compiled
unsigned binaries**, which surfaces as `MSB3021`/`CS2012` "access denied" on
`obj\…\*.dll` for a brand-new project while existing projects build fine.

## Also fixed here

`DewarpSampler`'s bilinear blend truncated instead of rounding — a systematic half-LSB
bias downward wherever the source was brighter to the right or below. Invisible alone;
it showed up the moment there was a second renderer to compare against, as two thirds of
all samples differing by exactly one. A `+ 128` before each shift took the mean
CPU-versus-GPU difference from 0.97 to 0.28.
