# DVRTool updates

Every installed DVRTool — the GUI and `dvrtool` alike — can find the newest release, prove it
is ours, and install it in place. The MSI stays the only installer: an update is the next
`DVRTool-x.y.z.msi` run as a major upgrade over the current one (`docs/installer.md`), so the
install dir, PATH entry, `HKLM\SOFTWARE\DVRTool` key and UpgradeCode are exactly what a hand
install leaves. Nothing downloads or installs without a click or a `--yes`.

Code: `src/DVRTool.Core/Updates/` (all the reasoning), `MainWindow.Updates.cs` +
`UpdateWindow` (GUI), `UpdateCommands.cs` (CLI), `tools/DVRTool.ReleaseSign` and
`release.ps1` (the release side).

## Where releases live

GitHub Releases on `Key-Information-Solutions/DVRtool`. A release is the tag `vX.Y.Z` plus
three assets:

| Asset | What it is |
| --- | --- |
| `DVRTool-X.Y.Z.msi` | the installer `build-installer.ps1` produces |
| `update.json` | the manifest: version, MSI file name, size, SHA-256, notes URL, published time, `keyId` |
| `update.json.sig` | Ed25519 signature over the exact bytes of `update.json`, base64 |

Clients ask `https://api.github.com/repos/Key-Information-Solutions/DVRtool/releases/latest`
anonymously (60 requests/hour per address; a client checks once a day), fetch `update.json` and
`.sig`, verify, compare versions, and only then offer the MSI. `/latest` skips **pre-releases
and drafts**, which is the test path: `release.ps1 -PreRelease` publishes a build no installed
DVRTool will see; install it by hand at one site; promote it with
`gh release edit vX.Y.Z --prerelease=false` when it has earned the fleet.

## Why a release is believed

**GitHub is where a release comes from, not why it is trusted.** The app carries an Ed25519
public key (`UpdateSigning.PublicKeyHex`, key id `a68b6dde`); a manifest that does not verify
against it is refused with a loud "NOT signed by the DVRTool release key" and is never offered,
however it arrived and whatever TLS said. The MSI is then held to the signed manifest's size and
SHA-256 before it is run, and a mismatch deletes the download. So a compromised GitHub account,
a stolen `gh` token or an interposed proxy can at worst make the check fail; none of them can
put code on a workstation.

The private key lives on the release machine only:
`%USERPROFILE%\.dvrtool-release\update-signing.key`, DPAPI-protected to the Windows user that
made it (like the saved device passwords). **Back it up** somewhere that is not that machine:
without it no future release can be signed, and the only recovery is shipping a new key by hand
to every install. It is deliberately **not** in CI: a key in GitHub Actions secrets would make a
GitHub compromise equal to a key compromise, which is the one thing the key exists to prevent.

```powershell
dotnet run --project tools\DVRTool.ReleaseSign -- keygen     # once; refuses to overwrite
dotnet run --project tools\DVRTool.ReleaseSign -- pubkey     # does the key match this build?
dotnet run --project tools\DVRTool.ReleaseSign -- verify --manifest update.json
```

Rotation is designed but not built: `keyId` in every manifest names the key that signed it, so
a future build can carry two keys and a release signed by the old one can introduce the new.
Today exactly one key is accepted. Authenticode code signing is not assumed; if a certificate
is bought it adds on top of this and does not replace it.

## Versioning

One number, `<Version>` in `Directory.Build.props`. Every assembly is stamped with it, and the
installer's `ProductVersion` defaults to it, so the version the running app compares against a
manifest **is** the version the MSI's upgrade logic compares. Numeric `x.y.z` only (the MSI
limit); `ProductVersion.TryParse` refuses a fourth field or any `-pre`/`+sha` suffix. Versions
compare as `System.Version`, never as strings. The numbered line starts at **1.1.0** so that
the first real release is distinguishable from every unversioned `1.0.x` hand-built install,
all of which upgrade cleanly to it.

## Cutting a release

