<#
.SYNOPSIS
  Cuts a DVRTool release: bump, tag, build the MSI, sign the update manifest, publish to GitHub.

.DESCRIPTION
  One command turns a clean main into a GitHub Release that every installed DVRTool can find:
    1. refuses on a dirty tree, an existing tag, or a version that is not above the current one
    2. writes <Version> into Directory.Build.props and commits "Release x.y.z"
    3. tags vx.y.z
    4. build-installer.ps1 -Version x.y.z  ->  artifacts\installer\DVRTool-x.y.z.msi
    5. tools\DVRTool.ReleaseSign sign      ->  update.json + update.json.sig (Ed25519, local key)
    6. gh release create vx.y.z with the three assets (pushes main and the tag first)

  Signing happens HERE, on this machine, with the key in %USERPROFILE%\.dvrtool-release —
  never in CI. An installed DVRTool trusts the key, not github.com. See docs/updates.md.

.EXAMPLE
  .\release.ps1 -Version 1.2.0
  .\release.ps1 -Version 1.2.1 -PreRelease      # invisible to the fleet until promoted
  .\release.ps1 -Version 1.2.0 -Notes "Fixes the Dahua 6 h playback ceiling."
  .\release.ps1 -Version 1.2.0 -NoPublish       # everything but the GitHub release
#>
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [string]$Notes,
    [switch]$PreRelease,
    [switch]$NoPublish,
    [string]$Repo = "Key-Information-Solutions/DVRtool"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Set-Location $root

function Fail($msg) { Write-Host "release: $msg" -ForegroundColor Red; exit 1 }

# --- preconditions --------------------------------------------------------------------------
if (git status --porcelain) { Fail "working tree is not clean — commit or stash first." }
$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne "main") { Fail "on '$branch'; releases are cut from main." }
if (git tag --list "v$Version") { Fail "tag v$Version already exists." }

$props = Join-Path $root "Directory.Build.props"
$current = ([xml](Get-Content $props)).Project.PropertyGroup.Version
if ([version]$Version -le [version]$current) {
    Fail "version $Version is not above the current $current (Directory.Build.props)."
}

if (-not $NoPublish) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail "gh CLI not found." }
    gh auth status 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "gh is not authenticated (gh auth login)." }
}

$keyFile = Join-Path $env:USERPROFILE ".dvrtool-release\update-signing.key"
if (-not (Test-Path $keyFile)) {
    Fail "no signing key at $keyFile — run: dotnet run --project tools\DVRTool.ReleaseSign -- keygen"
}

# --- tests must pass before anything is stamped ---------------------------------------------
Write-Host "Running tests..." -ForegroundColor Cyan
dotnet test (Join-Path $root "tests\DVRTool.Tests") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail "tests failed." }

# --- bump + commit + tag ----------------------------------------------------------------------
Write-Host "Bumping $current -> $Version" -ForegroundColor Cyan
(Get-Content $props -Raw) -replace "<Version>$([regex]::Escape($current))</Version>", "<Version>$Version</Version>" |
    Set-Content $props -NoNewline
if (-not (git status --porcelain)) { Fail "Directory.Build.props did not change." }
git add $props
git commit -q -m "Release $Version"
if ($LASTEXITCODE -ne 0) { Fail "commit failed." }
git tag -a "v$Version" -m "DVRTool $Version"

# --- build MSI ----------------------------------------------------------------------------------
Write-Host "Building installer..." -ForegroundColor Cyan
& (Join-Path $root "build-installer.ps1") -Version $Version
if ($LASTEXITCODE -ne 0) { Fail "installer build failed." }
$msi = Join-Path $root "artifacts\installer\DVRTool-$Version.msi"
if (-not (Test-Path $msi)) { Fail "$msi was not produced." }

# --- sign manifest ---------------------------------------------------------------------------------
Write-Host "Signing update manifest..." -ForegroundColor Cyan
$releaseDir = Join-Path $root "artifacts\release\$Version"
New-Item -ItemType Directory -Force $releaseDir | Out-Null
$notesUrl = "https://github.com/$Repo/releases/tag/v$Version"
dotnet run --project (Join-Path $root "tools\DVRTool.ReleaseSign") -c Release -- `
    sign --msi $msi --version $Version --out $releaseDir --notes $notesUrl
if ($LASTEXITCODE -ne 0) { Fail "signing failed." }
$manifest = Join-Path $releaseDir "update.json"
$sig      = Join-Path $releaseDir "update.json.sig"
dotnet run --project (Join-Path $root "tools\DVRTool.ReleaseSign") -c Release -- verify --manifest $manifest --sig $sig
if ($LASTEXITCODE -ne 0) { Fail "the signed manifest does not verify against the key compiled into this build." }

Copy-Item $msi $releaseDir -Force
Write-Host ""
Write-Host "Release assets in $releaseDir" -ForegroundColor Green
Get-ChildItem $releaseDir | Format-Table Name, Length -AutoSize

if ($NoPublish) {
    Write-Host "-NoPublish: commit and tag are local only. To publish later:"
    Write-Host "  git push origin main --follow-tags"
    Write-Host "  gh release create v$Version `"$releaseDir\DVRTool-$Version.msi`" `"$manifest`" `"$sig`" --title `"DVRTool $Version`" --generate-notes"
    exit 0
}

# --- publish -----------------------------------------------------------------------------------------
Write-Host "Pushing main and v$Version..." -ForegroundColor Cyan
git push -q origin main
if ($LASTEXITCODE -ne 0) { Fail "push failed (the commit and tag are local; fix and push by hand)." }
git push -q origin "v$Version"
if ($LASTEXITCODE -ne 0) { Fail "tag push failed." }

$ghArgs = @("release", "create", "v$Version",
    (Join-Path $releaseDir "DVRTool-$Version.msi"), $manifest, $sig,
    "--repo", $Repo, "--title", "DVRTool $Version", "--verify-tag")
if ($Notes) { $ghArgs += @("--notes", $Notes) } else { $ghArgs += "--generate-notes" }
if ($PreRelease) { $ghArgs += "--prerelease" }
Write-Host "Creating GitHub release..." -ForegroundColor Cyan
& gh @ghArgs
if ($LASTEXITCODE -ne 0) { Fail "gh release create failed (tag is pushed; create the release by hand with the assets above)." }

Write-Host ""
if ($PreRelease) {
    Write-Host "Published as PRE-RELEASE: installed DVRTools will not see it until it is promoted" -ForegroundColor Yellow
    Write-Host "(gh release edit v$Version --prerelease=false). Install it by hand at one site first."
} else {
    Write-Host "Published. Every installed DVRTool offers $Version on its next daily check." -ForegroundColor Green
}
Write-Host "https://github.com/$Repo/releases/tag/v$Version"
