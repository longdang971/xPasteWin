using System;
using System.IO;
using H.NotifyIcon.Core;

namespace xPasteWin.Services;

public sealed class TrayService : IDisposable
{
    private TrayIconWithContextMenu? _icon;
    private System.Drawing.Icon? _sysIcon;
    private bool _ownsIcon; // true nếu icon tự tạo từ file (được dispose); false nếu là SystemIcons dùng chung

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public void Show()
    {
        var menu = new PopupMenu();
        menu.Items.Add(new PopupMenuItem("Open Clipboard", (_, _) => OpenRequested?.Invoke()));
        menu.Items.Add(new PopupMenuItem("Settings…", (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(new PopupMenuSeparator());
        menu.Items.Add(new PopupMenuItem("Quit xPaste", (_, _) => QuitRequested?.Invoke()));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
        _ownsIcon = File.Exists(iconPath);
        _sysIcon = _ownsIcon
            ? new System.Drawing.Icon(iconPath)
            : System.Drawing.SystemIcons.Application;

        _icon = new TrayIconWithContextMenu
        {
            ToolTip = "xPaste",
            ContextMenu = menu,
            Icon = _sysIcon.Handle,
        };
        _icon.MessageWindow.MouseEventReceived += (_, e) =>
        {
            if (e.MouseEvent == MouseEvent.IconLeftMouseUp) OpenRequested?.Invoke();
        };
        _icon.Create();
    }

    /// <summary>Ẩn/hiện tray icon (setting "Show tray icon").</summary>
    public void SetVisible(bool visible)
    {
        if (_icon == null) return;
        try { _icon.Visibility = visible ? IconVisibility.Visible : IconVisibility.Hidden; } catch { }
    }

    public void Dispose()
    {
        _icon?.Dispose();
        if (_ownsIcon) _sysIcon?.Dispose(); // KHÔNG dispose SystemIcons.Application (handle dùng chung)
    }
}
