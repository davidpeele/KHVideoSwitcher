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
; The virtual camera COM component must be registered in HKLM and be
; readable by the Windows Frame Server services, so admin is required.
PrivilegesRequired=admin
OutputDir=..\dist-installer
OutputBaseFilename=KHVideoSwitcher-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
LicenseFile=..\LICENSE

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
Source: "..\dist\vcam\*"; DestDir: "{commonappdata}\KHVideoSwitcher\vcam"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "regsvr32.exe"; Parameters: "/s ""{commonappdata}\KHVideoSwitcher\vcam\{#VCamComHost}"""; \
    StatusMsg: "Registering the virtual camera..."; Flags: runhidden
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "regsvr32.exe"; Parameters: "/s /u ""{commonappdata}\KHVideoSwitcher\vcam\{#VCamComHost}"""; \
    RunOnceId: "UnregVCam"; Flags: runhidden

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\KHVideoSwitcher"

[Code]
// .NET 10 Desktop Runtime is required to run the app AND to load the virtual
// camera component into Windows services. Detect it via `dotnet --list-runtimes`;
// if missing (or dotnet itself isn't found), offer to fetch the installer.
function IsDesktopRuntimeInstalled(): Boolean;
var
  ResultCode: Integer;
  TmpFile: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := False;
  TmpFile := ExpandConstant('{tmp}\dotnet-runtimes.txt');
  if Exec(ExpandConstant('{cmd}'), '/C dotnet --list-runtimes > "' + TmpFile + '" 2>&1',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringsFromFile(TmpFile, Lines) then
      for I := 0 to GetArrayLength(Lines) - 1 do
        if Pos('Microsoft.WindowsDesktop.App 10.', Lines[I]) > 0 then
        begin
          Result := True;
          Break;
        end;
  end;
end;

// Close a running instance before copying files (e.g. re-running setup to
// upgrade) so the exe isn't locked.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM {#AppExeName} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    SecureVCamDirectory();
end;

// Verifies a downloaded file carries a valid Authenticode signature issued to
// Microsoft. This runs elevated and the result is executed, so an unsigned or
// tampered download must never be launched.
function IsSignedByMicrosoft(const Path: String): Boolean;
var
  ResultCode: Integer;
  MarkerFile: String;
  Lines: TArrayOfString;
begin
  Result := False;
  MarkerFile := ExpandConstant('{tmp}\sigcheck.txt');
  DeleteFile(MarkerFile);
  // Status must be Valid AND the signing certificate subject must be Microsoft.
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -ExecutionPolicy Bypass -Command "' +
       '$s = Get-AuthenticodeSignature -LiteralPath ''' + Path + '''; ' +
       'if ($s.Status -eq ''Valid'' -and $s.SignerCertificate.Subject -match ''O=Microsoft Corporation'') ' +
       '{ ''OK'' | Out-File -Encoding ascii ''' + MarkerFile + ''' }"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if FileExists(MarkerFile) and LoadStringsFromFile(MarkerFile, Lines) then
    Result := (GetArrayLength(Lines) > 0) and (Pos('OK', Lines[0]) > 0);
end;

// Downloads and quietly installs the .NET 10 Desktop Runtime. Never shows a
// dialog itself - callers decide what to tell the user, if anyone's there to
// tell. Returns True only if the runtime is genuinely present afterward.
function InstallDesktopRuntimeQuietly(): Boolean;
var
  ResultCode: Integer;
  DownloadPath: String;
begin
  Result := False;
  DownloadPath := ExpandConstant('{tmp}\windowsdesktop-runtime-10-win-x64.exe');
  // Never reuse a file that's already sitting at the target path.
  DeleteFile(DownloadPath);

  // curl.exe directly (no cmd.exe wrapper); -f fails on HTTP errors, --proto
  // and --tlsv1.2 keep the transfer on modern HTTPS, and we check the exit code.
  if not Exec(ExpandConstant('{sys}\curl.exe'),
              '-fsSL --proto "=https" --tlsv1.2 -o "' + DownloadPath + '" ' +
              'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Exit;
  if (ResultCode <> 0) or (not FileExists(DownloadPath)) then
    Exit;

  // Only execute it if Microsoft actually signed it.
  if not IsSignedByMicrosoft(DownloadPath) then
  begin
    DeleteFile(DownloadPath);
    Exit;
  end;

  if not Exec(DownloadPath, '/install /quiet /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
    Exit;
  // 0 = installed, 3010 = installed, reboot required.
  if (ResultCode <> 0) and (ResultCode <> 3010) then
    Exit;

  Result := IsDesktopRuntimeInstalled();
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if IsDesktopRuntimeInstalled() then
    Exit;

  // Silent/unattended install (e.g. scripted deployment): never block on a
  // dialog nobody can answer. Best-effort install the runtime and continue
  // either way - the app will simply fail to launch if it's still missing,
  // same as any prerequisite-less silent install.
  if WizardSilent() then
  begin
    InstallDesktopRuntimeQuietly();
    Exit;
  end;

  if MsgBox('KH Video Switcher requires the .NET 10 Desktop Runtime, which was not found on this computer.' + #13#10#13#10 +
            'Click OK to download and install it now (opens the official Microsoft installer), or Cancel to install it yourself later from https://dotnet.microsoft.com/download/dotnet/10.0',
            mbConfirmation, MB_OKCANCEL) = IDOK then
  begin
    if not InstallDesktopRuntimeQuietly() then
      MsgBox('The download could not be completed automatically. Please install the .NET 10 Desktop Runtime ' +
             'from https://dotnet.microsoft.com/download/dotnet/10.0 and run this setup again.', mbError, MB_OK);
  end;
end;
