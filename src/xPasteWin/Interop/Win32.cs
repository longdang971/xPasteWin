using System;
using System.Runtime.InteropServices;

namespace xPasteWin.Interop;

internal static class Win32
{
    public const int WM_HOTKEY = 0x0312;
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;

    public const int GWL_EXSTYLE = -20;
    public const int GWLP_WNDPROC = -4;
    public const int WM_NCCALCSIZE = 0x0083;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10,
                      SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const ushort VK_CONTROL = 0x11, VK_V = 0x56,
                        VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    public const uint KEYEVENTF_KEYUP = 0x2;

    public const uint INPUT_KEYBOARD = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);

    /// <summary>Clipboard hiện có format tên này không (dùng cho cờ privacy như ExcludeClipboardContentFromMonitorProcessing).</summary>
    public static bool IsClipboardFormatPresent(string name)
    {
        var fmt = RegisterClipboardFormat(name);
        return fmt != 0 && IsClipboardFormatAvailable(fmt);
    }

    /// <summary>Format DWORD (vd CanIncludeInClipboardHistory) tồn tại VÀ giá trị == 0 (không cho lưu).</summary>
    public static bool ClipboardFormatDwordIsZero(string name)
    {
        var fmt = RegisterClipboardFormat(name);
        if (fmt == 0 || !IsClipboardFormatAvailable(fmt)) return false;
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            var h = GetClipboardData(fmt);
            if (h == IntPtr.Zero) return false;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return false;
            try { return Marshal.ReadInt32(p) == 0; }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);

    // DPI hiệu dụng theo MÀN HÌNH (đa màn hình khác DPI) — dùng khi định vị theo monitor con trỏ.
    public const int MDT_EFFECTIVE_DPI = 0;
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Hệ số scale (DPI/96) của một màn hình cụ thể; 1.0 nếu lỗi.</summary>
    public static double ScaleForMonitor(IntPtr mon)
    {
        try
        {
            if (mon != IntPtr.Zero && GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0)
                return dx / 96.0;
        }
        catch { }
        return 1.0;
    }

    // Ẩn cửa sổ khỏi screen capture/share (WDA_EXCLUDEFROMCAPTURE) hoặc cho hiện (WDA_NONE).
    public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        System.Text.StringBuilder pwszBuff, int cchBuff, uint wFlags);

    // Tắt viền cửa sổ do DWM vẽ (Windows 11): DWMWA_BORDER_COLOR = 34, DWMWA_COLOR_NONE = 0xFFFFFFFE.
    public const int DWMWA_BORDER_COLOR = 34;
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
    }

    // INPUT phải marshal đúng 40 byte trên x64 (nếu không SendInput từ chối vì cbSize sai → không gửi phím).
    // Union PHẢI đủ lớn cho MOUSEINPUT (lớn nhất) dù ta chỉ dùng KEYBDINPUT.
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg; public ushort wParamL; public ushort wParamH;
    }
}
