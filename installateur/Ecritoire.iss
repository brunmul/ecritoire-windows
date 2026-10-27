; Installateur d'Écritoire pour Windows (Inno Setup 6).
; Construit par GitHub Actions (.github/workflows/version.yml) à partir de publish\Ecritoire.exe :
;   ISCC.exe /DAppVersion=0.3.0 installateur\Ecritoire.iss
; Installation pour l'utilisateur seul (sans droits administrateur), dans %LOCALAPPDATA%\Programs\Ecritoire, pour que
; la mise à jour automatique puisse se faire sans question. Les réglages (%APPDATA%\Ecritoire) et les polices
; personnelles (%LOCALAPPDATA%\Ecritoire\Polices personnelles) ne sont jamais touchés.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{78B17811-C3FF-4907-A68A-2A0AEBD42BB0}
AppName=Écritoire
AppVersion={#AppVersion}
AppVerName=Écritoire {#AppVersion}
AppPublisher=Écritoire
AppPublisherURL=https://github.com/brunmul/ecritoire-windows-versions
DefaultDirName={localappdata}\Programs\Ecritoire
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish\installateur
OutputBaseFilename=Ecritoire-Installation-{#AppVersion}
SetupIconFile=..\src\Ecritoire.App\Ressources\Ecritoire.ico
UninstallDisplayIcon={app}\Ecritoire.exe
UninstallDisplayName=Écritoire
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Une mise à jour ferme Écritoire si elle est ouverte (l'app enregistre avant de lancer l'installateur).
CloseApplications=force
RestartApplications=no

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "bureau"; Description: "Créer un raccourci sur le Bureau"; Flags: unchecked

[Files]
Source: "..\publish\Ecritoire.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Écritoire"; Filename: "{app}\Ecritoire.exe"
Name: "{autodesktop}\Écritoire"; Filename: "{app}\Ecritoire.exe"; Tasks: bureau

[Run]
; Aussi après une mise à jour silencieuse : Écritoire se rouvre d'elle-même.
Filename: "{app}\Ecritoire.exe"; Description: "Ouvrir Écritoire"; Flags: nowait postinstall
