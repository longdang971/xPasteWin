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

        // Clear-on-sign-out: nếu bật, xoá sạch lịch sử NGAY khi khởi động. Sau khi đăng xuất/tắt/khởi
        // động lại, app chạy lại cùng Windows → lịch sử trống. Sleep chỉ resume (KHÔNG khởi động lại)
        // nên lịch sử được giữ — đúng lựa chọn "chỉ xoá khi đăng xuất/tắt". Cách này chắc chắn hoạt
        // động vì không phụ thuộc việc bắt được message shutdown (thứ không đáng tin trong WinUI 3).
        if (_settings.Get("clearOnLogout", false)) { _store.WipeDiskFiles(); _store.ClearAll(); }

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
        _panel.SettingsRequested += () => _panel.DispatcherQueue.TryEnqueue(() => OpenSettings());
        _panel.UpdateRequested += () => _panel.DispatcherQueue.TryEnqueue(() => OpenSettings(about: true));
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
        _tray.SettingsRequested += () => _panel.DispatcherQueue.TryEnqueue(() => OpenSettings());
        _tray.QuitRequested += () => _panel.DispatcherQueue.TryEnqueue(Quit);
        _tray.Show();
        _tray.SetVisible(_settings.Get("showTrayIcon", true));

        // Xoá lịch sử khi đăng xuất/tắt máy HOẶC khi máy ngủ (nếu bật) — giống macOS
        // (willSleepNotification + willPowerOffNotification). Nghe qua message của panel (cửa sổ
        // top-level) thay vì Microsoft.Win32.SystemEvents: SystemEvents KHÔNG nhận được event trong
        // app WinUI 3 (thiếu message pump WinForms) nên trước đây lịch sử chưa từng được xoá.
        _panel.SystemEnding += ClearHistoryForShutdown;
    }

    /// <summary>Clear-on-sleep/logout: chạy ĐỒNG BỘ trên UI thread (do PanelWindow.SystemEnding phát từ
    /// window proc). Xoá ĐĨA trước (kịp trước khi tiến trình bị kết thúc), rồi dọn ObservableCollection
    /// (an toàn vì đang ở UI thread).</summary>
    private void ClearHistoryForShutdown()
    {
        if (!_settings.Get("clearOnLogout", false)) return;
        _store.WipeDiskFiles();
        _store.ClearAll();
        if (_panel.IsPanelVisible) _panelVm.Refresh();
    }

    private bool _quitting;

    private void Quit()
    {
        if (_quitting) return; // chống tái nhập (Quit nối từ panel + tray + menu, có thể kích hoạt sát nhau)
        _quitting = true;
        _panel.HideImmediately(); // gỡ global hook LL (tránh lag phím/chuột toàn hệ thống khi Quit lúc panel mở)
        _monitor.Stop();
        _hotkey.Unregister();
        _tray.Dispose();
        _msg.Dispose();
        Exit();
    }

    private SettingsWindow? _settingsWindow;

    private void OpenSettings(bool about = false)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            if (about) _settingsWindow.GoToAbout(autoCheck: true);
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, _store, _hotkey, _tray, Quit);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
        if (about) _settingsWindow.GoToAbout(autoCheck: true);
    }
}
