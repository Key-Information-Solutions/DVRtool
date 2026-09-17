# Camera settings: changing a camera's encoder, not only reading it

The per-camera encoder configuration — resolution, frame rate, codec, CBR/VBR, max bitrate,
I-frame interval, audio and the channel's name — in the GUI **Camera** tab and
`dvrtool camera show | options | probe | set`.

Read `device-config.md` for the recorder's *own* settings (clock, NTP, ports, LAN address) and
`hikvision-storage.md` for retention and the bitrate planner. The three are deliberately
separate and the boundary is worth stating:

| question | feature | interface |
|---|---|---|
| What is this **recorder** set to? | `docs/device-config.md` | `IDeviceConfigClient` |
| What will these cameras **write to disk**, and for how many days? | `docs/hikvision-storage.md` | `IStorageClient` |
| What is this **camera** configured to do? | this document | `ICameraSettingsClient` |

**Reads and writes are Hikvision only.** Dahua's encoder table is readable and its bitrate write
is specced but has never been fired; Nx sets a camera's rate through its own schedule cells
rather than a per-track document. Neither implements the interface, which is the point of it
being opt-in — `dvrtool camera` on either says so and exits 2 rather than half-working.

## 1. Why this is its own capability

`IDeviceConfigClient`'s own doc comment scopes it to "the recorder's own configuration, **as
opposed to the cameras on it**", and `device-config-spec.md`'s three tiers never anticipate
camera-level settings. Before this, the only per-channel write in the entire product was
`IStorageClient.SetMaxBitrateAsync` — one field, on one document, in service of retention math.
Everything an integrator actually changes on a service call meant the recorder's web UI, one
camera at a time, across a fleet.

## 2. `GET /ISAPI/Streaming/channels` — every track, not only the main one

The same document the storage read uses, with the track filter removed. `GetMainStreamsAsync`
keeps only `x01` **on purpose** — sub-stream bitrates in a retention total would flatter every
estimate — and that filter stays exactly where it is. `GetEncodingAsync` is a separate reader
that keeps every track, because here a sub stream is a setting to edit, not a bitrate to add up.

Track ids are `channel*100 + stream + 1`: `x01` main, `x02` sub, `x03` third. A track outside
those three is **skipped, not guessed at** — some firmware lists pseudo-tracks.

**`maxFrameRate` 0 is a choice, not a blank.** It is the web UI's "Full Frame Rate", and the
firmware re-resolves what it means whenever the resolution moves. So it is carried as its own
request field (`EncodingSettings.FullFrameRate`) and written as the literal `0`. Writing back
the number it happens to resolve to today would quietly pin a camera that was set to follow its
own maximum, and the grid would look identical afterwards.

**A camera with no audio input carries no `<Audio>` element at all.** Verified on the lab
recorder: ch1's document has none, ch2's has `<Audio><enabled>true</enabled>…`. So `?`/`—` in
the audio column means "there is nothing here to configure", which is a different fact from a
failed read, and the write refuses rather than inventing the element.

## 3. `GET /ISAPI/Streaming/channels/{track}/capabilities` — the device declares its own limits

This is what makes the whole feature safe to build. The document carries `opt=` lists and
`min`/`max` bounds, observed live:

```xml
<videoCodecType opt="H.264,H.265">H.265</videoCodecType>
<videoResolutionWidth opt="1280,1920,2304,2560,2688">2688</videoResolutionWidth>
<videoQualityControlType opt="CBR,VBR">VBR</videoQualityControlType>
<vbrUpperCap min="32" max="16384" def="4096">4096</vbrUpperCap>
<maxFrameRate opt="0,3000,2500,2200,2000,…">0</maxFrameRate>
<GovLength min="1" max="250" default="50">50</GovLength>
```

So both front ends offer exactly what each camera lists, and **a value the camera never declared
is refused, naming the camera's own list — never clamped**. A camera accepts a resolution it does
not support and silently ignores it; clamping would hand back a success for a setting nobody
chose. Three things to know before changing any of this:

- **Resolutions are two parallel `opt=` lists**, widths and heights, zipped positionally. A
  firmware whose lists differ in length is not guessed at — it declares nothing instead.
- **The default attribute is spelled both ways.** `def=` on the bitrate elements, `default=` on
  `GovLength`, in the same document. `adminAccesses/capabilities` already mixes them the same way
  (`device-config-discovery.md`); one helper reads both and a unit test asserts both spellings,
  because losing a default is silent.
- **Bounds differ per track.** On the lab recorder the main stream declares 32–16384 kbps and the
  sub stream 32–2048. A single per-camera range would be wrong for half the tracks.

**"Declared nothing" is not "allows nothing."** A missing capabilities document reads as `null`
(free entry, and the read-back is the only check); one that answers but lists nothing reads as
`EncodingOptions.IsEmpty`. Both front ends render those differently, the same `n/a` vs `?` rule
`ConfigScope` enforces. For a multi-camera selection the GUI offers
`EncodingOptions.Intersect` — what every selected camera accepts — where an undeclared camera
contributes **no opinion** rather than an empty set, since treating silence as "allows nothing"
would leave the operator with nothing to pick.

