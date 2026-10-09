; The UltraExplorer installer (Inno Setup 6.3 or later).
;
; It installs the complete self-contained folder from `dotnet publish`,
; including the .NET/WPF runtime, so no .NET needs to be on the machine. By default
; it installs for the current user only, without administrator rights, into
; the same folder scripts/install.ps1 uses (%LOCALAPPDATA%\Programs\UltraExplorer),
; so either one updates the other's copy; setup offers an all-users install in
; Program Files as well.  Settings stay where the app keeps them,
; %LOCALAPPDATA%\UltraExplorer, and survive an uninstall.
;
; Built by .github/workflows/release.yml.  By hand, after a publish:
;
;   iscc /DAppVersion=1.2.3 /DPublishDir=..\publish installer\UltraExplorer.iss
; The publish folder must also contain LICENSE, README.md, notices, and
; UltraExplorer-package-files.txt (all publish-relative file paths, one per line,
; including the manifest itself). The workflow creates these before compiling.
;
; AppVersion      shown to the user (the tag without its "v": 1.2.3, 1.2.3-beta.1)
; AppFileVersion  numeric version for the file properties (1.2.3); defaults to AppVersion
; PublishDir      the complete publish folder, relative to this file
; OutputDir       where the setup goes, relative to this file

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef AppFileVersion
  #define AppFileVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "UltraExplorer"
#define AppExe "UltraExplorer.exe"
#define PackageManifest "UltraExplorer-package-files.txt"
; The app's own taskbar identity (App.AppUserModelId): a shortcut carrying it
; and the running window are one taskbar button, as with scripts/install.ps1.
#define AppUserModelId "UltraExplorer.App"
; The two file-dialog classes --register-picker writes (ComServerRegistration).
#define OpenDialogClassKey "Software\Classes\CLSID\{A1D3B6E4-59C7-4E1B-9F2A-7C61D8E04B31}"
#define SaveDialogClassKey "Software\Classes\CLSID\{A1D3B6E4-59C7-4E1B-9F2A-7C61D8E04B32}"

