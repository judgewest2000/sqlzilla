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

[Run]
Filename: "{app}\Sqlzilla.exe"; Description: "Launch Sqlzilla"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Sqlzilla.exe"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[Registry]
; Remove the Apps & Features entry written by install.ps1 so only this installer's entry remains.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\Sqlzilla"; Flags: deletekey

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