## 4. `PUT /ISAPI/Streaming/channels/{track}` — one writer, not two

`SetMaxBitrateAsync` used to PUT this document with its own inline logic and no re-read guard. It
now forwards to `SetEncodingAsync`; its signature and contract are unchanged and it inherits the
guard. **Two writers to one document with different safety levels is how one of them quietly
loses what the other has.** `GetBitrateRangeAsync` shares the bounds reader for the same reason —
the retention planner and the settings editor cannot disagree about what a camera will accept.

The pipeline, the same shorthand as every other fleet write in this repo:

1. Read the track, remembering the raw document.
2. **Re-read immediately before writing** and refuse if the device's copy moved
   (`ReReadForWriteAsync`). Schedules on a live site were seen changing under an operator
   mid-batch; an encoder document edited from the recorder's own web UI is the same hazard, and
   clobbering it silently is worse than refusing. Whitespace-only reformatting is not a change.
3. Replace only the requested fields **in the device's own document** and PUT it whole. Fields
   nobody asked about — and per-firmware fields we have never seen — survive.
4. **HTTP 200 is not success.** ISAPI answers a `ResponseStatus`; `statusCode` 1 is the only one.
5. Read back and report what the recorder **kept**. A value it silently held onto comes back as
   `ChannelChange.Rejected` with its own words, never as a success.

**A field the channel does not expose is refused, not invented.** Asking for `--gop` on a
document with no `<GovLength>` names that in the error and writes nothing; adding an element the
firmware does not expect is how a whole PUT gets rejected for one field.

## 5. The channel's name lives somewhere else

Not in the streaming document. It is in the channel list — `/ISAPI/ContentMgmt/InputProxy/channels/{id}`
for an IP camera behind an NVR, `/ISAPI/System/Video/inputs/channels/{id}` for an analog input on
a DVR. `GetChannelIdentityAsync` finds out which by reading, and carries the source along,
because **a rename aimed at the wrong list is accepted and does nothing**. The rename is
single-camera by design in both front ends: every camera sharing one name is never what somebody
meant, and it would undo the labelling the rest of the product reads back.

## 6. Pins

A camera pinned in the retention planner (`channel-pins.json`, `dvrtool storage pin`, the Storage
tab) is **held back from a bitrate change here** and said out loud. A pin bites on the bitrate and
nothing else — blocking a resolution change would make a pin mean something nobody agreed to.
`--ignore-pins` plans as if nothing were pinned and, like `storage plan`, cannot be combined with
`--force`. The GUI reads pins with the same `host:port` + serial key, and an unreadable pin file
is **loud**: the tab warns that pinned cameras are not being protected rather than treating it as
"nothing is pinned".

## 7. Shape

`CameraSettingsPlan` (Core, pure, tested) is the one piece of arithmetic both front ends describe
the write from — the CLI's dry run and the GUI's confirmation dialog — exactly as `UserAddPlan`
and `BitratePlan` are. Two front ends deciding separately what a write will do is how they come
to disagree, and the dialog describing the write is the last thing between an operator and a
fleet set to the wrong resolution. Each channel lands on `NoChange`, `WillChange`, `OutOfRange`,
`Unreadable` or `Pinned`, and the held-back ones are never silently dropped from the summary.

Both front ends: dry run is the default, an explicit `--dry-run` beats `--force`, the GUI dialog
names every change and **defaults to No**, identity is re-verified on a fresh client between the
read and the write (`DeviceIdentityGuard.Ensure`, which throws), one camera's failure does not
abandon the batch, and the grid repaints from the recorder's own read-back rather than from the
plan.

## 8. Verified

**Automated** (`CameraSettingsPlanTests`, `HikvisionCameraSettingsTests`, 52 tests): plan
arithmetic, pins, refusals, the option intersection; both XML namespaces; the full-document round
trip with unknown fields intact; a rejection under HTTP 200; a document that moved between the
read and the write; whitespace-only reformatting tolerated; a field the channel does not expose;
a value the recorder silently kept; the storage bitrate write going through the same guarded path.

**Live on the lab recorder, 2026-09-17** (DS-7608NI series, 9 cameras / 18 tracks):

- Reads: every track, per-track capabilities, names off the IP-camera list.
- The capabilities enumerations above, including the two default spellings and the per-track
  bitrate bounds.
- A refusal: `--resolution 9999x9999` held back, naming the five the camera declares.
- **CLI canary write**: ch4 `GovLength` 50 → 40 → 50, read back at each step, clean.
- **GUI Apply fired live**: ch4 `GovLength` 50 → 45 through the Camera tab's confirmation dialog,
  verified by read-back, confirmed independently with `dvrtool camera show`, then restored to 50.

**Not yet fired:** a resolution, codec or quality-mode change on any recorder; a rename; a write
on any customer recorder; anything on an M-series or a DVR/hybrid chassis — where the analog
`VideoInput` name path in §5 is still unexercised. A camera that offers a **third stream** has not
been seen yet either; the lab recorder lists main and sub only.
