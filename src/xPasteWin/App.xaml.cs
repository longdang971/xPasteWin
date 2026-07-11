using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using xPasteWin.Interop;
using xPasteWin.Models;
using xPasteWin.Services;
using xPasteWin.ViewModels;
using xPasteWin.Views;

namespace xPasteWin;

public partial class App : Application
{
    // Giữ tham chiếu sống suốt vòng đời app (app không có cửa sổ chính, chỉ tray + panel).
    private SettingsService _settings = null!;
    private ClipboardStore _store = null!;
    private MessageWindow _msg = null!;
    private ClipboardMonitor _monitor = null!;
    private HotkeyService _hotkey = null!;
    private PasteService _paste = null!;
    private TrayService _tray = null!;
    private PanelViewModel _panelVm = null!;
    private PanelWindow _panel = null!;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _settings = new SettingsService();
        ThemeService.Init(_settings);
        _store = new ClipboardStore(_settings);
        _msg = new MessageWindow();
        _monitor = new ClipboardMonitor(_msg, _settings);
        _hotkey = new HotkeyService(_msg, _settings);
        _paste = new PasteService();
        _tray = new TrayService();
        _panelVm = new PanelViewModel(_store, _paste, _monitor, _settings);

        // Áp dụng setting lúc khởi động
        LinkPreviewService.Enabled = _settings.Get("linkPreviewEnabled", true);

        // Launch-at-login: BẬT MẶC ĐỊNH ở lần chạy đầu; sau đó tôn trọng lựa chọn của người dùng.
        if (!_settings.Get("launchAtLoginInitialized", false))
        {
            LaunchAtLoginService.Set(true);
            _settings.Set("launchAtLoginInitialized", true);
        }

        _panel = new PanelWindow(_settings);
        _panel.Bind(_panelVm);
        _panel.CloseRequested += () => _panel.DispatcherQueue.TryEnqueue(() => _panel.HidePanel());
        _panel.SettingsRequested += () => _panel.DispatcherQueue.TryEnqueue(OpenSettings);
        _panel.QuitRequested += () => _panel.DispatcherQueue.TryEnqueue(Quit);
        // Panel cần được kích hoạt một lần để dựng cây visual, rồi ẩn ngay.
        _panel.Activate();
        _panel.HideImmediately();

        // --- Clipboard capture ---
        _monitor.ItemCaptured += it => _panel.DispatcherQueue.TryEnqueue(() =>
        {
            _store.Add(it);
            if (_panel.IsPanelVisible) _panelVm.Refresh();
        });
        _monitor.Start();

        // --- Hotkey toggle ---
        _hotkey.Triggered += () => _panel.DispatcherQueue.TryEnqueue(() =>
        {
            if (_panel.IsPanelVisible) { _panel.HidePanel(); return; }
            _paste.CaptureForegroundWindow();
            _panelVm.Refresh();
            _panel.ShowPanel();
        });
        _hotkey.Register();

        // --- Paste flow ---
        _panelVm.PasteFinished += () => _panel.DispatcherQueue.TryEnqueue(async () =>
        {
            _panel.HidePanel();
            await _paste.PasteAsync();
            if (_panelVm.PendingReorder is { } it)
            {
                _store.MoveToTop(it);
                _panelVm.ClearPendingReorder();
            }
        });

        // --- Tray ---
        _tray.OpenRequested += () => _panel.DispatcherQueue.TryEnqueue(() =>
        {
            if (_panel.IsPanelVisible) return;
            _paste.CaptureForegroundWindow();
            _panelVm.Refresh();
            _panel.ShowPanel();
        });
        _tray.SettingsRequested += () => _panel.DispatcherQueue.TryEnqueue(OpenSettings);
        _tray.QuitRequested += () => _panel.DispatcherQueue.TryEnqueue(Quit);
        _tray.Show();
        _tray.SetVisible(_settings.Get("showTrayIcon", true));

        // Xoá lịch sử khi đăng xuất/tắt máy HOẶC khi máy ngủ (nếu bật) — giống macOS
        // (willSleepNotification + willPowerOffNotification). Giữ handler làm field để gỡ khi Quit.
        _onSessionEnding = (_, _) => ClearHistoryForShutdown();
        _onPowerModeChanged = (_, e) => { if (e.Mode == Microsoft.Win32.PowerModes.Suspend) ClearHistoryForShutdown(); };
        Microsoft.Win32.SystemEvents.SessionEnding += _onSessionEnding;
        Microsoft.Win32.SystemEvents.PowerModeChanged += _onPowerModeChanged;
    }

    private Microsoft.Win32.SessionEndingEventHandler? _onSessionEnding;
    private Microsoft.Win32.PowerModeChangedEventHandler? _onPowerModeChanged;

    /// <summary>Clear-on-sleep/logout: xóa ĐĨA đồng bộ ngay (trên thread SystemEvents, an toàn vì chỉ IO),
    /// còn dọn ObservableCollection thì marshal về UI thread (collection KHÔNG thread-safe).</summary>
    private void ClearHistoryForShutdown()
    {
        if (!_settings.Get("clearOnLogout", false)) return;
        _store.WipeDiskFiles();
        _panel.DispatcherQueue.TryEnqueue(() =>
        {
            _store.ClearAll();
            if (_panel.IsPanelVisible) _panelVm.Refresh();
        });
    }

    private bool _quitting;

    private void Quit()
    {
        if (_quitting) return; // chống tái nhập (Quit nối từ panel + tray + menu, có thể kích hoạt sát nhau)
        _quitting = true;
        if (_onSessionEnding != null) Microsoft.Win32.SystemEvents.SessionEnding -= _onSessionEnding;
        if (_onPowerModeChanged != null) Microsoft.Win32.SystemEvents.PowerModeChanged -= _onPowerModeChanged;
        _panel.HideImmediately(); // gỡ global hook LL (tránh lag phím/chuột toàn hệ thống khi Quit lúc panel mở)
        _monitor.Stop();
        _hotkey.Unregister();
        _tray.Dispose();
        _msg.Dispose();
        Exit();
    }

    private SettingsWindow? _settingsWindow;

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, _store, _hotkey, _tray);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
    }
}
