using System;
using xPasteWin.Interop;

namespace xPasteWin.Services;

public sealed class HotkeyService
{
    private const int HotkeyId = 1;
    private readonly MessageWindow _msg;
    private readonly ISettings _settings;
    private bool _registered;

    public event Action? Triggered;

    public HotkeyService(MessageWindow msg, ISettings settings)
    {
        _msg = msg;
        _settings = settings;
        _msg.HotkeyPressed += id => { if (id == HotkeyId) Triggered?.Invoke(); };
    }

    /// <summary>Đăng ký hotkey (mặc định Ctrl+Shift+V). Trả false nếu trùng phím app khác.</summary>
    public bool Register()
    {
        Unregister();
        uint mods = (uint)_settings.Get("hotkeyModifiers", (int)(Win32.MOD_CONTROL | Win32.MOD_SHIFT));
        uint vk = (uint)_settings.Get("hotkeyVk", (int)Win32.VK_V);
        _registered = Win32.RegisterHotKey(_msg.Handle, HotkeyId, mods, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) { Win32.UnregisterHotKey(_msg.Handle, HotkeyId); _registered = false; }
    }
}
