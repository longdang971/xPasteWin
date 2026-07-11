; Inno Setup script — bộ cài xPaste (WinUI 3 self-contained, cài vào C:\Program Files\xPaste)
#define MyAppName "xPaste"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "LQ Team"
#define MyAppExeName "xPasteWin.exe"

[Setup]
AppId={{AE3A2B1F-1149-4945-ADF6-DA5447D1AEB7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\xPasteWin\Assets\tray.ico
OutputDir=..\dist
OutputBaseFilename=xPaste-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Cài vào Program Files → cần quyền admin; ArchitecturesInstallIn64BitMode để {autopf}=C:\Program Files
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Toàn bộ output publish self-contained (đã nhúng .NET + Windows App SDK)
Source: "..\dist\xPasteWin-win-x64\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Chạy sau khi cài NHƯNG dưới quyền người dùng (không elevated) — clipboard manager phải chạy
; non-elevated mới dán được vào các app thường (UIPI).
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
