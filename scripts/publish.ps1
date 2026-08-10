# Builds a distributable package in .\dist:
#   dist\app\    - the switcher application (framework-dependent, x64)
#   dist\vcam\   - the virtual camera media source
#   dist\install.ps1
# Copy the dist folder to the target machine and run install.ps1 there.
param(
    [string]$OutDir = (Join-Path $PSScriptRoot "..\dist"),
    # Stamped into the app's AssemblyVersion/FileVersion/InformationalVersion so it
    # can read its own version at runtime (in-app update check). Left at the MSBuild
    # default (1.0.0.0) for ad-hoc local builds - only release.ps1/build-installer.ps1
    # pass a real one, sourced from the release tag.
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $env:Path += ";$env:ProgramFiles\dotnet"
}

if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Force "$OutDir\app" | Out-Null
New-Item -ItemType Directory -Force "$OutDir\vcam" | Out-Null

Write-Host "Publishing app..."
[string[]]$versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }
dotnet publish "$repo\src\KHVideoSwitcher\KHVideoSwitcher.csproj" -c Release -r win-x64 --no-self-contained -o "$OutDir\app" @versionArgs
if ($LASTEXITCODE -ne 0) { throw "app publish failed" }

Write-Host "Building virtual camera..."
dotnet build "$repo\src\KHVideoSwitcher.VCam\KHVideoSwitcher.VCam.csproj" -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw "vcam build failed" }
Copy-Item "$repo\src\KHVideoSwitcher.VCam\bin\Release\net10.0-windows10.0.22621.0\*" "$OutDir\vcam" -Recurse -Force

Copy-Item "$PSScriptRoot\install.ps1" $OutDir -Force

Write-Host ""
Write-Host "Package ready: $((Resolve-Path $OutDir).Path)"
Write-Host "On the target machine: right-click install.ps1 -> Run with PowerShell (accept the admin prompt)."
