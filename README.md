# xPaste for Windows

**Trình quản lý clipboard (clipboard manager) cho Windows** — bản dựng lại của app macOS *xPaste* bằng **WinUI 3 / .NET 8**. Bảng lịch sử kính mờ trượt ra từ mép màn hình bằng phím tắt, lưu lại mọi thứ bạn đã sao chép và dán lại chỉ với một cú nhấp — mà **không giành focus** khỏi ứng dụng đang dùng.

> Windows 10 (build 17763+) hoặc Windows 11 · x64

---

## ✨ Tính năng

- 📋 **Lịch sử clipboard** đầy đủ loại: văn bản, **URL**, **ảnh**, **file**, **thư mục**.
- 🔍 **Tìm kiếm** tức thì, **ghim (pin)** mục quan trọng, 2 tab **Clipboard / Pin**.
- 🖼️ **Xem trước (Quick Look)** bằng phím `Space`: văn bản cuộn được, ảnh lớn, danh sách file, và **web preview** cho URL (WebView2).
- 🔗 **Link preview**: tự lấy tiêu đề + ảnh (OpenGraph) hoặc favicon cho link đã sao chép.
- 🎨 **Ô màu**: sao chép mã màu `#hex` / `rgb()` / `hsl()` → thẻ hiển thị đúng màu đó.
- 🅰️ **Nhận diện app nguồn**: hiện icon app đã sao chép + tô màu accent trích từ icon.
- 🌗 **Giao diện Sáng / Tối / Theo hệ thống** — bám theo Windows và **đổi ngay** khi bạn đổi theme Windows.
- ⌨️ **Dán không giành focus** (nonactivating panel) → app đích luôn nhận được nội dung dán.
- 🚀 **Phím tắt** (mặc định `Ctrl+Shift+V`), **tray icon**, **khởi động cùng Windows**.
- 🔒 **Riêng tư**: ẩn khi chia sẻ màn hình, bỏ qua nội dung mật/tạm thời (password manager), danh sách app bỏ qua.
- 🧹 **Giữ lịch sử** theo Ngày/Tuần/Tháng/Năm/Vĩnh viễn, tuỳ chọn xoá khi máy ngủ / đăng xuất.
- 📐 **Panel responsive** — chiếm ~28% màn hình ở mọi độ phân giải & mức scale, chọn được vị trí (dưới/trên/trái/phải).

---

## 📥 Cài đặt

### Cách 1 — Dùng bộ cài (khuyến nghị)
Tải/chạy **`xPaste-Setup-*.exe`** → cài mặc định vào `C:\Program Files\xPaste`, tạo shortcut Start Menu (và Desktop nếu chọn).

> Bộ cài **self-contained** — đã nhúng sẵn .NET 8 + Windows App SDK, **máy đích không cần cài thêm gì**.
>
> Vì bộ cài chưa ký số, Windows SmartScreen có thể báo *"Windows protected your PC"* → bấm **More info → Run anyway**.

Gỡ cài đặt: **Settings → Apps → xPaste → Uninstall** (hoặc shortcut Uninstall trong Start Menu).

### Cách 2 — Bản portable
Chép cả thư mục `dist\xPasteWin-win-x64\` sang máy Windows x64 bất kỳ và chạy `xPasteWin.exe` (không cần cài).

---

## 🕹️ Sử dụng

1. Nhấn **`Ctrl+Shift+V`** (hoặc click trái **icon tray**) để mở bảng lịch sử.
2. **Click** một thẻ để chọn, **double-click** để dán ngay vào app đang dùng.
3. **`Space`** xem trước · **`←/→`** điều hướng · **`Enter`** dán · **`Shift+Enter`** dán không định dạng · **`Ctrl+C`** copy · **`Delete`** xoá · **`Esc`** đóng.
4. **Chuột phải** một thẻ để có menu: Paste / Copy / Open URL / Delete / Pin / Preview…
5. Nút **"…"** → **Settings** để đổi phím tắt, vị trí panel, giao diện, quyền riêng tư, thời gian giữ lịch sử.

---

## 🔨 Build từ mã nguồn

### Yêu cầu
- **.NET 8 SDK** — <https://dotnet.microsoft.com/download/dotnet/8.0>
- **Windows App SDK 1.6** — cài kèm workload **".NET Desktop Development" + WinUI** của Visual Studio 2022, hoặc cài riêng Windows App SDK runtime/SDK.

### Build & chạy thử (Debug)
```powershell
git clone https://github.com/longdang971/xPasteWin
cd xPasteWin

dotnet build xPasteWin.sln -c Release -p:Platform=x64
dotnet run --project src/xPasteWin           # chạy thử
dotnet test tests/xPasteWin.Tests            # unit test (logic thuần)
```

### Build bản Release **self-contained** (nhúng sẵn thư viện)

Đây là bản để phân phối: **nhúng cả .NET runtime lẫn Windows App SDK** vào output, nên **người dùng cuối KHÔNG cần cài .NET hay Windows App Runtime**.

```powershell
dotnet publish src/xPasteWin/xPasteWin.csproj -c Release -p:Platform=x64 -r win-x64 `
    -p:SelfContained=true --self-contained -o dist\xPasteWin-win-x64
```

- Kết quả nằm ở **`dist\xPasteWin-win-x64\`** (~160 MB, gồm `xPasteWin.exe` + toàn bộ thư viện).
- Chạy `xPasteWin.exe` trong đó là dùng được ngay trên máy Windows x64 sạch (chưa cài .NET).
- Việc nhúng Windows App SDK đã bật sẵn trong `xPasteWin.csproj` (`WindowsAppSDKSelfContained=true`).

### Đóng gói thành bộ cài `.exe` (Inno Setup)

```powershell
# 1) Cài Inno Setup (một lần)
winget install JRSoftware.InnoSetup

# 2) Đảm bảo đã publish self-contained vào dist\xPasteWin-win-x64 (bước trên)

# 3) Compile bộ cài
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\xPaste.iss
```

→ Tạo ra **`dist\xPaste-Setup-1.0.0.exe`** — bộ cài vào `C:\Program Files\xPaste`, kèm shortcut + uninstaller. Cấu hình bộ cài ở [`installer/xPaste.iss`](installer/xPaste.iss) (đổi tên/phiên bản/icon tại đây).

---

## 🧱 Kiến trúc (tóm tắt)

| Thành phần | Công nghệ Windows |
|---|---|
| Theo dõi clipboard | `AddClipboardFormatListener` + `WM_CLIPBOARDUPDATE` |
| Phím tắt toàn cục | `RegisterHotKey` + `WM_HOTKEY` |
| Panel không giành focus | Cửa sổ borderless + `WS_EX_NOACTIVATE\|TOOLWINDOW` + global hook (`WH_KEYBOARD_LL`/`WH_MOUSE_LL`) |
| Dán vào app đích | `SetForegroundWindow` + `SendInput` (Ctrl+V) |
| Nền kính mờ | `DesktopAcrylicController` (tint theo theme) |
| Tray icon | `H.NotifyIcon` |
| Lưu trữ | JSON trong `%AppData%\xPaste\` |

Mã nguồn: `src/xPasteWin/` — `Models/` · `Services/` · `ViewModels/` · `Views/` · `Interop/`.

---

## 📝 Ghi chú

- Chạy **non-elevated** (không "Run as administrator") — clipboard manager cần quyền người dùng thường để dán được vào các app khác (do UIPI của Windows).
- Dữ liệu (lịch sử, cài đặt, cache) nằm ở `%AppData%\xPaste\`; gỡ app không tự xoá — xoá thủ công nếu cần.

---

*Powered by LQ Team.*
