; Installer for IL-2 Tactical Radio (Inno Setup 6).
;
; Build after publishing the app:
;   dotnet publish src/Il2Radio.App
;   iscc installer\Il2Radio.iss /DAppVersion=0.2.0
; Writes publish\IL2-Tactical-Radio-Setup-<version>.exe. Without /DAppVersion the version is read from the exe.
;
; It installs for the current user only (no administrator prompt), into %LOCALAPPDATA%\Programs, because the
; radio keeps settings.json and logs\ beside Il2Radio.exe. The radio itself still asks for
; administrator rights when it starts (Volume Shadow Copy). IL-2's own files are never touched here:
; startup.cfg is changed by the radio, with a backup and the player's consent.
;
; Code signing: add a SignTool line here (and SignedUninstaller=yes) once a certificate is set up.

#define AppName "IL-2 Tactical Radio"
#define AppExe "Il2Radio.exe"
#define PublishDir "..\publish"
#ifndef AppVersion
  #define AppVersion GetVersionNumbersString(AddBackslash(SourcePath) + PublishDir + "\" + AppExe)
#endif

[Setup]
AppId={{9067F2DD-1245-4E7E-9293-5F19E1334B65}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Kyle Bloom
AppPublisherURL=https://github.com/kylebloom/AI_Friendly_Radio
AppSupportURL=https://github.com/kylebloom/AI_Friendly_Radio/issues
AppUpdatesURL=https://github.com/kylebloom/AI_Friendly_Radio/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
OutputDir={#PublishDir}
OutputBaseFilename=IL2-Tactical-Radio-Setup-{#AppVersion}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
; The radio runs elevated, which Restart Manager can't close; InitializeSetup asks the player instead.
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README-PLAYER.md"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Dirs]
; Session reports land here when the radio window closes; the Start menu links to it.
Name: "{app}\logs"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Player guide"; Filename: "{app}\README.txt"
Name: "{group}\Session reports (logs)"; Filename: "{app}\logs"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/"; Description: "Open the RivaTuner Statistics Server download page (Guru3D)"; Flags: postinstall shellexec skipifsilent nowait; Check: not RtssInstalled
Filename: "{app}\README.txt"; Description: "Read the player guide"; Flags: postinstall shellexec skipifsilent nowait unchecked
; shellexec: the radio requires administrator rights, so it must start through the UAC prompt.
Filename: "{app}\{#AppExe}"; Description: "Start the radio now (it asks for administrator rights)"; Flags: postinstall shellexec skipifsilent nowait

[Code]
const
  SteamUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 307960';
  RadioWindowTitle = '{#AppName}';

var
  Il2Page: TInputDirWizardPage;

function IsIl2Folder(const Dir: String): Boolean;
begin
  Result := (Dir <> '') and FileExists(AddBackslash(Dir) + 'data\startup.cfg');
end;

{ Steam's own record of where it installed IL-2 (any library drive), then the folders the radio checks itself. }
function DetectIl2: String;
var
  Candidates: TArrayOfString;
  I: Integer;
begin
  if RegQueryStringValue(HKLM64, SteamUninstallKey, 'InstallLocation', Result) and IsIl2Folder(Result) then
    Exit;
  SetArrayLength(Candidates, 4);
  Candidates[0] := ExpandConstant('{commonpf32}\Steam\steamapps\common\IL-2 Sturmovik Battle of Stalingrad');
  Candidates[1] := ExpandConstant('{commonpf64}\Steam\steamapps\common\IL-2 Sturmovik Battle of Stalingrad');
  Candidates[2] := ExpandConstant('{commonpf64}\1C Game Studios\IL-2 Sturmovik Great Battles');
  Candidates[3] := ExpandConstant('{commonpf32}\1C Game Studios\IL-2 Sturmovik Great Battles');
  for I := 0 to GetArrayLength(Candidates) - 1 do
    if IsIl2Folder(Candidates[I]) then
    begin
      Result := Candidates[I];
      Exit;
    end;
  Result := '';
end;

function RtssInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM32, 'SOFTWARE\Unwinder\RTSS')
    or RegKeyExists(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS');
end;

{ The radio holds Il2Radio.exe open while it runs; its window title is how a non-elevated setup can see it. }
function RadioClosed(const Action: String): Boolean;
begin
  while FindWindowByWindowName(RadioWindowTitle) <> 0 do
    if SuppressibleMsgBox('IL-2 Tactical Radio is running. Close its window (or press Quit), then choose Retry to ' + Action + '.',
        mbError, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  Result := True;
end;

function InitializeSetup: Boolean;
begin
  Result := RadioClosed('continue');
end;

function InitializeUninstall: Boolean;
begin
  Result := RadioClosed('uninstall');
end;

procedure InitializeWizard;
begin
  Il2Page := CreateInputDirPage(wpSelectDir,
    'IL-2 folder',
    'Where is IL-2 Sturmovik: Great Battles installed?',
    'The radio reads the mission logs and Tacview tracks IL-2 writes into its data folder. ' +
    'Select the IL-2 folder, the one that contains data\startup.cfg.' + #13#10#13#10 +
    'Leave it empty to let the radio look in the usual Steam and 1C folders itself. You can change it later on the radio''s Settings tab.',
    False, '');
  Il2Page.Add('');
  Il2Page.Values[0] := DetectIl2;
end;

function SettingsPath: String;
begin
  Result := AddBackslash(WizardDirValue) + 'settings.json';
end;

{ An update keeps the player's settings.json, IL-2 folder included. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = Il2Page.ID) and FileExists(SettingsPath);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir: String;
begin
  Result := True;
  if CurPageID = Il2Page.ID then
  begin
    Dir := Trim(Il2Page.Values[0]);
    if (Dir <> '') and not IsIl2Folder(Dir) then
    begin
      SuppressibleMsgBox('There is no data\startup.cfg in that folder. Select the IL-2 folder itself (for example ' +
        '...\steamapps\common\IL-2 Sturmovik Battle of Stalingrad), or leave it empty.', mbError, MB_OK, IDOK);
      Result := False;
    end;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  if not FileExists(SettingsPath) then
  begin
    Result := Result + 'IL-2 folder:' + NewLine + Space;
    if Trim(Il2Page.Values[0]) = '' then
      Result := Result + '(found by the radio when it starts)'
    else
      Result := Result + Trim(Il2Page.Values[0]);
    Result := Result + NewLine + NewLine;
  end;
  if MemoTasksInfo <> '' then
    Result := Result + MemoTasksInfo + NewLine + NewLine;
  if not RtssInstalled then
    Result := Result + 'RivaTuner Statistics Server:' + NewLine + Space +
      'not found. The radio shows its text inside IL-2 through it; you can open its download page at the end.';
end;

{ Only the IL-2 folder: the radio fills in every other setting with its default. }
procedure WriteSettings;
var
  Dir: String;
begin
  Dir := Trim(Il2Page.Values[0]);
  if FileExists(SettingsPath) or (Dir = '') then
    Exit;
  StringChangeEx(Dir, '\', '\\', True);
  SaveStringToFile(SettingsPath, '{' + #13#10 + '  "il2Path": "' + Dir + '"' + #13#10 + '}' + #13#10, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteSettings;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and not RtssInstalled then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'RivaTuner Statistics Server wasn''t found. The radio shows its text inside IL-2 through it, so install it ' +
      'from Guru3D (free) and turn its on-screen display on. The player guide explains how.';
end;

{ Settings and session reports are the player's: kept unless they say otherwise. piper\ is a German voice
  download from earlier versions of the radio, which no longer uses it. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  App: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  App := ExpandConstant('{app}');
  if not (FileExists(App + '\settings.json') or DirExists(App + '\logs') or DirExists(App + '\piper')) then
    Exit;
  if SuppressibleMsgBox('Also delete your radio settings and session reports (logs)?' + #13#10#13#10 +
      'IL-2''s startup.cfg is left as it is; the radio''s backup of it (startup.cfg.radio-backup-...) is in IL-2''s data folder.',
      mbConfirmation, MB_YESNO, IDNO) = IDYES then
  begin
    DeleteFile(App + '\settings.json');
    DelTree(App + '\logs', True, True, True);
    DelTree(App + '\piper', True, True, True);
  end;
end;
