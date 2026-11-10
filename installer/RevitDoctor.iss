; Inno Setup script for Revit Doctor, in the same style as the FlexiData installer:
; per-user (no admin rights), BasePointLabs as publisher. English only, like the app.
;
; Built by .github/workflows/build.yml:
;   iscc /DAppVersion=0.3.0 installer\RevitDoctor.iss
; AppVersion comes from Directory.Build.props, the file that also versions the exe, so the two cannot drift.
; The default below only applies when the script is compiled by hand.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; Unique to this product; never reuse it for another installer.
AppId={{AB9BB5D6-BE3D-499C-A08B-10C74A71F960}
AppName=Revit Doctor
AppVersion={#AppVersion}
AppVerName=Revit Doctor {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=BasePointLabs
AppPublisherURL=https://basepointlabs.io
AppSupportURL=https://basepointlabs.io
AppUpdatesURL=https://github.com/basepointlabs-io/journal-files/releases
DefaultDirName={localappdata}\Programs\BasePointLabs\RevitDoctor
DisableDirPage=yes
DefaultGroupName=BasePointLabs
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; One installer for ordinary PCs and ARM PCs; [Files] picks the matching exe.
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64
SetupIconFile=..\src\RevitJournal.App\Icons\app.ico
UninstallDisplayIcon={app}\RevitDoctor.exe
UninstallDisplayName=Revit Doctor
OutputDir=..\publish
OutputBaseFilename=RevitDoctor-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "English"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start with Windows and watch Revit in the background"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\RevitDoctor.exe"; DestDir: "{app}"; Check: not IsArm64; Flags: ignoreversion
Source: "..\publish\win-arm64\RevitDoctor.exe"; DestDir: "{app}"; Check: IsArm64; Flags: ignoreversion
Source: "..\src\RevitJournal.App\Fonts\Manrope-OFL.txt"; DestDir: "{app}\Legal"; Flags: ignoreversion

[InstallDelete]
; Up to 0.4.0 the program was called Revit Journal Analyzer. The AppId is the same, so this installer upgrades it
; in place; remove the old executable and shortcuts so only Revit Doctor remains.
Type: files; Name: "{app}\RevitJournalAnalyzer.exe"
Type: files; Name: "{autoprograms}\Revit Journal Analyzer.lnk"
Type: files; Name: "{autodesktop}\Revit Journal Analyzer.lnk"

[Icons]
Name: "{autoprograms}\Revit Doctor"; Filename: "{app}\RevitDoctor.exe"
Name: "{autodesktop}\Revit Doctor"; Filename: "{app}\RevitDoctor.exe"; Tasks: desktopicon

[Registry]
; The same value the app's tray menu ("Start with Windows") reads and writes.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RevitDoctor"; \
  ValueData: """{app}\RevitDoctor.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\RevitDoctor.exe"; Description: "{cm:LaunchProgram,Revit Doctor}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings the app keeps for the user (tray hint shown etc.).
Type: filesandordirs; Name: "{localappdata}\RevitDoctor"
Type: filesandordirs; Name: "{localappdata}\RevitJournalAnalyzer"

[Code]
// The app keeps running in the tray after its window is closed, so close it before files are replaced or removed.
procedure CloseRunningApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im RevitDoctor.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // The installed copy of the former name; portable copies have other file names and are left alone.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im RevitJournalAnalyzer.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  CloseRunningApp();
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Autostart set up under the former name pointed at the old executable: carry it over.
  if (CurStep = ssPostInstall) and RegValueExists(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'RevitJournalAnalyzer') then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'RevitJournalAnalyzer');
    RegWriteStringValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'RevitDoctor',
      '"' + ExpandConstant('{app}\RevitDoctor.exe') + '" --background');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Also drop the autostart entry when it was switched on from the tray menu rather than by the installer.
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'RevitDoctor');
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'RevitJournalAnalyzer');
  end;
end;
