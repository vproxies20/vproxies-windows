#define MyAppName "VProxies"
#define MyAppVersion "0.8.3"
#define MyAppPublisher "VProxies"
#define MyAppExeName "VProxies.exe"

[Setup]
AppId={{7CB46111-F1AE-421E-A15B-135177A46E25}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\VProxies
DefaultGroupName=VProxies
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=VProxiesSetup-{#MyAppVersion}-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\runtime\sing-box.exe"; DestDir: "{app}\runtime"; Flags: ignoreversion
Source: "..\runtime\wintun.dll"; DestDir: "{app}\runtime"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\VProxies"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\VProxies"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch VProxies"; Flags: nowait postinstall skipifsilent runascurrentuser
