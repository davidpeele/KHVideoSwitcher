# KH Video Switcher installer. Run from a dist package folder (see publish.ps1).
#
#   install.ps1              installs for all users (self-elevates): the app to
#                            Program Files plus the machine-wide virtual camera
#   install.ps1 -PerUser     installs the app only, into %LOCALAPPDATA%, with no
#                            administrator rights. The virtual camera works if an
#                            administrator already registered it on this machine
#                            (registration is machine-wide and serves every user
#                            account); otherwise everything except virtual camera
#                            output works.
#   install.ps1 -Uninstall   add -PerUser to remove a per-user install
param([switch]$Uninstall, [switch]$PerUser)

$ErrorActionPreference = 'Stop'

# Self-elevate, except for a per-user install which must not need admin at all.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $PerUser) {
    $args2 = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Uninstall) { $args2 += '-Uninstall' }
    Start-Process powershell -ArgumentList $args2 -Verb RunAs -Wait
    exit $LASTEXITCODE
}

if ($PerUser) {
    $appDir = "$env:LOCALAPPDATA\Programs\KH Video Switcher"
    $startMenu = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\KH Video Switcher.lnk"
    $desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) "KH Video Switcher.lnk"
}
else {
    $appDir = "$env:ProgramFiles\KH Video Switcher"
    $startMenu = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\KH Video Switcher.lnk"
    $desktop = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) "KH Video Switcher.lnk"
}
# The virtual camera always lives machine-wide; a per-user install never touches it.
$vcamDir = "$env:ProgramData\KHVideoSwitcher\vcam"
$comhost = Join-Path $vcamDir "KHVideoSwitcher.VCam.comhost.dll"
$vcamClsidKey = 'HKLM:\SOFTWARE\Classes\CLSID\{8ae54092-501b-4c01-afe0-b55cef94eb2d}\InprocServer32'

if ($Uninstall) {
    Get-Process KHVideoSwitcher -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($PerUser) {
        # Leave the machine-wide virtual camera alone: other user accounts on
        # this computer may still be using it.
        Remove-Item -Recurse -Force $appDir, $startMenu, $desktop -ErrorAction SilentlyContinue
        Write-Host "Per-user install removed (the machine-wide virtual camera was left in place)."
    }
    else {
        if (Test-Path $comhost) {
            $p = Start-Process regsvr32 -ArgumentList '/s','/u',"`"$comhost`"" -Wait -PassThru
            Write-Host "Virtual camera unregistered (regsvr32 exit $($p.ExitCode))."
        }
        Remove-Item -Recurse -Force $appDir, $vcamDir, $startMenu, $desktop -ErrorAction SilentlyContinue
        Write-Host "Uninstalled."
    }
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
    if ($PerUser) {
        # The runtime is a machine-wide package; winget will raise its own UAC prompt.
        Write-Warning ".NET 10 Desktop Runtime not found. Installing it needs administrator approval."
    }
    else {
        Write-Warning ".NET 10 Desktop Runtime not found. Installing via winget..."
    }
    winget install Microsoft.DotNet.DesktopRuntime.10 --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "Could not install the .NET 10 Desktop Runtime. Install it manually from https://dotnet.microsoft.com/download and rerun."
    }
}

Get-Process KHVideoSwitcher -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Installing app to $appDir..."
New-Item -ItemType Directory -Force $appDir | Out-Null
Copy-Item "$dist\app\*" $appDir -Recurse -Force

if ($PerUser) {
    if (Test-Path $vcamClsidKey) {
        Write-Host "Virtual camera: already registered on this computer — it will work for this account."
    }
    else {
        Write-Warning ("Virtual camera: NOT installed (that part needs administrator rights and " +
            "registers for the whole computer). Everything else works; to send video to Zoom, " +
            "have an administrator run install.ps1 without -PerUser once.")
    }
}
else {
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
}

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
