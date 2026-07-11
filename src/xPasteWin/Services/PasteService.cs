using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using xPasteWin.Interop;

namespace xPasteWin.Services;

public sealed class PasteService
{
    private IntPtr _previous;

    /// <summary>Gọi ngay TRƯỚC khi hiện panel: lưu cửa sổ app đang dùng để dán trả về.</summary>
    public void CaptureForegroundWindow() => _previous = Win32.GetForegroundWindow();

    /// <summary>Tên app đích đã lưu (cho nhãn "Paste to &lt;App&gt;"); null nếu không rõ.</summary>
    public string? TargetAppName => SourceAppService.GetWindowAppName(_previous);

    public async Task PasteAsync()
    {
        if (_previous == IntPtr.Zero || !Win32.IsWindow(_previous)) return;
        Win32.SetForegroundWindow(_previous);
        await Task.Delay(60);
        SendCtrlV();
    }

    private static void SendCtrlV()
    {
        // Nhả các modifier từ hotkey (Ctrl+Shift+V) trước khi bơm Ctrl+V để app đích nhận đúng "paste"
        // chứ không phải Ctrl+Shift+V. Chỉ nhả modifier ĐANG THỰC SỰ giữ — tránh keyup trần Alt/Win
        // (một số app cũ kích menu-bar/Start menu khi nhận Alt-up/Win-up không kèm keydown).
        var list = new List<Win32.INPUT>(8);
        foreach (ushort vk in new ushort[] { Win32.VK_SHIFT, Win32.VK_MENU, Win32.VK_LWIN, Win32.VK_RWIN })
            if ((Win32.GetAsyncKeyState(vk) & 0x8000) != 0) list.Add(KeyInput(vk, true));
        list.Add(KeyInput(Win32.VK_CONTROL, false));
        list.Add(KeyInput(Win32.VK_V, false));
        list.Add(KeyInput(Win32.VK_V, true));
        list.Add(KeyInput(Win32.VK_CONTROL, true));
        var inputs = list.ToArray();
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    private static Win32.INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        u = new Win32.InputUnion
        {
            ki = new Win32.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = up ? Win32.KEYEVENTF_KEYUP : 0,
            }
        }
    };
}
