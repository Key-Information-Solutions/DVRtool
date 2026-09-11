<#
.SYNOPSIS
  Builds the DVRTool MSI installer (GUI + CLI, self-contained win-x64).

.DESCRIPTION
  Publishes DVRTool.App and DVRTool.Cli self-contained for win-x64, then packages
  them with WiX into artifacts\installer\DVRTool-<version>.msi. The WiX toolset is
  a NuGet SDK — nothing to install beyond the .NET SDK.

.EXAMPLE
  .\build-installer.ps1                    # the version in Directory.Build.props
  .\build-installer.ps1 -Version 1.2.0
  .\build-installer.ps1 -SkipPublish      # repackage existing publish output only
#>
param(
    [ValidatePattern('^(\d+\.\d+\.\d+)?$')]
    [string]$Version = "",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# Default to the product version every assembly is stamped with, so an MSI built here carries
# the number the running app compares against a release manifest (release.ps1 bumps it).
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
    if (-not $Version) { throw "No <Version> in Directory.Build.props and none passed." }
}

$buildArgs = @(
    "build", (Join-Path $root "installer\DVRTool.Installer.wixproj"),
    "-c", "Release",
    "-p:ProductVersion=$Version"
)
if ($SkipPublish) { $buildArgs += "-p:SkipAppPublish=true" }

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Installer build failed (exit $LASTEXITCODE)." }

$msi = Join-Path $root "artifacts\installer\DVRTool-$Version.msi"
if (-not (Test-Path $msi)) { throw "Build reported success but $msi was not produced." }

Write-Host ""
Write-Host "Installer: $msi" -ForegroundColor Green
Write-Host "  Interactive install:  msiexec /i `"$msi`""
Write-Host "  Silent install:       msiexec /i `"$msi`" /qn"
Write-Host "  Silent, no desktop shortcut: msiexec /i `"$msi`" /qn DESKTOP_SHORTCUT=0"