```powershell
.\release.ps1 -Version 1.2.0                 # tests, bump, commit, tag, MSI, sign, publish
.\release.ps1 -Version 1.2.1 -PreRelease     # one-site test build
.\release.ps1 -Version 1.2.0 -NoPublish      # everything but the GitHub release
```

The script refuses a dirty tree, a branch other than `main`, an existing tag, a version not
above the current one, failing tests, and a signing key that does not match the public key
compiled into the build it just made. It commits `Release x.y.z`, tags `vx.y.z`, pushes both,
and creates the release with generated notes (or `-Notes "…"`). Assets are left in
`artifacts\release\<version>\` for a hand publish if `gh` fails.

## What the client does

**GUI.** On startup, if the last check is more than 24 h old, a quiet check runs after the
device list loads. Only an *available* result shows anything: a banner above the tabs —
*What's new* (browser), *Install now*, *Skip this version*, ✕ — that never steals focus from a
live view. *Install now* opens a small window that downloads with progress, verifies, and then
waits for **Install and restart**: msiexec is launched (`/passive /norestart` — a progress bar,
no questions; Windows shows UAC once, naming the MSI), the main window closes through its
normal cleanup, and `cmd.exe` relaunches the new GUI when msiexec exits 0. The `?` at the right
of the status bar is About: version, install dir, last check, release key id, and a manual
check whose every outcome reaches the status bar. A **signature rejection is never quiet**,
even on the automatic check.

**CLI.** The fleet path, scriptable from any remote-management tool:

```
dvrtool --version                 # dvrtool 1.1.0
dvrtool update check [--json]     # exit 0 up to date, 10 newer available, 3 disabled, 1 error
dvrtool update download           # prints the verified path under %LOCALAPPDATA%\DVRTool\updates
dvrtool update install --yes      # download, verify, msiexec /passive; --quiet for /qn
```

`install` without `--yes` refuses, matching the repo's `--force` convention for writes.

**Refusals.** `UpdatePolicy` reads `HKLM\SOFTWARE\DVRTool`. A copy running from outside
`InstallDir` (a dev build in `bin\`, an unzipped folder) reports *not an installed copy* and
never offers — msiexec from it would upgrade some other install and leave the running one
untouched. `HKLM\SOFTWARE\DVRTool\Updates\Enabled = 0`, written when the MSI is installed with
`UPDATES=0`, is the per-machine switch for a locked-down client workstation: the GUI shows
nothing at all and `dvrtool update check` reports the policy (exit 3).

```powershell
msiexec /i DVRTool-1.2.0.msi /qn UPDATES=0     # this machine never checks
```

Per-user state (`%APPDATA%\DVRTool\updates.json`): last check, skipped version, last seen
version, auto-check flag. Losing it costs one extra check and one re-shown banner, so it loads
quietly — unlike the pin files, whose contents guard writes to recorders.

## Things worth knowing before touching it

- The manifest is verified **as bytes** and parsed only afterwards; `UpdateManifest.Parse` is
  never handed anything unsigned. Keep it that way.
- `UpdateClient.CheckAsync` never throws (every failure is `UpdateCheck.Failed` with one line)
  and has its own 30 s budget; the 30 min `HttpClient` timeout is for the MSI download.
- The Releases API's asset `size` must agree with the signed manifest's, or the release is
  refused before anything large is downloaded.
- `UpdateInstaller` does **not** elevate. msiexec raises UAC itself for a per-machine upgrade,
  so the operator sees a prompt for the MSI rather than for `cmd.exe`. `cmd.exe` is used as the
  relauncher because it is a system binary and survives our own files being replaced.
- A dev build (`bin\Debug`) can exercise everything but the offer: it reports *not an installed
  copy*. To test the full path install an MSI and run from `Program Files`.
- `UpdateClientTests` fake the whole channel through `MockHttpHandler` with a per-test key
  passed as `trustedPublicKey`; the shipped key is exercised only by "a random key's signature
  is refused".
