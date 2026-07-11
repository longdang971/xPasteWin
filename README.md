# xPaste for Windows

**Clipboard manager cấp chuyên nghiệp cho Windows** — quản lý toàn bộ lịch sử sao chép với giao diện hiện đại, tìm kiếm nhanh và ghim mục quan trọng.

> **Yêu cầu:** Windows 10 (build 17763+) hoặc Windows 11 · x64

---

## ✨ Tính năng chính

### 📋 **Lịch sử clipboard toàn diện**
- Hỗ trợ mọi loại dữ liệu: văn bản, **URL**, **ảnh**, **file**, **thư mục**
- Tìm kiếm tức thì với bộ lọc thông minh
- Ghim (pin) các mục quan trọng để dễ tìm lại

### 🖼️ **Xem trước nhanh chóng (Quick Look)**
Nhấn `Space` để xem trước tức thì:
- Văn bản có thể cuộn đọc được
- Ảnh hiển thị full size
- Danh sách file/thư mục có cấu trúc
- Web preview cho các URL (dùng WebView2)

### 🔗 **Link preview thông minh**
- Tự động lấy tiêu đề + ảnh từ OpenGraph
- Hiển thị favicon cho link đã sao chép
- Dễ dàng nhận diện và quản lý URL

### 🎨 **Hỗ trợ màu sắc**
- Nhận diện và hiển thị mã màu `#hex`, `rgb()`, `hsl()`
- Ô màu preview cho từng mục sao chép được màu

### 🅰️ **Ứng dụng nguồn thông minh**
- Hiển thị icon của app mà bạn sao chép
- Tô màu accent trích từ icon ứng dụng
- Dễ dàng quản lý theo nguồn

### 🌗 **Giao diện linh hoạt**
- Chế độ Sáng / Tối / Theo hệ thống
- Tự động tuân theo Windows Theme
- Đổi giao diện ngay lập tức mà không cần khởi động lại

### ⌨️ **Dán không giành focus**
Panel dán mà **không chiếm quyền điều khiển** — ứng dụng đích luôn nhận được nội dung đúng lúc

### 🚀 **Phím tắt & Tiện ích**
- Phím tắt toàn cục (mặc định `Ctrl+Shift+V`)
- Tray icon với menu nhanh
- Khởi động cùng Windows
- Có thể tùy chỉnh tất cả

### 🔒 **Riêng tư & An toàn**
- Ẩn tự động khi chia sẻ màn hình
- Bỏ qua nội dung từ password manager
- Danh sách app bỏ qua (sensitive apps)
- Không theo dõi dữ liệu ngoài local

### 🧹 **Quản lý lịch sử linh hoạt**
Chọn thời gian giữ lịch: Ngày / Tuần / Tháng / Năm / Vĩnh viễn
- Tuỳ chọn xoá tự động khi máy ngủ / đăng xuất
- Kiểm soát hoàn toàn dung lượng lưu trữ

### 📐 **Panel responsive**
- Chiếm ~28% màn hình ở mọi độ phân giải
- Tự động scale theo DPI system
- Chọn vị trí: dưới / trên / trái / phải

---

## 📥 Cài đặt

### Cách 1 — Bộ cài (Khuyến nghị)
1. Tải **`xPaste-Setup-*.exe`** từ [Releases](../../releases)
2. Chạy installer → cài mặc định vào `C:\Program Files\xPaste`
3. Tạo shortcut tự động trong Start Menu (và Desktop nếu chọn)

> ✅ **Self-contained** — đã nhúng sẵn .NET 8 + Windows App SDK
>
> ⚠️ Vì chưa ký số, Windows SmartScreen có thể cảnh báo → nhấp **More info → Run anyway**

**Gỡ cài đặt:** Settings → Apps → xPaste → Uninstall

