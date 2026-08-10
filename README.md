# DVRTool

In-house multi-vendor NVR client for Key Information Solutions: live view, recording
search, playback, and footage export — talking directly to the recordings stored on
each NVR's own hard drives (the thing SmartPSS makes painful and third-party VMSes
can't do at all).

## Vendor support

| Vendor | Protocol | Status |
|---|---|---|
| Hikvision (incl. LT Security OEM) | ISAPI over HTTP (digest) + RTSP | In progress — first target |
| Dahua / Amcrest | CGI over HTTP (digest) + RTSP | Driver written, needs live verification |
| DW Spectrum | Nx REST `/media/` | Planned |
| UniFi Protect | Private `/api/video/export` | Planned |

## Layout

- `src/DVRTool.Core` — vendor-neutral contracts (`INvrClient`), models, digest HTTP, ffmpeg remux helper
- `src/DVRTool.Vendors.Hikvision` — ISAPI driver (search / download / live + playback RTSP URIs)
- `src/DVRTool.Vendors.Dahua` — CGI driver (`mediaFileFind` / `loadfile` / RTSP by time)
- `src/DVRTool.Cli` — headless test harness & tech-friendly CLI
- `src/DVRTool.App` — WPF GUI (LibVLCSharp video panes)
- `tests/DVRTool.Tests` — unit tests against canned NVR responses

## CLI usage

Credentials come from a `.env` file in the working directory (or `--env <path>`), or
an interactive prompt when neither `--pass` nor `DVR_PASS` is set. Avoid `--pass` on
the command line — it persists in shell history and process-audit logs. The `.env`
holds the admin password in **plaintext**: it is gitignored, and must never sit in a
footage/export folder that gets zipped up and shared.

```
DVR_HOST=192.0.2.10
DVR_USER=admin
DVR_PASS=...
```

```
dvrtool info                                   # device model / serial / firmware
dvrtool channels                               # list channels
dvrtool search   --channel 3 --start "2026-07-21 00:00" --end "2026-07-22 00:00"
dvrtool download --channel 3 --start "2026-07-21 08:00" --end "2026-07-21 08:05" --out clip.mp4 --remux
dvrtool live-url --channel 3 [--stream sub] [--with-creds]     # paste into VLC
dvrtool playback-url --channel 3 --start ... --end ... [--with-creds]
```

Add `--vendor dahua` for Dahua units (default is hikvision). `--tls` switches the
HTTP API to HTTPS — port 443 unless you say otherwise, and plenty of NVRs don't use
443 (our DS-7716NI serves HTTPS on 8443), so give the port on the host:
`--host 192.0.2.10:8443` (or `DVR_HOST=192.0.2.10:8443`). NVR certs are
self-signed, so the cert is pinned trust-on-first-use into
`%APPDATA%\DVRTool\pins.json` and a later mismatch fails loudly. Note RTSP (live view
/ playback / the URL commands) stays cleartext regardless — only the HTTP API and
downloads are encrypted.

Downloads stream to a `.part` file and are renamed into place only on success, so a
dropped connection or Ctrl+C never leaves a truncated clip that looks complete.

An export never overwrites a file that is already there. A `download` whose target
exists is refused — before the transfer starts, not after you have waited out a long
one — naming the path it would have replaced. Pass `--force` to replace it deliberately.
A second run onto a name you already handed to a client is far more often a mistake
than an intention.

The target is checked again immediately before the finished file is moved into place,
because a download and remux take minutes and something can land there in between — a
second operator exporting to the same case name, a backup job, someone's script. That
refusal exits non-zero and says so plainly: the export was produced but not promoted,
and the raw download is still on disk.

### `--remux` — get a file that actually plays

Hikvision NVRs (and OEM derivatives such as LT Security) hand back an **MPEG program
stream** for an ISAPI download no matter what the file is called — a known defect of
the platform, not of this tool. VLC sniffs the content and plays it; Windows Photos,
QuickTime, browsers and most evidence-review software look at the extension, decide
it's an MP4, and refuse. Dahua has the same problem in a different shape: its exports
are `.dav` (DHAV), which almost nothing but SmartPSS opens.

