; The release's setup.exe: a per-user install of the published app, with no
; admin rights. Built by tools\Package-Release.ps1, which passes the version
; and the publish folder:
;
;   ISCC.exe /DAppVersion=0.6.1 /DSourceDir=<publish folder> /O<out dir> installer\AIUsage.iss
;
; It installs where tools\Install.ps1 does (%LOCALAPPDATA%\Programs\AI Usage),
; so either can replace the other: each removes the other's Installed apps
; entry. Like the script, it never touches the data folder
; (%LOCALAPPDATA%\AI Usage Native), and closes a running copy by the path it
; runs from, never by name.
;
; Keep this file ASCII: ISCC reads a BOM-less script as ANSI.

#ifndef AppVersion
  #error Pass /DAppVersion=<version>
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<publish folder>
#endif

[Setup]
; Never change AppId: it names the Installed apps entry (AIUsage_is1) that
; upgrades find and replace.
AppId=AIUsage
AppName=AI Usage
AppVersion={#AppVersion}
AppVerName=AI Usage {#AppVersion}
AppPublisher=Naimul Nashid
AppPublisherURL=https://github.com/naimulnashid/ai-usage-native
AppSupportURL=https://github.com/naimulnashid/ai-usage-native/issues
AppUpdatesURL=https://github.com/naimulnashid/ai-usage-native/releases/latest
VersionInfoVersion={#AppVersion}
VersionInfoDescription=AI Usage setup
PrivilegesRequired=lowest
; {autopf} is %LOCALAPPDATA%\Programs for a per-user install. Fixed, because
; start at login and tools\Install.ps1 both expect the app there.
DefaultDirName={autopf}\AI Usage
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 2004: the app's TargetPlatformMinVersion.
MinVersion=10.0.19041
; PrepareToInstall closes the running copy itself; Restart Manager would ask
; the user to close an app that only hides to the tray.
CloseApplications=no
SetupIconFile=..\src\UsageApp\Assets\app.ico
UninstallDisplayIcon={app}\AIUsage.exe
UninstallDisplayName=AI Usage
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
OutputBaseFilename=AIUsage-{#AppVersion}-win-x64-setup

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; Flags: unchecked

[InstallDelete]
; An upgrade starts from an empty folder, as tools\Install.ps1 does, so no
; file of an older version is left behind.
Type: filesandordirs; Name: "{app}\*"

[UninstallDelete]
; Inno removes only folders it created; after an upgrade from tools\Install.ps1
; the folder predates it and would be left behind, empty.
Type: dirifempty; Name: "{app}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\AI Usage"; Filename: "{app}\AIUsage.exe"; WorkingDir: "{app}"; Comment: "Claude Code and Codex usage, cost and runtime"
Name: "{userdesktop}\AI Usage"; Filename: "{app}\AIUsage.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\AIUsage.exe"; Description: "Open AI Usage"; Flags: nowait postinstall skipifsilent

[Code]
const
  ScriptUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\AIUsage';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'AI Usage';

{ Closes the copy running from this install, by its path: a copy from the zip
  or a demo run elsewhere keeps running. }
procedure CloseRunningCopy();
var
  Exe: String;
  ResultCode: Integer;
begin
  Exe := ExpandConstant('{app}\AIUsage.exe');
  StringChangeEx(Exe, '''', '''''', True);
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Get-Process AIUsage -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -ieq ''' + Exe + ''' } | ForEach-Object { Stop-Process -Id $_.Id -Force; $_.WaitForExit(5000) | Out-Null }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningCopy();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { A copy installed by tools\Install.ps1 had an entry of its own; this
    install has replaced its files, so the entry would only uninstall ours. }
  if (CurStep = ssPostInstall) and RegKeyExists(HKCU, ScriptUninstallKey) then
    RegDeleteKeyIncludingSubkeys(HKCU, ScriptUninstallKey);
end;

function InitializeUninstall(): Boolean;
begin
  CloseRunningCopy();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  { Start at login is the app's own setting; remove it only when it points
    at this install. }
  if (CurUninstallStep = usPostUninstall) and RegQueryStringValue(HKCU, RunKey, RunValue, Command)
    and (Pos(Lowercase(ExpandConstant('{app}\AIUsage.exe')), Lowercase(Command)) > 0) then
    RegDeleteValue(HKCU, RunKey, RunValue);
end;
