; Inno Setup script for Decrypta.
; Builds a per-user installer (no admin required) from a self-contained publish folder.
;
;   dotnet publish src\Decrypta.App -c Release -r win-x64 --self-contained true -o publish
;   dotnet publish src\Decrypta.Cli -c Release -r win-x64 --self-contained true -o publish
;   ISCC installer\Decrypta.iss

#define AppName "Decrypta"
#define AppVersion "1.3.0"
#define AppPublisher "Decrypta Contributors"
#define AppExe "Decrypta.exe"

[Setup]
AppId={{7C2F5B1E-9A2E-4D2B-8E7C-D3C0A1F2B345}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\release
OutputBaseFilename=Decrypta-Setup-{#AppVersion}
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
