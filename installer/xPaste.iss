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

[Code]
{ DeleteData: người dùng có tick ô "xoá toàn bộ dữ liệu" trong hộp thoại gỡ cài đặt hay không. }
var
  DeleteData: Boolean;

{ Đóng xPaste đang chạy (tray app → khoá exe/dll). BẮT BUỘC trước khi gỡ/ghi đè: nếu không,
  file trong C:\Program Files\xPaste bị khoá → uninstaller bỏ qua, thư mục còn nguyên và app
  vẫn chạy sau khi báo "gỡ thành công". }
procedure KillRunningApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName} /T', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(700); { chờ OS giải phóng khoá file trước khi xoá }
end;

{ Cài đè (nâng cấp): đóng bản đang chạy trước khi ghi file mới. }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    KillRunningApp();
end;

{ Trước khi gỡ: hỏi người dùng có xoá luôn TOÀN BỘ dữ liệu (lịch sử/cài đặt/cache) hay không.
  Yes = xoá sạch; No = chỉ gỡ app, giữ dữ liệu ở %AppData%\xPaste. }
function InitializeUninstall(): Boolean;
begin
  Result := True;
  DeleteData := MsgBox(
    'Also delete all xPaste data (clipboard history, settings, cache) from this PC?' + #13#10 + #13#10 +
    'Choose "Yes" to remove everything (cannot be undone).' + #13#10 +
    'Choose "No" to uninstall the app but keep your data.',
    mbConfirmation, MB_YESNO) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  case CurUninstallStep of
    usUninstall:
      KillRunningApp();  { đóng app để mở khoá file TRƯỚC khi uninstaller xoá [Files] }
    usPostUninstall:
      begin
        { Startup entry (do app tạo lúc chạy) trỏ tới exe đã xoá → luôn gỡ để không còn mục khởi động hỏng. }
        RegDeleteValue(HKEY_CURRENT_USER,
          'Software\Microsoft\Windows\CurrentVersion\Run', 'xPaste');
        if DeleteData then
        begin
          DelTree(ExpandConstant('{userappdata}\xPaste'), True, True, True);       { %AppData%\xPaste: lịch sử/cài đặt/cache }
          DelTree(ExpandConstant('{localappdata}\Temp\xPasteUpdate'), True, True, True); { file bộ cài đã tải khi tự cập nhật }
        end;
        DelTree(ExpandConstant('{app}'), True, True, True);  { dọn thư mục cài đặt còn sót trong C:\Program Files }
      end;
  end;
end;