`--remux` fixes that with a **stream copy** — ffmpeg rebuilds the container around the
untouched bitstream. Nothing is re-encoded, so the footage is bit-identical evidence
and the remux takes a second or two:

```
dvrtool download --channel 3 --start "2026-07-21 08:00" --end "2026-07-21 08:05" \
    --out clip.mp4 --remux           # MP4, the default
dvrtool download ... --out clip.mkv --remux        # Matroska, inferred from the name
dvrtool download ... --out clip     --remux mkv    # Matroska, asked for outright
```

MP4 is the default because it is the copy most likely to play on an unprepared
Windows machine — which is what matters when footage goes to a client, an insurer or
law enforcement. HEVC in MP4 gets the `hvc1` codec tag (ffmpeg's default `hev1` is
exactly what Photos and iOS reject) and `+faststart` so the index sits at the front of
the file. Matroska is there for codec and audio combinations MP4 cannot carry.

The raw download goes to a `.raw` sibling of the target and is deleted once the remux
succeeds. ffmpeg writes to a second sibling, `.remux.part`, which is renamed onto the
target only after it exits cleanly — ffmpeg truncates its output the moment the muxer
opens it, so pointing it straight at the target would mean a killed process leaves a
truncated file under the finished export's name. That rename is refused (without
`--force`) if the target exists by then, and the `.remux.part` is discarded rather than
left lying around. If ffmpeg fails or isn't installed, the `.raw` file is kept and its
path printed — it is the footage, and VLC opens it; nothing is left at the target.

Giving `--remux` an explicit container that disagrees with the `--out` name — say
`--out clip.mkv --remux mp4` — writes what you asked for and warns, rather than
renaming a file you named yourself. Plain `--remux` can't hit this: with no value the
`--out` extension picks the container.

Without `--remux`, an auto-named download is named after the container the device
actually sent (an `.mpg` for the Hikvision case above), and an `--out` name that
contradicts the bytes gets a warning rather than a silent rename — you named the
file, so we don't second-guess it.

ffmpeg must be on `PATH`.

## GUI exports

The WPF app exports through the same pipeline as the CLI. **Remux to a playable file**
on the Playback / Export tab governs both `Download selected…` and `Export range…`, and
is on by default — the copies that leave this tool should play on the machine they are
going to.

The Save-As dialog suggests a name for what the file will actually be: `.mp4` with
remuxing on, and otherwise the container the device really sends (`.mpg` for Hikvision,
`.dav` for Dahua) rather than the `.mp4` the device implies. Choose an `.mkv` name and
the remux writes Matroska. With remuxing off, the download is sniffed once it lands and
a warning names the mismatch if the bytes contradict the name you chose.

The Save dialog's own "replace this file?" prompt is the permission to overwrite —
answering yes there is the GUI's `--force`. That permission is bound to the exact name
the dialog showed: leave the extension off and the remux supplies one, and the export
lands somewhere you were never asked about, so it is refused rather than written.

For a remuxed export, a file that appears at the target *while* the download is running
was never offered either — it is refused and the raw download kept, exactly as in the
CLI. A raw export has no such second check: it streams onto the destination and the
promotion overwrites, the same single-check window the CLI has for `--out` without
`--remux`. Both come from `AtomicDownload`, which both front ends share.

If the name you choose contradicts what the remux writes — `case.dav` with remuxing on
produces MP4 — the app says so and asks before the transfer starts, rather than handing
you an MP4 called `.dav`. The CLI warns about the same thing after the fact.

Cancelling during a download saves nothing. Cancelling during the remux keeps the raw
download and says where it is: it is minutes of transfer, and VLC opens it.

## Conventions

- All timestamps are **NVR-local wall-clock time** (`DateTimeKind.Unspecified`).
  The NVR interprets and returns times in its own clock/timezone; we pass them through
  untouched. Cross-site timezone normalization is a later feature.
- Channel numbers are 1-based display numbers as shown in each vendor's own UI.
