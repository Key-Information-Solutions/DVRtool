# DVRTool

DVRTool is an in-house **Windows desktop application** for Key Information Solutions:
view live cameras, search / play / export recorded footage across a multi-vendor NVR
fleet, audit NVR accounts, and review door-access rosters — talking directly to the
recordings stored on each NVR's own hard drives (the thing SmartPSS makes painful and
third-party VMSes can't do at all). A command-line tool (`dvrtool`) ships alongside for
automation and scripting, headless or remote runs, and power operations such as the gated
door-access writes.

## Vendor support

| Vendor | Protocol | Status |
|---|---|---|
| Hikvision (incl. LT Security OEM) | ISAPI over HTTP (digest) + RTSP | In progress — first target |
| Dahua / Amcrest | CGI over HTTP (digest) + RTSP | Driver written, needs live verification |
| Hikvision access control (DS-K / OEM "OCB") | HCNetSDK over the SDK port (P/Invoke) | Reads and writes live-verified |
| DW Spectrum | Nx REST `/media/` | Planned |
| UniFi Protect | Private `/api/video/export` | Planned |

## Layout

- `src/DVRTool.App` — the WPF desktop app operators use day to day (LibVLCSharp video
  panes); the primary product
- `src/DVRTool.Cli` — the `dvrtool` automation / scripting CLI, and the headless test
  harness
- `src/DVRTool.Core` — the shared engine both front ends ride: vendor-neutral contracts
  (`INvrClient`), models, digest HTTP, the ffmpeg remux helper, atomic download, and the
  cardholder identity map
- `src/DVRTool.Vendors.Hikvision` — ISAPI driver (search / download / live + playback RTSP URIs)
- `src/DVRTool.Vendors.Dahua` — CGI driver (`mediaFileFind` / `loadfile` / RTSP by time)
- `src/DVRTool.Vendors.HikvisionAccess` — door-panel driver (Hikvision SDK P/Invoke; no HTTP)
- `src/DVRTool.Vendors.HikvisionIvms` — one-way iVMS → DVRTool name-enrichment reader (SQLCipher)
- `tests/DVRTool.Tests` — unit tests against canned NVR responses

## The desktop app

`DVRTool.App` is the everyday operator surface. The left panel lists the saved NVRs and,
below them, the channels of whichever one is selected; the tabs on the right — **Live**,
**Playback / Export**, **Users** and **Access** — are the work. Every export, remux,
overwrite check and name-enrichment step runs through the same `DVRTool.Core` engine the
CLI uses, so the behavior is identical whichever front end you reach for — the app is not
a lesser path.

### Devices

**Add NVR…** opens a dialog for the name, vendor, host/IP, the HTTP, RTSP and SDK ports,
credentials, and an optional **Use HTTPS** (which pins the self-signed cert trust-on-first-
use, exactly as the CLI does). The **SDK port** is where the vendor's private SDK answers
(Hikvision HCNetSDK, Dahua DHNetSDK) — 8000 from the factory, but it is changeable from
the recorder's own network menu, so it is recorded per NVR rather than assumed.

**Test connection** checks all three ports at once, one line each, filled in as they
answer:

* **web** — a real API call, so a pass also proves TLS (including the pinned certificate)
  and the credentials, and reports the model, serial and firmware;
* **RTSP** — `OPTIONS`, then a digest-authenticated `DESCRIBE` of channel 1's live URL, so
  a pass means the stream is genuinely serveable rather than just the port being open.
  Note that Hikvision firmware answers `404` to an `OPTIONS` on `/` and is perfectly
  healthy — any well-formed RTSP status line proves the service, so the code is reported,
  never used to fail the port;
* **SDK** — a TCP connect only. Both vendors' SDK ports speak a proprietary binary
  protocol, so this reports a listener, never a working login.

Silence is the only real failure: a refused, dropped or unroutable port is red, while
anything that *answered* — an error reply, a rejected password, the wrong protocol — is
amber, because the port is demonstrably open and the fix is on the device rather than the
firewall. A closing line names the consequence, since which port fell short decides which
feature breaks: nothing works without the web port, playback and export need RTSP, and the
Access tab needs the SDK port. A partial pass does not block **Save**.

**Save** adds the NVR to the list. Saved NVRs
persist to `%APPDATA%\DVRTool\devices.json`, and each password is **DPAPI-protected for
the current Windows user** (`ProtectedData.Protect`, `CurrentUser` scope) — it cannot be
read back from the file by another Windows user or on another machine. This is the
primary, more-secure config path; the CLI's plaintext `.env` (below) is the automation
alternative. **Edit…** (or a double-click in the list) reopens the dialog on the selected
NVR; leaving the password blank keeps the stored one. **Remove** drops the selected NVR
after a confirmation.

