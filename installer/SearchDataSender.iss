; The installer of SearchDataSender.exe (Inno Setup 6): for this user only, without an administrator, into
; %LOCALAPPDATA%\Programs\SearchDataSender, the folder the background sender runs from (Places.InstallDir). A Start menu
; entry, a desktop one if chosen, and an entry in Settings › Apps to remove it. Removing it runs
; SearchDataSender.exe --uninstall first (Uninstall.cs): the question whether to delete from the server what was sent,
; the background sender stopped and out of Windows' startup, the tool's settings and data gone.
; CI builds it: ISCC /DAppVersion=<x.y.z> installer\SearchDataSender.iss -> dist\SearchDataSender-Setup.exe
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "שליחת נתוני חיפוש"

[Setup]
; Uninstall.AppId: the entry in Settings › Apps is HKCU\...\Uninstall\<AppId>_is1
AppId={{623648DC-7C9D-4C51-8DA7-127FFFC75A23}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=efrul
AppPublisherURL=https://github.com/efrul/semantic-query-telemetry
AppSupportURL=https://github.com/efrul/semantic-query-telemetry/issues
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\SearchDataSender
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\SearchDataSender.exe
UninstallDisplayName={#AppName}
OutputDir=..\dist
OutputBaseFilename=SearchDataSender-Setup
Compression=lzma2
SolidCompression=yes
; An open window holds this; Setup and the uninstaller ask to close it
AppMutex=Local\SearchDataSender.Window
CloseApplications=no

[Languages]
Name: "he"; MessagesFile: "compiler:Languages\Hebrew.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\SearchDataSender.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\SearchDataSender.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\SearchDataSender.exe"; Tasks: desktopicon

[Run]
; An update: the background sender, if it was on, starts again from the new file
Filename: "{app}\SearchDataSender.exe"; Parameters: "--background"; Flags: nowait runhidden; Check: BackgroundWasOn
Filename: "{app}\SearchDataSender.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\SearchDataSender.exe"; Parameters: "--uninstall"; RunOnceId: "Remove"; Check: not UninstallSilent
Filename: "{app}\SearchDataSender.exe"; Parameters: "--uninstall --quiet"; RunOnceId: "RemoveQuiet"; Flags: runhidden; Check: UninstallSilent

[Code]
const
  EVENT_MODIFY_STATE = $0002;

function OpenEvent(DesiredAccess: Cardinal; InheritHandle: Integer; Name: String): Integer;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: Integer): Integer;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: Integer): Integer;
  external 'CloseHandle@kernel32.dll stdcall';

var
  BackgroundOn: Boolean;

{ Asks the background sender, if it runs, to stop, and waits for it a little: its file is about to be replaced or removed }
procedure StopBackground;
var
  Handle, I: Integer;
begin
  Handle := OpenEvent(EVENT_MODIFY_STATE, 0, 'Local\SearchDataSender.Exit');
  if Handle = 0 then
    Exit;
  SetEvent(Handle);
  CloseHandle(Handle);
  for I := 1 to 40 do
  begin
    if not CheckForMutexes('Local\SearchDataSender.Background') then
      Exit;
    Sleep(250);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  BackgroundOn := RegValueExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SearchDataSender');
  StopBackground;
  Result := '';
end;

function BackgroundWasOn: Boolean;
begin
  Result := BackgroundOn;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopBackground;
end;