[Setup]
; Never change AppId: it is how an update finds the copy it replaces.
AppId={{6F0B8E57-3C1D-4B7A-9E54-2D8A1C3F7B90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
AppPublisherURL=https://github.com/so2k/ultraexplorer
AppSupportURL=https://github.com/so2k/ultraexplorer/issues
AppUpdatesURL=https://github.com/so2k/ultraexplorer/releases
VersionInfoVersion={#AppFileVersion}
VersionInfoProductVersion={#AppFileVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup

; Per-user by default, all users on request: {autopf} is
; %LOCALAPPDATA%\Programs for the first and Program Files for the second.
; Opening folders in UltraExplorer is per user (HKCU), so an all-users copy
; refuses it (ShellReplacementRegistration.IsPerMachineInstall), and the
; uninstaller sweeps every loaded user hive for verbs an older version left.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes

; x64 only, Windows 10 1903 or later, like the app itself.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.18362

; A running copy holds its exe; Restart Manager asks it to close (it saves
; its state as it always does) before the files are replaced or removed.
; Live dialog integration is recovered first and restored after an upgrade
; (PrepareToInstall/RestoreIntegration, the same steps as scripts/install.ps1).
CloseApplications=yes
RestartApplications=no

LicenseFile=..\LICENSE
SetupIconFile=..\src\UltraExplorer\Assets\UltraExplorer.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
ShowLanguageDialog=auto

OutputDir={#OutputDir}
; A name without the version, so releases/latest/download/<name> always works.
OutputBaseFilename={#AppName}-Setup-x64
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; First in a solid archive so PrepareToInstall can read it cheaply. The second
; entry installs it as well, alongside ALL runtime, native and locale files.
Source: "{#PublishDir}\{#PackageManifest}"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "A file manager on a zoomable canvas"; AppUserModelID: "{#AppUserModelId}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "A file manager on a zoomable canvas"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Before the files go: any Open/Save dialog UltraExplorer is standing in for is
; handed back to Windows, the replacement mode is switched off and its sign-in
; start removed (docs/DIALOG_INTEGRATION.md), and its listener stops.
Filename: "{app}\{#AppExe}"; Parameters: "--dialog-recover"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RestoreWindowsDialogs"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  DialogRunName = 'UltraExplorer dialogs';
  ShortcutRunName = 'UltraExplorer Win+E';

var
  PreviousPackageFiles: TArrayOfString;
  CurrentPackageFiles: TArrayOfString;
  { The dialog/folder integration paused for the upgrade, as scripts/install.ps1
    does: recovered before the files change, preference and agents back after. }
  IntegrationRecovered: Boolean;
  IntegrationRestored: Boolean;
  IntegrationEnabled: Boolean;
  ShortcutEnabled: Boolean;
  SavedSettingsPresent: Boolean;
  SavedSettings: AnsiString;
  HadDialogRunValue: Boolean;
  OldDialogRunValue: String;
  HadShortcutRunValue: Boolean;

function PackageFileAttributes(const FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function PackageContains(const RelativeName: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(CurrentPackageFiles) - 1 do
    if CompareText(CurrentPackageFiles[I], RelativeName) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

(* Only manifest-owned relative files inside {app} may be removed. Reject path
  traversal, wildcard/absolute paths, and any junction or symbolic link in the
  path. Never delete a directory, the uninstall log, or profile settings. *)
function SafeObsoletePackageFile(const RelativeName: String): Boolean;
var
  Remaining, Segment, Prefix: String;
  Separator: Integer;
  Attributes: LongWord;
begin
  Result := False;
  if RelativeName = '' then Exit;
  if (Pos(':', RelativeName) > 0) or
     (Pos('/', RelativeName) > 0) or (Pos('*', RelativeName) > 0) or
     (Pos('?', RelativeName) > 0) or (RelativeName[1] = '\') or
     (Pos('unins', Lowercase(RelativeName)) = 1) then Exit;

  Prefix := ExpandConstant('{app}');
  Attributes := PackageFileAttributes(Prefix);
  if (Attributes = $FFFFFFFF) or ((Attributes and $400) <> 0) then Exit;
  Remaining := RelativeName;
  repeat
    Separator := Pos('\', Remaining);
    if Separator > 0 then
    begin
      Segment := Copy(Remaining, 1, Separator - 1);
      Delete(Remaining, 1, Separator);
    end
    else
    begin
      Segment := Remaining;
      Remaining := '';
    end;
    if (Segment = '') or (Segment = '.') or (Segment = '..') then Exit;
    Prefix := AddBackslash(Prefix) + Segment;
    Attributes := PackageFileAttributes(Prefix);
    if (Attributes = $FFFFFFFF) or ((Attributes and $400) <> 0) then Exit;
  until Separator = 0;
  Result := FileExists(Prefix) and not DirExists(Prefix);
end;

procedure RemoveObsoletePackageFile(const RelativeName: String);
begin
  if not PackageContains(RelativeName) and SafeObsoletePackageFile(RelativeName) then
  begin
    if DeleteFile(AddBackslash(ExpandConstant('{app}')) + RelativeName) then
      Log('Removed obsolete package file: ' + RelativeName)
    else
      Log('Retained locked obsolete package file: ' + RelativeName);
  end;
end;

function InstalledExecutable(): String;
begin
  Result := ExpandConstant('{app}\{#AppExe}');
end;

function IntegrationSettingsPath(): String;
begin
  Result := ExpandConstant('{localappdata}\{#AppName}\dialog-integration.json');
end;

{ A top-level JSON boolean, however the writer spaced it ("Enabled": true or
  "Enabled":  true). False when the name is absent or not a boolean. }
function JsonFlag(const Text, Name: String; var Value: Boolean): Boolean;
var
  At: Integer;
  Rest: String;
begin
  Result := False;
  At := Pos('"' + Name + '"', Text);
  if At = 0 then Exit;
  Rest := TrimLeft(Copy(Text, At + Length(Name) + 2, Length(Text)));
  if Rest = '' then Exit;
  if Rest[1] <> ':' then Exit;
  Rest := TrimLeft(Copy(Rest, 2, Length(Rest)));
  if Pos('true', Rest) = 1 then begin Value := True; Result := True; end
  else if Pos('false', Rest) = 1 then begin Value := False; Result := True; end;
end;

function IsRoleCommand(const CommandLine: String): Boolean;
var
  Text: String;
begin
  Text := Lowercase(CommandLine);
  Result := (Pos('--dialog-agent', Text) > 0) or (Pos('--dialog-guardian', Text) > 0) or
    (Pos('--dialog-worker', Text) > 0) or (Pos('--dialog-proxy', Text) > 0) or
    (Pos('--dialog-recover', Text) > 0) or (Pos('--shortcut-agent', Text) > 0);
end;

{ Background integration processes of the installed copy. With Stop, each one
  still running is ended, as scripts/install.ps1 does after recovery. }
function InstalledRoles(Stop: Boolean): Integer;
var
  Locator, Service, Items, Item: Variant;
  I, ProcessId, Code: Integer;
  Path, CommandLine: String;
begin
  Result := 0;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Items := Service.ExecQuery('SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = ''{#AppExe}''');
    for I := 0 to Items.Count - 1 do
    begin
      Item := Items.ItemIndex(I);
      if VarIsNull(Item.ExecutablePath) or VarIsNull(Item.CommandLine) then Continue;
      Path := Item.ExecutablePath;
      CommandLine := Item.CommandLine;
      if (CompareText(Path, InstalledExecutable()) <> 0) or not IsRoleCommand(CommandLine) then Continue;
      Result := Result + 1;
      if Stop then
      begin
        ProcessId := Item.ProcessId;
        Log('Ending lingering installed background process ' + IntToStr(ProcessId));
        Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /PID ' + IntToStr(ProcessId), '', SW_HIDE, ewWaitUntilTerminated, Code);
      end;
    end;
  except
    Log('Installed background processes could not be listed: ' + GetExceptionMessage);
  end;
end;

{ Mirrors the finally block of scripts/install.ps1: the preference recovery
  paused, the sign-in start and the agents come back. Runs once. }
procedure RestoreIntegration(Installed: Boolean);
var
  Value: String;
  Code: Integer;
begin
  if IntegrationRestored or not IntegrationRecovered then Exit;
  IntegrationRestored := True;
  { Nested tests throughout: nothing here relies on short-circuit evaluation. }
  if SavedSettingsPresent then
    if not SaveStringToFile(IntegrationSettingsPath(), SavedSettings, False) then
      Log('The dialog integration preference could not be restored.');
  if IntegrationEnabled then
  begin
    if Installed or not HadDialogRunValue then Value := '"' + InstalledExecutable() + '" --dialog-agent'
    else Value := OldDialogRunValue;
    if not RegWriteStringValue(HKCU, RunKey, DialogRunName, Value) then Log('The dialog sign-in start could not be restored.');
    if FileExists(InstalledExecutable()) then
      if not ExecAsOriginalUser(InstalledExecutable(), '--dialog-agent', ExpandConstant('{app}'), SW_HIDE, ewNoWait, Code) then
        Log('The dialog agent could not be started: ' + SysErrorMessage(Code));
  end;
  { An older copy left by a failed upgrade does not know --shortcut-agent. }
  if ShortcutEnabled and (Installed or HadShortcutRunValue) then
    if FileExists(InstalledExecutable()) then
      if not ExecAsOriginalUser(InstalledExecutable(), '--shortcut-agent', ExpandConstant('{app}'), SW_HIDE, ewNoWait, Code) then
        Log('The Win+E agent could not be started: ' + SysErrorMessage(Code));
end;

{ An upgrade over a live integration: every dialog is handed back to Windows
  and the installed agents stop before their files are replaced. }
function RecoverIntegration(): String;
var
  Code, Waited: Integer;
  Text: String;
begin
  Result := '';
  IntegrationRecovered := False;
  IntegrationRestored := False;
  if not FileExists(InstalledExecutable()) then Exit;
  SavedSettingsPresent := LoadStringFromFile(IntegrationSettingsPath(), SavedSettings);
  IntegrationEnabled := False;
  ShortcutEnabled := False;
  if SavedSettingsPresent then
  begin
    Text := SavedSettings;
    if not JsonFlag(Text, 'Enabled', IntegrationEnabled) then IntegrationEnabled := False;
    if not JsonFlag(Text, 'WinEEnabled', ShortcutEnabled) then ShortcutEnabled := IntegrationEnabled;
  end;
  HadDialogRunValue := RegQueryStringValue(HKCU, RunKey, DialogRunName, OldDialogRunValue);
  HadShortcutRunValue := RegValueExists(HKCU, RunKey, ShortcutRunName);
  if not IntegrationEnabled and not ShortcutEnabled and (InstalledRoles(False) = 0) then Exit;

  Log('Restoring Windows dialogs and stopping installed background integration.');
  IntegrationRecovered := True;
  if not ExecAsOriginalUser(InstalledExecutable(), '--dialog-recover', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  if Code <> 0 then
  begin
    RestoreIntegration(False);
    Result := 'UltraExplorer could not hand its Open/Save dialogs back to Windows (exit code ' + IntToStr(Code) + '). No application files were replaced.';
    Exit;
  end;
  Waited := 0;
  while (InstalledRoles(False) > 0) and (Waited < 15000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  { Recovery has already restored originals; a lingering provider may end. }
  InstalledRoles(True);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  ExtractTemporaryFile('{#PackageManifest}');
  if not LoadStringsFromFile(ExpandConstant('{tmp}\{#PackageManifest}'), CurrentPackageFiles) then
  begin
    Result := 'The UltraExplorer package manifest could not be read.';
    Exit;
  end;
  SetArrayLength(PreviousPackageFiles, 0);
  LoadStringsFromFile(ExpandConstant('{app}\{#PackageManifest}'), PreviousPackageFiles);
  Result := RecoverIntegration();
end;

{ A cancelled or failed installation still gets the paused integration back. }
procedure DeinitializeSetup();
begin
  RestoreIntegration(False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  I: Integer;
begin
  (* Deletion happens only after the new payload has installed successfully.
     Updates retain settings and any unlisted file the user put in {app}. *)
  if CurStep = ssPostInstall then
  begin
    for I := 0 to GetArrayLength(PreviousPackageFiles) - 1 do
      RemoveObsoletePackageFile(PreviousPackageFiles[I]);
    { Exact filenames used by older packages before they carried a manifest.
      Required current files are protected by PackageContains. }
    RemoveObsoletePackageFile('LICENSE.txt');
    RemoveObsoletePackageFile('UltraExplorer.pdb');
    RemoveObsoletePackageFile('Vanara.Windows.Shell.dll');
    RemoveObsoletePackageFile('Vanara.PInvoke.Shared.dll');
    RemoveObsoletePackageFile('Vanara.PInvoke.Shell32.dll');
    RemoveObsoletePackageFile('Vanara.PInvoke.User32.dll');
    RestoreIntegration(True);
  end;
end;

{ The file-dialog classes (--register-picker) are removed with the copy they
  start, and only then: a registration that points at another build, a
  development copy say, is not this uninstaller's to touch. }
procedure RemovePickerClass(const Key: String);
var
  Command: String;
begin
  if RegQueryStringValue(HKCU, Key + '\LocalServer32', '', Command) and
     (Pos(Lowercase(ExpandConstant('{app}\{#AppExe}')), Lowercase(Command)) > 0) then
    RegDeleteKeyIncludingSubkeys(HKCU, Key);
end;

{ The sign-in start of the dialog replacement, should --dialog-recover not
  have removed it already; again only when it starts this copy. }
procedure RemoveDialogStart();
var
  Command: String;
begin
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'UltraExplorer dialogs', Command) and
     (Pos(Lowercase(ExpandConstant('{app}\{#AppExe}')), Lowercase(Command)) > 0) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'UltraExplorer dialogs');
end;

{ One folder verb (Folder, Directory or Drive; open or explore) whose command
  starts this copy: the command, the empty DelegateExecute and the "open"
  default written beside it go, then the keys this leaves empty - the same
  release the app does for a registration whose receipt is gone. }
procedure ReleaseFolderCommand(const RootKey: Integer; const ShellKey, Verb: String);
var
  VerbKey, CommandKey, Command, Value: String;
begin
  VerbKey := ShellKey + '\' + Verb;
  CommandKey := VerbKey + '\command';
  if not RegQueryStringValue(RootKey, CommandKey, '', Command) then Exit;
  if Pos(Lowercase('"' + ExpandConstant('{app}\{#AppExe}') + '"'), Lowercase(Command)) <> 1 then Exit;
  RegDeleteValue(RootKey, CommandKey, '');
  if RegQueryStringValue(RootKey, CommandKey, 'DelegateExecute', Value) then
    if Value = '' then RegDeleteValue(RootKey, CommandKey, 'DelegateExecute');
  if RegQueryStringValue(RootKey, ShellKey, '', Value) then
    if CompareText(Value, 'open') = 0 then RegDeleteValue(RootKey, ShellKey, '');
  RegDeleteKeyIfEmpty(RootKey, CommandKey);
  RegDeleteKeyIfEmpty(RootKey, VerbKey);
  RegDeleteKeyIfEmpty(RootKey, ShellKey);
  Log('Released folder command ' + CommandKey);
end;

procedure ReleaseFolderCommands(const RootKey: Integer; const Prefix: String);
begin
  ReleaseFolderCommand(RootKey, Prefix + 'Software\Classes\Folder\shell', 'open');
  ReleaseFolderCommand(RootKey, Prefix + 'Software\Classes\Folder\shell', 'explore');
  ReleaseFolderCommand(RootKey, Prefix + 'Software\Classes\Directory\shell', 'open');
  ReleaseFolderCommand(RootKey, Prefix + 'Software\Classes\Directory\shell', 'explore');
  ReleaseFolderCommand(RootKey, Prefix + 'Software\Classes\Drive\shell', 'open');
  ReleaseFolderCommand(RootKey, Prefix + 'Software\Classes\Drive\shell', 'explore');
end;

{ Folder verbs live in each user's HKCU. --dialog-recover gives the
  uninstalling user's back from its receipt; an all-users copy registered by an
  earlier version may also have left them in other users' hives, which would
  keep naming the deleted executable. Every loaded hive is swept for commands
  that start exactly this copy (docs/SHELL_REGISTRATION.md). Hives of users
  who are not signed in are not loaded and cannot be reached. }
procedure ReleaseAllFolderCommands();
var
  Users: TArrayOfString;
  I: Integer;
begin
  ReleaseFolderCommands(HKCU, '');
  if not IsAdminInstallMode then Exit;
  if RegGetSubkeyNames(HKU, '', Users) then
    for I := 0 to GetArrayLength(Users) - 1 do
      if (Pos('_Classes', Users[I]) = 0) and (CompareText(Users[I], '.DEFAULT') <> 0) then
        ReleaseFolderCommands(HKU, Users[I] + '\');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemovePickerClass('{#OpenDialogClassKey}');
    RemovePickerClass('{#SaveDialogClassKey}');
    RemoveDialogStart();
  end;
  { After [UninstallRun]: recovery restores from its receipt first. }
  if CurUninstallStep = usPostUninstall then
    ReleaseAllFolderCommands();
end;
