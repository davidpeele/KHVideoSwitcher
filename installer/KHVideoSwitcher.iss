; Inno Setup script for KH Video Switcher.
; Build with: scripts\build-installer.ps1 (runs publish.ps1 then ISCC.exe on this file).
;
; #Version is passed in via /DAppVersion=X.Y.Z from the build script; default
; below is only used if someone compiles this .iss directly in the IDE.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "KH Video Switcher"
#define AppPublisher "David Peele"
#define AppURL "https://github.com/davidpeele/KHVideoSwitcher"
#define AppExeName "KHVideoSwitcher.exe"
#define VCamComHost "KHVideoSwitcher.VCam.comhost.dll"

; Pinned .NET 10 Desktop Runtime, fetched only when the machine has no 10.x
; runtime. Verified against this SHA-256 before it is executed. To update:
; take the new URL + SHA-512 from
; https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json,
; confirm the download against that SHA-512, then record its SHA-256 here.
#define DotNetRuntimeUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.10/windowsdesktop-runtime-10.0.10-win-x64.exe"
#define DotNetInstallerName "windowsdesktop-runtime-10.0.10-win-x64.exe"
#define DotNetRuntimeSha256 "e82fc901c8f52d716293b2bc0830ce0dd254a06268c457a19e8fc503560a84d1"

