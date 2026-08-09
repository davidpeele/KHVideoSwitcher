# Builds, packages, tags, and publishes a GitHub release.
#
#   scripts\release.ps1 -Version 1.1.0
#   scripts\release.ps1 -Version 1.1.0 -Notes "Fixed X, added Y"
#   scripts\release.ps1 -Version 1.1.0 -DryRun     # build + zip only, no tag/publish
#
# Without -Notes, GitHub auto-generates notes from the commits since the last
# release. Requires: gh CLI authenticated (gh auth login), clean working tree.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes,
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
$zip = Join-Path $repo "KHVideoSwitcher-$tag.zip"

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

Write-Host "== Building and packaging $tag =="
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "publish.ps1")
if ($LASTEXITCODE -ne 0) { throw "publish.ps1 failed." }

if (-not $SkipTests) {
    Write-Host "== Running detector self-test =="
    & dotnet run --project (Join-Path $repo "tools\DeviceCheck") -c Release -- detecttest
    if ($LASTEXITCODE -ne 0) { throw "DeviceCheck failed to run." }
}

Write-Host "== Building installer exe =="
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "build-installer.ps1") -Version $Version
if ($LASTEXITCODE -ne 0) { throw "build-installer.ps1 failed." }
$setupExe = Join-Path $repo "dist-installer\KHVideoSwitcher-Setup-$Version.exe"
if (-not (Test-Path $setupExe)) { throw "Installer exe not found at $setupExe" }

Write-Host "== Zipping =="
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $repo "dist\*") -DestinationPath $zip
Write-Host ("   {0} ({1:0.0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))

# Publish checksums so users can verify downloads. (The binaries are not
# Authenticode-signed: a code-signing certificate is a recurring paid expense
# for a volunteer project. SHA-256 is the free substitute, and is why the
# README documents the SmartScreen warning users will see.)
Write-Host "== Writing SHA256SUMS.txt =="
$sums = Join-Path $repo "SHA256SUMS.txt"
Get-FileHash -Algorithm SHA256 $setupExe, $zip |
    ForEach-Object { "{0}  {1}" -f $_.Hash.ToLower(), (Split-Path $_.Path -Leaf) } |
    Set-Content -Encoding ascii $sums
Get-Content $sums | ForEach-Object { Write-Host "   $_" }

if ($DryRun) {
    Write-Host "== Dry run complete (no tag, no release) =="
    exit 0
}

Write-Host "== Tagging and publishing =="
git tag $tag
git push origin HEAD --tags
if ($LASTEXITCODE -ne 0) { throw "git push failed." }

$releaseArgs = @('release', 'create', $tag, $setupExe, $zip, $sums, '--title', "KH Video Switcher $tag")
if ($Notes) { $releaseArgs += @('--notes', $Notes) } else { $releaseArgs += '--generate-notes' }
& gh @releaseArgs
if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }

Write-Host ""
Write-Host "Released: https://github.com/davidpeele/KHVideoSwitcher/releases/tag/$tag"
