; ============================================================
;  KuroMon installer (Inno Setup 6)
;  Build with scripts\build_installer.bat (passes /DAppVersion=x.y.z)
;  Keep this file ASCII only. Japanese UI text comes from Japanese.isl.
; ============================================================

#define AppName "KuroMon"
#define AppExe "KuroMon.exe"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; Scheduled task created by the app's auto start option (overridable for tests)
#ifndef TaskName
  #define TaskName "KuroMon"
#endif

[Setup]
AppId={{8F3C2B1A-5D4E-4F7A-9B21-6C0E7D9A4B13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\bin
OutputBaseFilename=KuroMon-Setup-{#AppVersion}
SetupIconFile=..\src\KuroMon\Assets\KuroMon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
; Close a running KuroMon (it exits on WM_QUERYENDSESSION) before files are replaced.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\build\publish\KuroMon.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\.env.example"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
; PawnIO driver installer (CPU temperature). Downloaded and hash-checked into bin\ by the build.
Source: "..\vendor\PawnIO_setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: PawnIoNeedsInstall

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; Stop only the KuroMon instance that runs from the install folder.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-Process KuroMon -ErrorAction SilentlyContinue | Where-Object Path -eq '{app}\{#AppExe}' | Stop-Process -Force"""; Flags: runhidden waituntilterminated; RunOnceId: "StopKuroMon"
; Remove the logon task created by the app's auto start option.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#TaskName}"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveAutoStartTask"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\backup"
Type: filesandordirs; Name: "{commonappdata}\KuroMon"

; PawnIO is intentionally NOT uninstalled: other apps (FanControl, LibreHardwareMonitor...) share the same driver.

[Code]
var
  PawnIoRestartNeeded: Boolean;

{ Same rule as the app: install when PawnIO is missing or older than 2.0 (required by LibreHardwareMonitorLib 0.9.6). }
function PawnIoNeedsInstall: Boolean;
var
  Ver: String;
begin
  Result := True;
  if RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO', 'DisplayVersion', Ver) then
    Result := (Length(Ver) = 0) or (Ver[1] < '2');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Setup: String;
begin
  if CurStep <> ssPostInstall then Exit;
  { The driver needs administrator rights; a per-user install (/CURRENTUSER) skips it and the app offers it later. }
  if not IsAdminInstallMode then Exit;
  Setup := ExpandConstant('{tmp}\PawnIO_setup.exe');
  if not FileExists(Setup) then Exit;

  WizardForm.StatusLabel.Caption := 'Installing PawnIO driver (required for CPU temperature)...';
  if Exec(Setup, '-install -silent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 3010 then
      PawnIoRestartNeeded := True
    else if ResultCode <> 0 then
      Log(Format('PawnIO setup failed with code %d', [ResultCode]));
  end
  else
    Log(Format('PawnIO setup could not be started: %s', [SysErrorMessage(ResultCode)]));
end;

function NeedRestart: Boolean;
begin
  Result := PawnIoRestartNeeded;
end;
