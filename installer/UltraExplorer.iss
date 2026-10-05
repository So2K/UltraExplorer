; The UltraExplorer installer (Inno Setup 6.3 or later).
;
; It installs the self-contained single-file UltraExplorer.exe that
; `dotnet publish` makes, so no .NET needs to be on the machine.  By default
; it installs for the current user only, without administrator rights, into
; the same folder scripts/install.ps1 uses (%LOCALAPPDATA%\Programs\UltraExplorer),
; so either one updates the other's copy; setup offers an all-users install in
; Program Files as well.  Settings stay where the app keeps them,
; %LOCALAPPDATA%\UltraExplorer, and survive an uninstall.
;
; Built by .github/workflows/release.yml.  By hand, after a publish:
;
;   iscc /DAppVersion=1.2.3 /DPublishDir=..\publish installer\UltraExplorer.iss
;
; AppVersion      shown to the user (the tag without its "v": 1.2.3, 1.2.3-beta.1)
; AppFileVersion  numeric version for the file properties (1.2.3); defaults to AppVersion
; PublishDir      the folder holding UltraExplorer.exe, relative to this file
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

[InstallDelete]
; What scripts/install.ps1 leaves in the same folder: a framework-dependent
; build.  The single-file exe replaces all of it.
Type: files; Name: "{app}\UltraExplorer.dll"
Type: files; Name: "{app}\UltraExplorer.pdb"
Type: files; Name: "{app}\UltraExplorer.deps.json"
Type: files; Name: "{app}\UltraExplorer.runtimeconfig.json"
Type: files; Name: "{app}\Nodify.dll"
Type: files; Name: "{app}\Vortice.*.dll"
Type: files; Name: "{app}\SharpGen.Runtime*.dll"

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

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