### Live

Pick a channel, choose **Main** or **Sub**, and **▶ Play** opens the live RTSP stream in
the embedded player; **⏹ Stop** ends it. Credentials are handed to libVLC as stream
options rather than embedded in the URL, so the password never appears in the stream MRL
or the player's logs.

### Playback / Export

Enter a time window, **Search**, and the results grid lists the recording segments in
range. **▶ Play selected** streams one segment into the player; **⬇ Download selected…**
saves that segment; **Export range…** saves the whole window as one file. **Remux to a
playable file** governs both the download and the export buttons and is **on by default**
— the copies that leave this tool should play on the machine they are going to (the remux
explanation under CLI automation covers what that does and why).

The desktop app and the CLI export through the same `AtomicDownload` and remux pipeline,
so the atomic-promote and refuse-to-overwrite guarantees described below hold identically
here. What the app adds on top is a Save-As dialog that handles the naming and the consent:

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
was never offered either — it is refused and the raw download kept, exactly as the CLI
does. A raw export has no such second check: it streams onto the destination and the
promotion overwrites, the same single-check window the CLI has for `--out` without
`--remux`. Both come from `AtomicDownload`, which both front ends share.

If the name you choose contradicts what the remux writes — `case.dav` with remuxing on
produces MP4 — the app says so and asks before the transfer starts, rather than handing
you an MP4 called `.dav`. The CLI warns about the same thing after the fact.

Cancelling during a download saves nothing. Cancelling during the remux keeps the raw
download and says where it is: it is minutes of transfer, and VLC opens it.

### Users

Pick a device, optionally a second to **compare with**, and **Load** reads the account
list from each. The grid is **read-only** — user, the level on device A, the level on
device B, and a status (match, level differs, or present on only one). This is an account
**audit and comparison** view for spotting drift across the fleet; it does not create,
modify or delete accounts. Reading users opens its own short-lived connections, so it
never disturbs the Live or Playback session on the selected device.

### Access

The **Access** tab reads door-access panels rather than video recorders. Enter the panel
addresses (comma-separated), the panel username and password, the SDK port (8000 by
default) and, if it can't be auto-located, the SDK directory; **Load roster** shows every
fob and the doors it opens, one row per fob per panel, **read-only**. Because door rights are
per-panel — a fob on one controller says nothing about the others — every panel is read,
and a panel that cannot be reached marks the whole view **PARTIAL** in a warning of its
own: a fob may still be active on it, so "no access found" is not a safe conclusion.

These panels store **no cardholder names**, so the **Card** / **Doors** / **Valid**
columns come straight off the hardware while the **Name** column is filled from a
cardholder map imported one-way from iVMS. The **Cardholder names (from iVMS)** group does
that import: point it at the iVMS person database (blank auto-discovers the local install)
and supply the per-install database key (blank uses the `IVMS_DB_KEY` environment variable
or the key cached for this install; **Cache key** stores it, and DVRTool never derives or
displays it). **Import CSV…** reads the supported iVMS Person export — the complete path.
**Import from iVMS database** reads the live database and binds names to fobs by unique
expiry — a deliberately partial path, and the app says so each time. **Clear map** drops
the cached names.

The tab is read-only toward the panels on purpose: granting or revoking changes physical
door access, so those writes live only in the CLI, behind an explicit `--force` and a
read-back verification (see CLI automation). Nothing here writes back toward iVMS.

## CLI automation

`dvrtool` is the headless, scriptable surface: the same engine as the app, driven from a
shell for batch jobs, remote or unattended runs, and the higher-stakes operations the app
deliberately doesn't expose (the door-access writes below). It doubles as the project's
test harness, and it is what gets published self-contained (win-x64) to run live against
door panels on a machine like the relay host.

Credentials come from a `.env` file in the working directory (or `--env <path>`), or
an interactive prompt when neither `--pass` nor `DVR_PASS` is set. Avoid `--pass` on
the command line — it persists in shell history and process-audit logs. The `.env`
holds the admin password in **plaintext**: it is gitignored, and must never sit in a
footage/export folder that gets zipped up and shared. (The desktop app avoids this
plaintext caveat entirely — its device store keeps passwords DPAPI-protected per Windows
user.)

