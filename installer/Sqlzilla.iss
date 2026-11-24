; Build with: .\build-installer.ps1 -Platform x64 -Version 1.0.0
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{CC299425-4089-4D2B-A91F-DDFC9691B53C}
AppName=Sqlzilla
AppVersion={#AppVersion}
AppPublisher=judgewest2000
DefaultDirName={localappdata}\Programs\Sqlzilla
DefaultGroupName=Sqlzilla
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\Sqlzilla.exe
OutputDir=..\publish
OutputBaseFilename=Sqlzilla-Setup-{#Arch}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
ChangesAssociations=yes
SetupIconFile=..\src\Sqlzilla\Assets\AppIcon.ico
#if Arch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#elif Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x86compatible
#endif

[Files]
Source: "..\publish\win-{#Arch}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Sqlzilla"; Filename: "{app}\Sqlzilla.exe"
Name: "{autodesktop}\Sqlzilla"; Filename: "{app}\Sqlzilla.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "sqlassoc"; Description: "Open .sql files with Sqlzilla by default"; Flags: unchecked

[Run]
Filename: "{app}\Sqlzilla.exe"; Description: "Launch Sqlzilla"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Sqlzilla.exe"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[Registry]
; Remove the Apps & Features entry written by install.ps1 so only this installer's entry remains.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\Sqlzilla"; Flags: deletekey
; .sql files: always offer Sqlzilla under "Open with"; the sqlassoc task also makes it the default.
; (If the user has already picked a default in Windows, Windows asks them which app to use next time.)
Root: HKCU; Subkey: "Software\Classes\Sqlzilla.sql"; ValueType: string; ValueData: "SQL Script"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Sqlzilla.sql\DefaultIcon"; ValueType: string; ValueData: """{app}\Sqlzilla.exe"",0"
Root: HKCU; Subkey: "Software\Classes\Sqlzilla.sql\shell\open\command"; ValueType: string; ValueData: """{app}\Sqlzilla.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.sql\OpenWithProgids"; ValueType: string; ValueName: "Sqlzilla.sql"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.sql"; ValueType: string; ValueData: "Sqlzilla.sql"; Flags: uninsdeletevalue; Tasks: sqlassoc
Root: HKCU; Subkey: "Software\Classes\Applications\Sqlzilla.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "Sqlzilla"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\Sqlzilla.exe\SupportedTypes"; ValueType: string; ValueName: ".sql"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\Sqlzilla.exe\shell\open\command"; ValueType: string; ValueData: """{app}\Sqlzilla.exe"" ""%1"""

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
