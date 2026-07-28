; Script Inno Setup pour DONNA — voir ARCHITECTURE.md §11.
; Compilation : ISCC installer\donna.iss (ou via build.ps1, qui publie l'exe avant).

#define MyAppName "DONNA"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Mehdi"
#define MyAppExeName "Donna.exe"
#define MyPublishDir "..\Donna\bin\Release\net10.0-windows\win-x64\publish"

[Setup]
AppId={{8F2C1A6E-4B3D-4E7F-9A1B-2C3D4E5F6A7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputBaseFilename=Donna-Setup
OutputDir=..\dist
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
; Signature du binaire et de l'installeur à ajouter séparément (ARCHITECTURE.md §11)
; pour limiter les avertissements SmartScreen/antivirus (§7.5).

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Files]
Source: "{#MyPublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Désinstaller {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Lancer {#MyAppName}"; Flags: nowait postinstall skipifsilent
