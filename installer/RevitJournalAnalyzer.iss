; Inno Setup script for Revit Journal Analyzer, in the same style as the FlexiData installer:
; per-user (no admin rights), BasePointLabs as publisher. English only, like the app.
;
; Built by .github/workflows/build.yml:
;   iscc /DAppVersion=0.3.0 installer\RevitJournalAnalyzer.iss
; AppVersion comes from Directory.Build.props, the file that also versions the exe, so the two cannot drift.
; The default below only applies when the script is compiled by hand.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; Unique to this product; never reuse it for another installer.
AppId={{AB9BB5D6-BE3D-499C-A08B-10C74A71F960}
AppName=Revit Journal Analyzer
AppVersion={#AppVersion}
AppVerName=Revit Journal Analyzer {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=BasePointLabs
AppPublisherURL=https://basepointlabs.io
AppSupportURL=https://basepointlabs.io
AppUpdatesURL=https://github.com/basepointlabs-io/journal-files/releases
DefaultDirName={localappdata}\Programs\BasePointLabs\RevitJournalAnalyzer
DisableDirPage=yes
DefaultGroupName=BasePointLabs
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; One installer for ordinary PCs and ARM PCs; [Files] picks the matching exe.
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64
SetupIconFile=..\src\RevitJournal.App\Icons\app.ico
UninstallDisplayIcon={app}\RevitJournalAnalyzer.exe
UninstallDisplayName=Revit Journal Analyzer
OutputDir=..\publish
OutputBaseFilename=RevitJournalAnalyzer-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "English"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start with Windows and watch Revit in the background"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\RevitJournalAnalyzer.exe"; DestDir: "{app}"; Check: not IsArm64; Flags: ignoreversion
Source: "..\publish\win-arm64\RevitJournalAnalyzer.exe"; DestDir: "{app}"; Check: IsArm64; Flags: ignoreversion
Source: "..\src\RevitJournal.App\Fonts\Manrope-OFL.txt"; DestDir: "{app}\Legal"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Revit Journal Analyzer"; Filename: "{app}\RevitJournalAnalyzer.exe"
Name: "{autodesktop}\Revit Journal Analyzer"; Filename: "{app}\RevitJournalAnalyzer.exe"; Tasks: desktopicon

[Registry]
; The same value the app's tray menu ("Start with Windows") reads and writes.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RevitJournalAnalyzer"; \
  ValueData: """{app}\RevitJournalAnalyzer.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\RevitJournalAnalyzer.exe"; Description: "{cm:LaunchProgram,Revit Journal Analyzer}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings the app keeps for the user (tray hint shown etc.).
Type: filesandordirs; Name: "{localappdata}\RevitJournalAnalyzer"

[Code]
// The app keeps running in the tray after its window is closed, so close it before files are replaced or removed.
procedure CloseRunningApp();
var
  ResultCode: Integer;
begin
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

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Also drop the autostart entry when it was switched on from the tray menu rather than by the installer.
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'RevitJournalAnalyzer');
end;