```
DVR_HOST=192.0.2.10
DVR_USER=admin
DVR_PASS=...
DVR_SDK_PORT=8000      # only when the recorder's SDK port was moved off 8000
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

`--sdk-port <n>` (or `DVR_SDK_PORT`) sets the vendor SDK port, the same field the desktop
app's Add-NVR dialog records. It defaults to 8000 but is not assumed: the port is
changeable from the recorder itself, and a moved port has to be given here to match.

Downloads and exports go through the shared `AtomicDownload` engine, so these guarantees
hold whichever front end wrote the file. A download streams to a `.part` file and is
renamed into place only on success, so a dropped connection or Ctrl+C never leaves a
truncated clip that looks complete.

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

### Access control (door panels)

Hikvision DS-K door controllers (including OEM rebrands like the "OCB" DS-K2604) are a
separate device class, not NVRs: they run no web server at all, so the ISAPI driver cannot
reach them. They speak only Hikvision's private SDK on its **SDK port** (8000 by default),
which means access control — the app's Access tab and the `access` command group alike —
needs **Windows, a 64-bit process, and HCNetSDK.dll**. Installing iVMS-4200 or HikCentral
Lite provides it, and it is found automatically (override with `--sdk-dir` or `OCB_SDK_DIR`).

Panels have their own credentials, kept alongside the NVR ones in `.env`:

```
OCB_PANELS=192.0.2.221,192.0.2.222,192.0.2.223
OCB_USER=admin
OCB_PASS=...
```

```
dvrtool access panels                       # model / firmware / doors / fob count per panel
dvrtool access roster                       # every fob and the doors it opens, across all panels
dvrtool access cards   --panel 192.0.2.221
dvrtool access find    --card 2375          # where one fob is provisioned
dvrtool access compare --panel 192.0.2.222 --against 192.0.2.221
dvrtool access export  --out roster.csv
dvrtool access grant   --card 9001 --doors 1,2 --panel 192.0.2.223 --force
dvrtool access revoke  --card 9001 --force
```

The read verbs (`panels`, `roster`, `cards`, `find`, `compare`, `export`) are the same
reads the Access tab surfaces, from a shell instead of a grid; `grant` and `revoke` are
the writes the tab deliberately does not offer. Cardholder names come in one-way from iVMS
the same way the Access tab imports them — the `access identity` verbs (`--import-csv` for
the supported Person export, `--import-ivms` for the live database, plus `--capture-key`,
`--where`, `--clear`) build a DVRTool-owned map that the read verbs apply automatically.

**These panels store no cardholder names**, and cannot be made to — writing the name field
is accepted and then silently discarded. A credential on a DS-K2604 is a fob number,
the doors it opens, and a validity window — nothing else. The name/employee fields exist in
the wire format but are empty on this firmware, and the card→name lookup is unsupported by
it, so names live only in whatever provisioned the fobs (iVMS-4200). `access find --name`
therefore says so outright instead of returning nothing, because an empty result would read
as "this person has no access" — the wrong conclusion to hand someone doing an offboarding
check. Look fobs up by number.

For the same reason, a panel that could not be reached is reported loudly and the view is
marked **PARTIAL**: a fob may still be active on it, so "no access found" is not a safe
conclusion. `compare` refuses to report drift at all from a partial read. The Access tab
raises the same PARTIAL warning for an unreachable panel.

Access is per-door, not fleet-wide — one panel typically holds the full roster while others
hold subsets — so a thorough check queries every panel, which is the default.

`grant` and `revoke` change **physical door access**, so without `--force` they are a dry
run: they print what they would change and exit non-zero. With `--force` they write and then
**read the fob back**, printing the state the device actually holds — a write the SDK
acknowledged is not proof the door changed. Revoking targets only panels that actually hold
the fob, and a revoke is `byCardValid = 0`, which is the device's own delete mechanism — the
record disappears outright rather than lingering as deactivated. These writes are the reason
the Access tab stays read-only: a button nobody has to confirm is the wrong home for a change
to a physical door.

A fob granted without `--valid-until` never expires, so `grant` says so; every fob iVMS
provisioned on these panels carries a window.

Writes are read-modify-write: the existing record is fetched and only the fields being
changed are touched, so week plans, holiday groups, card passwords and lock/room codes
survive instead of being zeroed by a rewrite.

## Conventions

- All timestamps are **NVR-local wall-clock time** (`DateTimeKind.Unspecified`).
  The NVR interprets and returns times in its own clock/timezone; we pass them through
  untouched. Cross-site timezone normalization is a later feature.
- Channel numbers are 1-based display numbers as shown in each vendor's own UI.