### Cách 2 — Bản portable (Không cần cài)
1. Sao chép cả thư mục `dist\xPasteWin-win-x64\` sang máy Windows x64
2. Chạy `xPasteWin.exe` trực tiếp

---

## 🕹️ Cách sử dụng

### Mở Panel
- Nhấn **`Ctrl+Shift+V`** hoặc click trái **icon tray**

### Điều hướng & Thao tác
| Phím | Chức năng |
|---|---|
| **Click** | Chọn một mục |
| **Double-click** | Dán ngay vào app hiện tại |
| **`Space`** | Xem trước nội dung |
| **`←/→`** | Điều hướng giữa các mục |
| **`Enter`** | Dán nội dung đã chọn |
| **`Shift+Enter`** | Dán không định dạng (plain text) |
| **`Ctrl+C`** | Copy lại nội dung |
| **`Delete`** | Xoá mục |
| **`Esc`** | Đóng panel |

### Menu Chuột Phải
Right-click một mục để:
- Paste / Copy / Delete / Pin
- Mở URL (nếu là link)
- Xem chi tiết & Preview

### Cấu Hình
Nhấp nút **"..."** → **Settings**:
- 🎹 Đổi phím tắt
- 📍 Chọn vị trí panel
- 🎨 Giao diện (Sáng/Tối/Hệ thống)
- 🔒 Quyền riêng tư
- ⏱️ Thời gian giữ lịch

---

## 🔨 Build từ mã nguồn

### Yêu cầu
- **.NET 8 SDK** → https://dotnet.microsoft.com/download/dotnet/8.0
- **Windows App SDK 1.6** → Cài qua Visual Studio 2022 (workload ".NET Desktop Development + WinUI") hoặc riêng

### Clone & Build
```powershell
git clone https://github.com/longdang971/xPasteWin
cd xPasteWin

# Build
dotnet build xPasteWin.sln -c Release -p:Platform=x64

# Chạy thử
dotnet run --project src/xPasteWin

# Unit test
dotnet test tests/xPasteWin.Tests
```

### Publish Self-Contained (Để phân phối)
Tạo bản chứa đầy đủ .NET runtime + Windows App SDK:

```powershell
dotnet publish src/xPasteWin/xPasteWin.csproj -c Release -p:Platform=x64 -r win-x64 `
    -p:SelfContained=true --self-contained -o dist\xPasteWin-win-x64
```

Kết quả: `dist\xPasteWin-win-x64\xPasteWin.exe` (~160 MB) — chạy được trên Windows sạch.

### Đóng Gói thành Installer
```powershell
# Cài Inno Setup (một lần)
winget install JRSoftware.InnoSetup

# Compile bộ cài
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\xPaste.iss
```

→ Tạo ra **`dist\xPaste-Setup-1.0.0.exe`** (có uninstaller)

---

## 🧱 Kiến trúc & Công Nghệ

| Thành phần | Giải pháp |
|---|---|
| **Giao diện** | WinUI 3 (.NET 8) |
| **Theo dõi clipboard** | `AddClipboardFormatListener` + `WM_CLIPBOARDUPDATE` |
| **Phím tắt toàn cục** | `RegisterHotKey` + `WM_HOTKEY` |
| **Panel không giành focus** | Borderless window + `WS_EX_NOACTIVATE\|TOOLWINDOW` + global hooks |
| **Dán vào app** | `SetForegroundWindow` + `SendInput` |
| **Nền kính mờ** | `DesktopAcrylicController` |
| **Lưu trữ** | JSON trong `%AppData%\xPaste\` |
| **Notification** | H.NotifyIcon |

Mã: [`src/xPasteWin/`](src/xPasteWin) — `Models/` · `Services/` · `ViewModels/` · `Views/` · `Interop/`

---

## 📝 Ghi chú quan trọng

- ⚠️ **Chạy non-elevated** (không "Run as administrator") — clipboard manager cần quyền người dùng thường để dán được vào các app khác
- 📁 **Dữ liệu** nằm ở `%AppData%\xPaste\` — gỡ app không tự xoá, xoá thủ công nếu cần
- 🔄 **Tự động cập nhật** — app kiểm tra phiên bản mới trên GitHub

---

*Developed with ❤️ by LQ Team*
