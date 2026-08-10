# Builds dist\ (via publish.ps1) then compiles it into a single Windows
# installer exe with Inno Setup: dist-installer\KHVideoSwitcher-Setup-X.Y.Z.exe
#
#   scripts\build-installer.ps1 -Version 1.0.0
#
# Requires Inno Setup 6 (https://jrsoftware.org/isinfo.php).
param(
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Version = $Version.TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php"
}

Write-Host "== Building app + vcam (publish.ps1) =="
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "publish.ps1") -Version $Version
if ($LASTEXITCODE -ne 0) { throw "publish.ps1 failed." }

Write-Host "== Compiling installer (Inno Setup) =="
& "$iscc" "/DAppVersion=$Version" (Join-Path $repo "installer\KHVideoSwitcher.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed." }

$exe = Join-Path $repo "dist-installer\KHVideoSwitcher-Setup-$Version.exe"
Write-Host ""
Write-Host ("Installer ready: {0} ({1:0.0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
