# Installs (or uninstalls) the KH Video Switcher virtual camera media source.
# Must run elevated: the COM class must be registered in HKLM and the DLLs must
# live where the Windows Frame Server services can read them (ProgramData).
param(
    [switch]$Uninstall,
    [string]$BuildOutput = (Join-Path $PSScriptRoot "..\src\KHVideoSwitcher.VCam\bin\Release\net10.0-windows10.0.22621.0")
)

$ErrorActionPreference = 'Stop'
$installDir = "C:\ProgramData\KHVideoSwitcher\vcam"
$comhost = Join-Path $installDir "KHVideoSwitcher.VCam.comhost.dll"

if ($Uninstall) {
    if (Test-Path $comhost) {
        & regsvr32 /s /u $comhost
        Write-Host "Unregistered $comhost"
    }
    exit 0
}

$BuildOutput = (Resolve-Path $BuildOutput).Path
if (-not (Test-Path (Join-Path $BuildOutput "KHVideoSwitcher.VCam.comhost.dll"))) {
    throw "Build output not found at $BuildOutput - build KHVideoSwitcher.VCam in Release first."
}

New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item "$BuildOutput\*" $installDir -Recurse -Force
& regsvr32 /s $comhost
if ($LASTEXITCODE -ne 0) { throw "regsvr32 failed with exit code $LASTEXITCODE" }
Write-Host "Registered $comhost"
