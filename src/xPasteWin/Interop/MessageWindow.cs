using System;
using System.Runtime.InteropServices;

namespace xPasteWin.Interop;

/// <summary>
/// Cửa sổ message-only (HWND_MESSAGE) sở hữu một window proc riêng để nhận
/// WM_CLIPBOARDUPDATE và WM_HOTKEY — thay cho NotificationCenter/Carbon bên macOS.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    public event Action? ClipboardUpdated;
    public event Action<int>? HotkeyPressed;

    public IntPtr Handle { get; }

    // Giữ delegate sống để GC không thu hồi con trỏ hàm đã trao cho Win32.
    private readonly WndProc _wndProc;
    private readonly ushort _atom;

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public MessageWindow(string className = "xPasteMsgWnd")
    {
        _wndProc = WindowProc;
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        _atom = RegisterClass(ref wc);
        Handle = CreateWindowEx(0, className, className, 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CLIPBOARDUPDATE: ClipboardUpdated?.Invoke(); return IntPtr.Zero;
            case WM_HOTKEY: HotkeyPressed?.Invoke(wParam.ToInt32()); return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyWindow(Handle);
        if (_atom != 0) UnregisterClass(_atom, GetModuleHandle(null));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(ushort atom, IntPtr hInstance);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
