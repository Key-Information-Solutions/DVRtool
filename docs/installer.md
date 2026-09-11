# DVRTool installer (MSI)

One per-machine MSI carries both front ends: the GUI (`DVRTool.exe`) and the CLI
(`dvrtool.exe`), each published **self-contained win-x64** — target machines need no
.NET runtime. The WiX toolset is pulled as a NuGet SDK; the only build prerequisite
is the .NET SDK this repo already requires.

## Building

From the repo root:

```powershell
.\build-installer.ps1                 # -> artifacts\installer\DVRTool-1.0.0.msi
.\build-installer.ps1 -Version 1.2.0
.\build-installer.ps1 -SkipPublish    # repackage without re-publishing the apps
```

The script is a thin wrapper — the whole pipeline (publish both apps, link the MSI)
lives in [installer/DVRTool.Installer.wixproj](../installer/DVRTool.Installer.wixproj),
so this is equivalent:

```powershell
dotnet build installer\DVRTool.Installer.wixproj -c Release -p:ProductVersion=1.2.0
```

**From Visual Studio:** run either command in the built-in terminal
(View → Terminal). To build it as a project inside VS instead, install the free
[HeatWave for VS2022](https://www.firegiant.com/heatwave/) extension and add
`installer\DVRTool.Installer.wixproj` to the solution. The project is deliberately
**not** in `DVRTool.slnx`: without HeatWave VS can't load it, and a normal solution
build should not re-publish two self-contained apps every time.

## What it installs

| Piece | Where |
| --- | --- |
| GUI | `C:\Program Files\DVRTool\app\DVRTool.exe` |
| CLI | `C:\Program Files\DVRTool\cli\dvrtool.exe` |
| Start-menu shortcut | always |
| Public-desktop shortcut (`C:\Users\Public\Desktop`) | default on; `DESKTOP_SHORTCUT=0` skips it |
| System `PATH` | `...\DVRTool\cli` appended (so `dvrtool` works in any shell); removed on uninstall |
| Registry | `HKLM\SOFTWARE\DVRTool` — see below |
| Update policy | `HKLM\SOFTWARE\DVRTool\Updates\Enabled` = `UPDATES` (default 1); `UPDATES=0` disables self-update on this machine — see [updates.md](updates.md) |

Discovery key for scripts and other programs (64-bit view):

```
HKLM\SOFTWARE\DVRTool
  InstallDir  REG_SZ  C:\Program Files\DVRTool\
  GuiExe      REG_SZ  C:\Program Files\DVRTool\app\DVRTool.exe
  CliExe      REG_SZ  C:\Program Files\DVRTool\cli\dvrtool.exe
  Version     REG_SZ  1.0.0
```

The Hikvision HCNetSDK native DLLs are **not** bundled (vendor-licensed); as always
the SDK folder is found at runtime via `--sdk-dir` / `OCB_SDK_DIR` / `DVR_SDK_DIR`
or probing of the usual iVMS/HikCentral install paths.

## Installing

```powershell
# Interactive (progress UI, no wizard)
msiexec /i DVRTool-1.0.0.msi

# Fully silent (needs an elevated shell — it's a per-machine install)
msiexec /i DVRTool-1.0.0.msi /qn

# Silent, no desktop shortcut, custom folder, with a log
msiexec /i DVRTool-1.0.0.msi /qn DESKTOP_SHORTCUT=0 INSTALLFOLDER="D:\Tools\DVRTool" /l*v install.log
```

Upgrades are major-upgrade in place: installing a higher `-Version` MSI replaces the
old one (same `UpgradeCode`); installing an older version over a newer one is
refused. This is also exactly what the built-in updater does ([updates.md](updates.md)):
it downloads the next release's MSI, verifies it against a signed manifest, and runs
`msiexec /i … /passive /norestart`. Silent uninstall:

```powershell
msiexec /x DVRTool-1.0.0.msi /qn        # or /x {ProductCode} without the file
```

Note the `PATH` change reaches new shells only — already-open terminals (and
services) keep their old environment until restarted.

## Maintenance notes

- `UpgradeCode` in [installer/Package.wxs](../installer/Package.wxs) is the product's
  permanent identity — **never change it**, or upgrades stop replacing old installs.
- `ProductVersion` must be numeric `x.y.z` (MSI limit: 255.255.65535). It defaults to
  `<Version>` in the root `Directory.Build.props`, which `release.ps1` bumps; bump at
  least the third field for every shipped build, or Windows treats it as the same product.
- The component GUIDs on the PATH/registry/shortcut components are likewise stable
  on purpose; only new components need new GUIDs.
