using System;
using System.Runtime.InteropServices;

namespace xPasteWin.Interop;

/// <summary>
/// Hook bàn phím + chuột toàn cục (WH_KEYBOARD_LL / WH_MOUSE_LL), chỉ bật khi panel mở.
/// Cho phép panel nhận phím điều hướng + chữ search VÀ bắt click ra ngoài mà KHÔNG cần
/// giành focus (panel giữ WS_EX_NOACTIVATE → app đích luôn giữ foreground để dán được).
/// Tái tạo mô hình "nonactivating panel + global monitor" của macOS.
/// </summary>
public sealed class GlobalHooks : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    private const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204;

    /// <summary>Trả true để "nuốt" phím (không cho tới app đích).</summary>
    public event Func<int, bool>? KeyDown;
    /// <summary>Click chuột (toạ độ màn hình vật lý).</summary>
    public event Action<int, int>? MouseDown;

    private IntPtr _kb, _mouse;
    private HookProc? _kbProc, _mouseProc; // giữ delegate sống, tránh GC
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    public void Install()
    {
        var hMod = GetModuleHandle(null);
        if (_kb == IntPtr.Zero)
        {
            _kbProc = KbProc;
            _kb = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
        }
        if (_mouse == IntPtr.Zero)
        {
            _mouseProc = MouseProc;
            _mouse = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
        }
    }

    public void Uninstall()
    {
        if (_kb != IntPtr.Zero) { UnhookWindowsHookEx(_kb); _kb = IntPtr.Zero; }
        if (_mouse != IntPtr.Zero) { UnhookWindowsHookEx(_mouse); _mouse = IntPtr.Zero; }
    }

    private IntPtr KbProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
            if (KeyDown?.Invoke(vk) == true) return (IntPtr)1;
        }
        return CallNextHookEx(_kb, code, wParam, lParam);
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == (IntPtr)WM_LBUTTONDOWN || wParam == (IntPtr)WM_RBUTTONDOWN))
        {
            int x = Marshal.ReadInt32(lParam);     // MSLLHOOKSTRUCT.pt.x
            int y = Marshal.ReadInt32(lParam, 4);  // .pt.y
            MouseDown?.Invoke(x, y);
        }
        return CallNextHookEx(_mouse, code, wParam, lParam);
    }

    public void Dispose() => Uninstall();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
