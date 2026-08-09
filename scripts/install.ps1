# KH Video Switcher installer. Run from a dist package folder (see publish.ps1).
# Installs the app to Program Files, registers the virtual camera, and creates
# Start Menu / Desktop shortcuts. Self-elevates.
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'

# Self-elevate.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $args2 = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Uninstall) { $args2 += '-Uninstall' }
    Start-Process powershell -ArgumentList $args2 -Verb RunAs -Wait
    exit $LASTEXITCODE
}

$appDir = "$env:ProgramFiles\KH Video Switcher"
$vcamDir = "$env:ProgramData\KHVideoSwitcher\vcam"
$comhost = Join-Path $vcamDir "KHVideoSwitcher.VCam.comhost.dll"
$startMenu = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\KH Video Switcher.lnk"
$desktop = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) "KH Video Switcher.lnk"

if ($Uninstall) {
    Get-Process KHVideoSwitcher -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $comhost) {
        $p = Start-Process regsvr32 -ArgumentList '/s','/u',"`"$comhost`"" -Wait -PassThru
        Write-Host "Virtual camera unregistered (regsvr32 exit $($p.ExitCode))."
    }
    Remove-Item -Recurse -Force $appDir, $vcamDir, $startMenu, $desktop -ErrorAction SilentlyContinue
    Write-Host "Uninstalled."
    Read-Host "Press Enter to close"
    exit 0
}

$dist = $PSScriptRoot
if (-not (Test-Path "$dist\app\KHVideoSwitcher.exe")) {
    throw "Run this script from the dist package folder (app\ and vcam\ must sit beside it)."
}

# Prerequisite: .NET 10 Windows Desktop Runtime (needed by the app AND by the
# virtual camera component loaded into Windows services).
$hasRuntime = $false
try {
    $runtimes = & dotnet --list-runtimes 2>$null
    $hasRuntime = ($runtimes | Where-Object { $_ -match 'Microsoft\.WindowsDesktop\.App 10\.' }).Count -gt 0
} catch {}
if (-not $hasRuntime) {
    Write-Warning ".NET 10 Desktop Runtime not found. Installing via winget..."
    winget install Microsoft.DotNet.DesktopRuntime.10 --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "Could not install the .NET 10 Desktop Runtime. Install it manually from https://dotnet.microsoft.com/download and rerun."
    }
}

Get-Process KHVideoSwitcher -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Installing app to $appDir..."
New-Item -ItemType Directory -Force $appDir | Out-Null
Copy-Item "$dist\app\*" $appDir -Recurse -Force

Write-Host "Installing virtual camera to $vcamDir..."
$vcamRoot = Split-Path $vcamDir -Parent
New-Item -ItemType Directory -Force $vcamDir | Out-Null

# The DLL below is registered machine-wide and loaded by Windows into camera
# processes, so this folder must not be writable by unprivileged users.
# %ProgramData% grants CREATOR OWNER rights on new subfolders, so a standard user
# could pre-create this path, keep write access (and, as owner, implicit WRITE_DAC
# to undo any ACL we set), then swap the DLL later — a local privilege escalation.
# Seize ownership and reset the ACL BEFORE copying the files in.
& icacls.exe $vcamRoot /setowner "*S-1-5-32-544" /T /C /Q | Out-Null
& icacls.exe $vcamRoot /inheritance:r `
    /grant "*S-1-5-18:(OI)(CI)F" `
    /grant "*S-1-5-32-544:(OI)(CI)F" `
    /grant "*S-1-5-32-545:(OI)(CI)RX" `
    /grant "*S-1-15-2-1:(OI)(CI)RX" /T /C /Q | Out-Null

Copy-Item "$dist\vcam\*" $vcamDir -Recurse -Force
$p = Start-Process regsvr32 -ArgumentList '/s',"`"$comhost`"" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Virtual camera registration failed (regsvr32 exit $($p.ExitCode))." }

Write-Host "Creating shortcuts..."
$shell = New-Object -ComObject WScript.Shell
foreach ($lnk in @($startMenu, $desktop)) {
    $sc = $shell.CreateShortcut($lnk)
    $sc.TargetPath = Join-Path $appDir "KHVideoSwitcher.exe"
    $sc.WorkingDirectory = $appDir
    $sc.Description = "KH Video Switcher"
    $sc.Save()
}

Write-Host ""
Write-Host "Installed. Launch 'KH Video Switcher' from the Start Menu or Desktop."
Read-Host "Press Enter to close"