[Setup]
AppId={{6C6B6E9B-6C6E-4C7E-9C1C-2B7B8C1E3D4A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Two install modes (the user picks on the first wizard page):
;   * For all users (admin) - installs the app AND registers the virtual camera.
;     The camera's COM component must be registered in HKLM and be readable by
;     the Windows Frame Server services, which is why that mode needs admin.
;   * Just for me (no admin) - installs the app only, into %LOCALAPPDATA%. The
;     virtual camera works if some admin already registered it on this machine
;     (it is machine-wide, so one install serves every user account); otherwise
;     everything except virtual camera output works.
; "dialog" shows the mode chooser; "commandline" allows /ALLUSERS and
; /CURRENTUSER for unattended deployment.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog
; Always ask which mode to use. Inno defaults to silently reusing the mode of a
; previous install, which traps anyone who installed "just for me" first and
; then wants to add the machine-wide virtual camera: re-running setup would go
; straight back to per-user with no way to change it.
UsePreviousPrivileges=no
OutputDir=..\dist-installer
OutputBaseFilename=KHVideoSwitcher-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\KHVideoSwitcher\Resources\KHVideoSwitcher.ico
UninstallDisplayIcon={app}\{#AppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
LicenseFile=..\LICENSE
; Let Restart Manager close a running copy during an upgrade, instead of
; shelling out to taskkill.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; The app (framework-dependent publish output).
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The virtual camera component, installed to ProgramData so the Windows Frame
; Server services (running as SYSTEM/LOCAL SERVICE) can load it - HKLM-only
; registration requires this, it cannot live under the per-user app folder.
; Admin install mode only: a per-user install can neither write here nor
; register machine-wide.
Source: "..\dist\vcam\*"; DestDir: "{commonappdata}\KHVideoSwitcher\vcam"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsAdminInstallMode

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "regsvr32.exe"; Parameters: "/s ""{commonappdata}\KHVideoSwitcher\vcam\{#VCamComHost}"""; \
    StatusMsg: "Registering the virtual camera..."; Flags: runhidden; Check: IsAdminInstallMode
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; Records whether THIS install registered the shared virtual camera. The
; uninstaller reads it back and only removes the camera if this install put it
; there. Deliberately not done with Check: IsAdminInstallMode on [UninstallRun]
; / [UninstallDelete]: that is evaluated in the uninstaller's own context, which
; is not a reliable statement about what the original install actually did, and
; getting it wrong tears the shared camera away from other accounts.
[Registry]
Root: HKA; Subkey: "Software\KHVideoSwitcher"; ValueType: dword; ValueName: "InstalledVCam"; \
    ValueData: "1"; Flags: uninsdeletevalue; Check: IsAdminInstallMode
Root: HKA; Subkey: "Software\KHVideoSwitcher"; ValueType: dword; ValueName: "InstalledVCam"; \
    ValueData: "0"; Flags: uninsdeletevalue; Check: not IsAdminInstallMode

[Code]
// .NET 10 Desktop Runtime is required to run the app AND to load the virtual
// camera component into Windows services. Detected by looking for an installed
// 10.x shared framework directly on disk, rather than shelling out to
// `dotnet --list-runtimes` - no child process, faster, and it avoids a
// cmd.exe command line that antivirus heuristics score against installers.
function IsDesktopRuntimeInstalled(): Boolean;
var
  Base: String;
  FindRec: TFindRec;
begin
  Result := False;
  Base := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(Base) then
    Exit;

  if FindFirst(Base + '\10.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// The virtual camera DLL is registered machine-wide and loaded by Windows into
// camera processes, so its folder must not be writable by unprivileged users.
// %ProgramData% grants CREATOR OWNER rights on new subfolders, so a standard
// user could pre-create this path, keep write access (and, as owner, implicit
// WRITE_DAC to undo any ACL we set), then swap the DLL later - a local privilege
// escalation. Seize ownership and reset the ACL BEFORE any files are copied in.
procedure SecureVCamDirectory();
var
  Dir: String;
  ResultCode: Integer;
begin
  Dir := ExpandConstant('{commonappdata}\KHVideoSwitcher');
  if not DirExists(Dir) then
    ForceDirectories(Dir);

  // Owner -> Administrators, so nobody else retains implicit WRITE_DAC.
  Exec(ExpandConstant('{sys}\icacls.exe'), '"' + Dir + '" /setowner *S-1-5-32-544 /T /C /Q',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Drop inherited ACEs, then grant: SYSTEM and Administrators full control,
  // Users and ALL APPLICATION PACKAGES read+execute only.
  Exec(ExpandConstant('{sys}\icacls.exe'),
       '"' + Dir + '" /inheritance:r' +
       ' /grant *S-1-5-18:(OI)(CI)F' +
       ' /grant *S-1-5-32-544:(OI)(CI)F' +
       ' /grant *S-1-5-32-545:(OI)(CI)RX' +
       ' /grant *S-1-15-2-1:(OI)(CI)RX /T /C /Q',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// True if this machine already has the virtual camera registered (by an
// earlier admin install). A per-user install can then still use it, because
// registration is machine-wide and serves every user account.
function IsVCamRegistered(): Boolean;
begin
  Result := RegKeyExists(HKEY_LOCAL_MACHINE,
    'SOFTWARE\Classes\CLSID\{8ae54092-501b-4c01-afe0-b55cef94eb2d}\InprocServer32');
end;

// Uninstall: remove the shared virtual camera ONLY if this same install put it
// there, as recorded under Software\KHVideoSwitcher at install time. A per-user
// install never sets that flag, so uninstalling it leaves the machine-wide
// camera (and other user accounts that depend on it) untouched.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  InstalledVCam: Cardinal;
  ResultCode: Integer;
  ComHost: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  InstalledVCam := 0;
  if not RegQueryDWordValue(HKEY_LOCAL_MACHINE, 'Software\KHVideoSwitcher', 'InstalledVCam', InstalledVCam) then
    if not RegQueryDWordValue(HKEY_CURRENT_USER, 'Software\KHVideoSwitcher', 'InstalledVCam', InstalledVCam) then
      InstalledVCam := 0;

  if InstalledVCam <> 1 then
    Exit;

  ComHost := ExpandConstant('{commonappdata}\KHVideoSwitcher\vcam\{#VCamComHost}');
  if FileExists(ComHost) then
    Exec('regsvr32.exe', '/s /u "' + ComHost + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  DelTree(ExpandConstant('{commonappdata}\KHVideoSwitcher'), True, True, True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssInstall) and IsAdminInstallMode() then
    SecureVCamDirectory();

  // After a per-user install, be explicit about what the user just got, so
  // "the virtual camera is missing" is never a mystery.
  if (CurStep = ssPostInstall) and (not IsAdminInstallMode()) and (not WizardSilent()) then
  begin
    if IsVCamRegistered() then
      MsgBox('Installed for your account only.' + #13#10#13#10 +
             'The KH Video Switcher virtual camera is already installed on this computer, ' +
             'so everything works normally, including output to Zoom.',
             mbInformation, MB_OK)
    else
      MsgBox('Installed for your account only.' + #13#10#13#10 +
             'The virtual camera was NOT installed, because that part requires administrator ' +
             'rights and registers itself for the whole computer.' + #13#10#13#10 +
             'Everything else works: camera, pan/zoom, presets, scenes, media capture and ' +
             'automatic switching. To send video to Zoom, run this installer again and choose ' +
             '"Install for all users" (an administrator will need to approve it once).',
             mbInformation, MB_OK);
  end;
end;

// Downloads and quietly installs the .NET 10 Desktop Runtime. Never shows a
// dialog itself - callers decide what to tell the user, if anyone's there to
// tell. Returns True only if the runtime is genuinely present afterward.
//
// The download uses Inno's in-process downloader (WinHTTP) and verifies the
// file against a pinned SHA-256 before it is ever executed. That is stronger
// than checking an Authenticode signature - it pins one exact known artifact -
// and it avoids spawning curl.exe or powershell.exe, whose command lines are
// heavily weighted by antivirus heuristics as downloader/dropper behaviour.
//
// The pinned build is a specific patch release rather than the rolling
// "latest" aka.ms link, so the hash stays valid. Bumping it is a two-line
// change: take the new URL and its SHA-512 from
// https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
// then record the file's SHA-256 here. Machines that already have any 10.x
// desktop runtime skip the download entirely.
function InstallDesktopRuntimeQuietly(): Boolean;
var
  ResultCode: Integer;
  DownloadPath: String;
begin
  Result := False;
  DownloadPath := ExpandConstant('{tmp}\{#DotNetInstallerName}');
  // Never reuse a file that's already sitting at the target path.
  DeleteFile(DownloadPath);

  try
    // Raises on any transport failure or hash mismatch; the file is discarded
    // rather than executed in that case.
    DownloadTemporaryFile('{#DotNetRuntimeUrl}', '{#DotNetInstallerName}',
                          '{#DotNetRuntimeSha256}', nil);
  except
    Exit;
  end;

  if not FileExists(DownloadPath) then
    Exit;

  if not Exec(DownloadPath, '/install /quiet /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
    Exit;
  // 0 = installed, 3010 = installed, reboot required.
  if (ResultCode <> 0) and (ResultCode <> 3010) then
    Exit;

  Result := IsDesktopRuntimeInstalled();
end;

// Runs after the wizard (so the chosen install mode is known) and before files
// are copied. Closes any running instance so the exe isn't locked, then makes
// sure the .NET runtime is present - returning a non-empty string aborts the
// install with that message, which is the honest outcome since the app cannot
// start without it.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  // A running instance is closed by Restart Manager (CloseApplications=yes)
  // rather than by shelling out to taskkill.

  if IsDesktopRuntimeInstalled() then
    Exit;

  if WizardSilent() then
  begin
    // Unattended: best effort, never block on a dialog nobody can answer.
    InstallDesktopRuntimeQuietly();
  end
  else if IsAdminInstallMode() then
  begin
    // Already elevated - no further prompt beyond this confirmation.
    if MsgBox('KH Video Switcher requires the .NET 10 Desktop Runtime, which was not found.' + #13#10#13#10 +
              'Download and install it now?', mbConfirmation, MB_OKCANCEL) = IDOK then
      InstallDesktopRuntimeQuietly();
  end
  else
  begin
    // Per-user install: the runtime is a machine-wide package, so Microsoft's
    // installer raises its own administrator prompt.
    if MsgBox('KH Video Switcher requires the .NET 10 Desktop Runtime, which was not found.' + #13#10#13#10 +
              'It can be downloaded now, but installing it needs administrator approval ' +
              '(Windows will ask). If nobody can approve it, ask an administrator to install ' +
              'the .NET 10 Desktop Runtime, then run this setup again.' + #13#10#13#10 +
              'Try now?', mbConfirmation, MB_OKCANCEL) = IDOK then
      InstallDesktopRuntimeQuietly();
  end;

  if not IsDesktopRuntimeInstalled() then
    Result := 'The .NET 10 Desktop Runtime is required and is not installed, so KH Video ' +
              'Switcher would not be able to start.' + #13#10#13#10 +
              'Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run ' +
              'this setup again.';
end;
