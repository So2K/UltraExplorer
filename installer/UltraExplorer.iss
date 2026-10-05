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
var
  PreviousPackageFiles: TArrayOfString;
  CurrentPackageFiles: TArrayOfString;

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

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemovePickerClass('{#OpenDialogClassKey}');
    RemovePickerClass('{#SaveDialogClassKey}');
    RemoveDialogStart();
  end;
end;
