# Cuts a release. The actual build/test/package/publish now happens in
# GitHub Actions (.github/workflows/release.yml), triggered by the tag this
# script pushes - that's what lets SignPath sign artifacts in a trusted CI
# environment instead of on a maintainer's laptop. This script's job is:
#
#   1. Sanity-check locally that the tree builds and passes tests (catches
#      a broken release before it's public, without waiting on Actions).
#   2. Optionally commit release notes so Actions can find them.
#   3. Tag and push, which triggers the Actions workflow to do the rest.
#
#   scripts\release.ps1 -Version 1.5.0
#   scripts\release.ps1 -Version 1.5.0 -NotesFile notes.md
#   scripts\release.ps1 -Version 1.5.0 -DryRun     # local build+test only, no tag/push
#
# Without -Notes/-NotesFile, the release gets GitHub's auto-generated notes
# (just a changelog link) - prefer writing real notes for anything users must
# act on (a security fix, a breaking change).
#
# Requires: gh CLI authenticated (gh auth login), clean working tree.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes,
    [string]$NotesFile,
    [switch]$DryRun,
    [switch]$AllowDirty,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repo

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $env:Path += ";$env:ProgramFiles\dotnet"
}

$Version = $Version.TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }
$tag = "v$Version"

# Preflight.
$dirty = git status --porcelain
if ($dirty -and -not $AllowDirty) {
    throw "Working tree has uncommitted changes. Commit them first (or pass -AllowDirty):`n$($dirty -join "`n")"
}
git rev-parse -q --verify "refs/tags/$tag" *> $null
if ($LASTEXITCODE -eq 0) { throw "Tag $tag already exists." }
if (-not $DryRun) {
    & gh auth status *> $null
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI is not authenticated. Run: gh auth login" }
}

# The running app locks its exe and silently breaks builds.
Get-Process KHVideoSwitcher -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "== Sanity build: app + virtual camera =="
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "publish.ps1") -Version $Version
if ($LASTEXITCODE -ne 0) { throw "publish.ps1 failed." }

if (-not $SkipTests) {
    $deviceCheck = Join-Path $repo "tools\DeviceCheck"

    Write-Host "== Running shared-channel hardening test =="
    $fuzz = & dotnet run --project $deviceCheck -c Release -- channelfuzz
    $fuzz | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "DeviceCheck failed to run." }
    if ($fuzz -match 'FAILED') { throw "Shared-channel hardening test FAILED - refusing to release." }

    Write-Host "== Running detector self-test =="
    $detect = & dotnet run --project $deviceCheck -c Release -- detecttest
    $detect | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "DeviceCheck failed to run." }
    if ($detect -match 'FAILED') { throw "Detector self-test FAILED - refusing to release." }
}

Write-Host "== Sanity build: installer (Inno Setup) =="
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "build-installer.ps1") -Version $Version
if ($LASTEXITCODE -ne 0) { throw "build-installer.ps1 failed." }
$setupExe = Join-Path $repo "dist-installer\KHVideoSwitcher-Setup-$Version.exe"
if (-not (Test-Path $setupExe)) { throw "Installer exe not found at $setupExe" }
Write-Host ("   OK: {0} ({1:0.0} MB) - this local copy is for your own testing; " -f $setupExe, ((Get-Item $setupExe).Length / 1MB))
Write-Host "   GitHub Actions builds the copy that actually gets published."

if ($DryRun) {
    Write-Host "== Dry run complete (no notes committed, no tag, no push) =="
    exit 0
}

# Commit notes where the Actions workflow will look for them: release-notes\vX.Y.Z.md.
if ($NotesFile -or $Notes) {
    $committedNotes = Join-Path $repo "release-notes\$tag.md"
    New-Item -ItemType Directory -Force (Split-Path $committedNotes) | Out-Null
    if ($NotesFile) {
        if (-not (Test-Path $NotesFile)) { throw "Notes file not found: $NotesFile" }
        Copy-Item $NotesFile $committedNotes -Force
    }
    else {
        Set-Content -Path $committedNotes -Value $Notes
    }
    git add $committedNotes
    git commit -m "Add release notes for $tag"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed." }
}

Write-Host "== Tagging and pushing =="
git tag $tag
git push origin HEAD --tags
if ($LASTEXITCODE -ne 0) { throw "git push failed." }

Write-Host ""
Write-Host "Pushed $tag. GitHub Actions will build, test, package and publish the release:"
Write-Host "  https://github.com/davidpeele/KHVideoSwitcher/actions"
Write-Host "It will appear at:"
Write-Host "  https://github.com/davidpeele/KHVideoSwitcher/releases/tag/$tag"
