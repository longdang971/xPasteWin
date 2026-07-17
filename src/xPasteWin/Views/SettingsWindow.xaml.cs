using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using xPasteWin.Interop;
using xPasteWin.Models;
using xPasteWin.Services;

namespace xPasteWin.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly ISettings _settings;
    private readonly ClipboardStore _store;
    private readonly HotkeyService _hotkey;
    private readonly TrayService _tray;
    private readonly Action? _quit;   // đóng app "sạch" để bộ cài update ghi đè được file đang chạy
    private readonly IntPtr _hwnd;

    private readonly List<Button> _navButtons = new();
    private string _tab = "General";

    // Brush theo theme (sáng/tối) — lấy từ ThemeService để đồng bộ toàn app.
    private static Brush CardBg => ThemeService.SettingsCardBg;
    private static Brush CardStroke => ThemeService.SettingsCardStroke;
    private static Brush DividerBg => ThemeService.SettingsDivider;
    private static Brush TextPrimary => ThemeService.PrimaryTextBrush;
    private static Brush TextSecondary => ThemeService.SecondaryTextBrush;
    private static readonly Brush AccentBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF)); // accent giữ cả 2 theme
    private static Brush AccentText => ThemeService.AccentText;

    public SettingsWindow(ISettings settings, ClipboardStore store, HotkeyService hotkey, TrayService tray, Action? quit = null)
    {
        _settings = settings; _store = store; _hotkey = hotkey; _tray = tray; _quit = quit;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        Title = "xPaste Settings";
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
        if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        double scale = Win32.GetDpiForWindow(_hwnd) / 96.0; if (scale <= 0) scale = 1;
        int w = (int)(800 * scale), h = (int)(480 * scale);

        // Máy phân giải thấp + scale cao: kẹp kích thước trong vùng làm việc và kéo cửa sổ vào
        // trong màn để title bar / nút đóng không ra ngoài. Nội dung có ScrollViewer nên co lại vẫn dùng được.
        var mon = Win32.MonitorFromWindow(_hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (Win32.GetMonitorInfo(mon, ref mi))
        {
            var work = mi.rcWork;
            w = Math.Min(w, work.Right - work.Left);
            h = Math.Min(h, work.Bottom - work.Top);
            AppWindow.Resize(new SizeInt32(w, h));
            var p0 = AppWindow.Position;
            int x = Math.Clamp(p0.X, work.Left, Math.Max(work.Left, work.Right - w));
            int y = Math.Clamp(p0.Y, work.Top, Math.Max(work.Top, work.Bottom - h));
            if (x != p0.X || y != p0.Y) AppWindow.Move(new PointInt32(x, y));
        }
        else
        {
            AppWindow.Resize(new SizeInt32(w, h));
        }
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
        {
            p.IsResizable = true; p.IsMaximizable = false;
        }

        // Nền kính mờ (acrylic) đồng bộ với panel; title bar mở rộng vào nội dung (liền mạch).
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        ApplyTheme();

        BuildSidebar();
        Select("General");

        // Đổi theme trong tab Appearance → áp lại ngay (Settings đang mở).
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeService.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        ApplyTheme();
        BuildSidebar();   // dựng lại để cập nhật màu chữ/nút theo theme
        Select(_tab);     // dựng lại nội dung tab hiện tại
    }

    // Được đặt true ngay trước khi dựng tab About để tự chạy kiểm tra cập nhật một lần (vào từ menu "…").
    private bool _autoCheckOnAboutBuild;

    /// <summary>Mở thẳng tab About. autoCheck=true (menu "…" trên panel) → kiểm tra NGAY;
    /// false (bấm About ở sidebar Settings) → chờ người dùng bấm nút.</summary>
    public void GoToAbout(bool autoCheck = false)
    {
        _autoCheckOnAboutBuild = autoCheck;
        Select("About");
        Activate();
    }

    // Theo lựa chọn Appearance (System/Light/Dark). Built-in control + ThemeResource tự đổi theo RequestedTheme;
    // các bề mặt tô tay lấy màu từ ThemeService khi dựng lại.
    private void ApplyTheme()
    {
        Root.RequestedTheme = ThemeService.ElementTheme;
        SidebarBorder.Background = ThemeService.SettingsSidebarBg;
        SidebarBorder.BorderBrush = ThemeService.SettingsSidebarBorder;
        AppWindow.TitleBar.ButtonForegroundColor = ThemeService.TitleButtonFg;
    }

    // ---------- Sidebar ----------
    private void BuildSidebar()
    {
        Sidebar.Children.Clear();
        _navButtons.Clear();
        (string tab, string glyph)[] items =
        {
            ("General", ""), ("Privacy", ""),
            ("Appearance", ""), ("About", ""),
        };
        foreach (var (tab, glyph) in items)
        {
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(7),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new FontIcon { Glyph = glyph, FontSize = 15 },
                        new TextBlock { Text = tab, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                    }
                },
                Tag = tab,
            };
            btn.Click += (_, _) => Select(tab);
            _navButtons.Add(btn);
            Sidebar.Children.Add(btn);
        }
    }

    private void Select(string tab)
    {
        _tab = tab;
        var accent = AccentBg;
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var b in _navButtons)
        {
            bool on = (string)b.Tag == tab;
            b.Background = on ? accent : clear;
            if (((StackPanel)b.Content).Children[1] is TextBlock t)
                t.Foreground = on ? new SolidColorBrush(Microsoft.UI.Colors.White)
                                  : TextPrimary;
            if (((StackPanel)b.Content).Children[0] is FontIcon fi)
                fi.Foreground = on ? new SolidColorBrush(Microsoft.UI.Colors.White)
                                  : TextPrimary;
        }
        TabTitle.Text = tab;
        TabHost.Content = tab switch
        {
            "General" => BuildGeneral(),
            "Privacy" => BuildPrivacy(),
            "Appearance" => BuildAppearance(),
            _ => BuildAbout(),
        };
    }

    // ---------- Helpers ----------
    private static Border Card(params UIElement[] children)
    {
        var sp = new StackPanel();
        foreach (var c in children) sp.Children.Add(c);
        return new Border
        {
            Background = CardBg,
            BorderBrush = CardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = sp,
        };
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = TextSecondary,
        Margin = new Thickness(2, 16, 0, 6),
    };

    private static Grid Row(string title, string? subtitle, FrameworkElement control)
    {
        var g = new Grid { Padding = new Thickness(14, 10, 14, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        left.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (subtitle != null)
            left.Children.Add(new TextBlock
            {
                Text = subtitle, FontSize = 12,
                Foreground = TextSecondary,
                TextWrapping = TextWrapping.Wrap,
            });
        Grid.SetColumn(left, 0);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(control, 1);
        g.Children.Add(left); g.Children.Add(control);
        return g;
    }

    private static Border Divider() => new()
    {
        Height = 1, Margin = new Thickness(14, 0, 0, 0),
        Background = DividerBg,
    };

    private ToggleSwitch Toggle(string key, bool def, Action<bool>? onChanged = null)
    {
        var ts = new ToggleSwitch { IsOn = _settings.Get(key, def), OnContent = null, OffContent = null, MinWidth = 0 };
        ts.Toggled += (_, _) => { _settings.Set(key, ts.IsOn); onChanged?.Invoke(ts.IsOn); };
        return ts;
    }

    // ---------- Tab: General ----------
    private UIElement BuildGeneral()
    {
        var root = new StackPanel();

        var launch = new ToggleSwitch { IsOn = LaunchAtLoginService.IsEnabled, OnContent = null, OffContent = null, MinWidth = 0 };
        launch.Toggled += (_, _) => LaunchAtLoginService.Set(launch.IsOn);
        var showTray = Toggle("showTrayIcon", true, v => _tray.SetVisible(v));
        root.Children.Add(Card(
            Row("Launch at login", "Start xPaste when you sign in to Windows.", launch),
            Divider(),
            Row("Show tray icon", "When hidden, open the panel with the shortcut.", showTray)));

        root.Children.Add(Section("Shortcut"));
        root.Children.Add(Card(Row("Show clipboard panel",
            "Click, then press the key combination you want.", BuildHotkeyRecorder())));

        root.Children.Add(Section("Keep history"));
        root.Children.Add(BuildKeepHistory());

        root.Children.Add(Section("Clipboard"));
        root.Children.Add(Card(Row("Always paste as Plain Text",
            "Strip formatting when pasting text items.", Toggle("alwaysPastePlainText", false))));

        root.Children.Add(Section("Storage"));
        int pinned = _store.Items.Count(i => i.IsPinned);
        var erase = new Button { Content = "Erase History…" };
        erase.Click += async (_, _) => await EraseHistoryAsync();
        root.Children.Add(Card(
            Row($"{_store.Items.Count} items stored", $"{pinned} pinned",
                new TextBlock { Text = "" }),
            Divider(),
            Row("Clear unpinned history", "Remove all items except pinned ones.", erase)));

        return root;
    }

    private Button BuildHotkeyRecorder()
    {
        var btn = new Button { MinWidth = 130 };
        btn.Content = _settings.Get("hotkeyDisplay", "Ctrl+Shift+V");
        bool recording = false;
        btn.Click += (_, _) =>
        {
            recording = true;
            btn.Content = "Press keys…";
        };
        btn.KeyDown += (_, e) =>
        {
            if (!recording) return;
            e.Handled = true;
            if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
                or VirtualKey.LeftWindows or VirtualKey.RightWindows) return; // chờ phím chính
            if (e.Key == VirtualKey.Escape) { recording = false; btn.Content = _settings.Get("hotkeyDisplay", "Ctrl+Shift+V"); return; }

            bool ctrl = Down(VirtualKey.Control), shift = Down(VirtualKey.Shift),
                 alt = Down(VirtualKey.Menu), win = Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows);
            uint mods = 0;
            if (ctrl) mods |= Win32.MOD_CONTROL;
            if (shift) mods |= Win32.MOD_SHIFT;
            if (alt) mods |= Win32.MOD_ALT;
            if (win) mods |= Win32.MOD_WIN;
            if (mods == 0) { btn.Content = "Need a modifier…"; return; } // yêu cầu ít nhất 1 modifier

            uint vk = (uint)e.Key;
            var display = HotkeyDisplay(mods, e.Key);

            // Lưu combo cũ để rollback nếu combo mới bị app khác chiếm (RegisterHotKey false) — nếu không,
            // hotkey cũ đã bị gỡ mà mới không đăng ký được → mất hẳn phím tắt, còn lưu combo hỏng.
            int oldMods = _settings.Get("hotkeyModifiers", (int)(Win32.MOD_CONTROL | Win32.MOD_SHIFT));
            int oldVk = _settings.Get("hotkeyVk", (int)Win32.VK_V);
            string oldDisplay = _settings.Get("hotkeyDisplay", "Ctrl+Shift+V");

            _settings.Set("hotkeyModifiers", (int)mods);
            _settings.Set("hotkeyVk", (int)vk);
            _settings.Set("hotkeyDisplay", display);
            recording = false;
            if (_hotkey.Register())
            {
                btn.Content = display;
            }
            else
            {
                // Rollback: khôi phục combo cũ (vẫn hoạt động) + báo combo bị chiếm.
                _settings.Set("hotkeyModifiers", oldMods);
                _settings.Set("hotkeyVk", oldVk);
                _settings.Set("hotkeyDisplay", oldDisplay);
                _hotkey.Register();
                btn.Content = $"{display} in use — kept {oldDisplay}";
            }
        };
        return btn;
    }

    private static bool Down(VirtualKey k) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static string HotkeyDisplay(uint mods, VirtualKey key)
    {
        var parts = new List<string>();
        if ((mods & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & Win32.MOD_WIN) != 0) parts.Add("Win");
        if ((mods & Win32.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private static readonly string[] KeepLabels = { "Day", "Week", "Month", "Year", "Forever" };
    private Border BuildKeepHistory()
    {
        int idx = _settings.Get("keepHistoryIndex", 4);
        var desc = new TextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = TextSecondary,
            Margin = new Thickness(14, 0, 14, 10),
        };
        var slider = new Slider { Minimum = 0, Maximum = 4, StepFrequency = 1, Value = idx, Margin = new Thickness(14, 6, 14, 0) };
        var labels = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        for (int i = 0; i < 5; i++) labels.ColumnDefinitions.Add(new ColumnDefinition());
        var lblBlocks = new TextBlock[5];
        for (int i = 0; i < 5; i++)
        {
            lblBlocks[i] = new TextBlock { Text = KeepLabels[i], FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(lblBlocks[i], i);
            labels.Children.Add(lblBlocks[i]);
        }
        void Update(int i)
        {
            desc.Text = i == 4
                ? "Items are kept forever until you remove them."
                : $"Unpinned items older than 1 {KeepLabels[i].ToLower()} are removed automatically. Pinned items are always kept.";
            for (int k = 0; k < 5; k++)
                lblBlocks[k].Foreground = k == i
                    ? AccentText
                    : TextSecondary;
        }
        slider.ValueChanged += (_, _) =>
        {
            int i = (int)slider.Value;
            _settings.Set("keepHistoryIndex", i);
            _store.PruneExpired();
            Update(i);
        };
        Update(idx);

        var col = new StackPanel();
        col.Children.Add(slider);
        col.Children.Add(labels);
        col.Children.Add(new Border { Height = 8 });
        col.Children.Add(desc);
        col.Children.Add(Divider());
        col.Children.Add(Row("Clear history on sign out or shutdown",
            "Erase every item (including pinned) when you sign out, restart, or shut down the PC.", Toggle("clearOnLogout", false)));
        return new Border
        {
            Background = CardBg,
            BorderBrush = CardStroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Child = col, Padding = new Thickness(0, 6, 0, 0),
        };
    }

    private async System.Threading.Tasks.Task EraseHistoryAsync()
    {
        var dlg = new ContentDialog
        {
            Title = "Erase history",
            Content = "Delete all unpinned clipboard history?",
            PrimaryButtonText = "Erase",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = ThemeService.ElementTheme,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            _store.ClearUnpinned();
            Select("General"); // dựng lại để cập nhật số lượng
        }
    }

    // ---------- Tab: Privacy ----------
    private UIElement BuildPrivacy()
    {
        var root = new StackPanel();
        root.Children.Add(Card(
            Row("Show during screen sharing", "Allow the panel to appear in screen captures and shares.",
                Toggle("showDuringScreenSharing", true)),
            Divider(),
            Row("Ignore confidential content", "Don't save passwords and sensitive data when detected.",
                Toggle("ignoreConfidentialContent", true)),
            Divider(),
            Row("Ignore transient content", "Don't save temporary data generated by other apps.",
                Toggle("ignoreTransientContent", true))));

        root.Children.Add(Section("Ignore applications"));
        root.Children.Add(new TextBlock
        {
            Text = "Don't save content copied from the applications below.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = TextSecondary,
            Margin = new Thickness(2, 0, 0, 6),
        });
        root.Children.Add(BuildIgnoreApps());
        return root;
    }

    private Border BuildIgnoreApps()
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 180 };
        void Reload()
        {
            var apps = _settings.Get("ignoredApps", Array.Empty<string>());
            list.ItemsSource = apps.Select(p => new { Path = p, Name = System.IO.Path.GetFileNameWithoutExtension(p) }).ToList();
            list.DisplayMemberPath = "Name";
        }
        Reload();

        var add = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(8) };
        var rem = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(8), Margin = new Thickness(6, 0, 0, 0) };
        add.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeFilter.Add(".exe");
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                var apps = _settings.Get("ignoredApps", Array.Empty<string>()).ToList();
                if (!apps.Contains(file.Path, StringComparer.OrdinalIgnoreCase)) apps.Add(file.Path);
                _settings.Set("ignoredApps", apps.ToArray());
                Reload();
            }
        };
        rem.Click += (_, _) =>
        {
            if (list.SelectedItem is null) return;
            dynamic sel = list.SelectedItem;
            string path = sel.Path;
            var apps = _settings.Get("ignoredApps", Array.Empty<string>()).Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToArray();
            _settings.Set("ignoredApps", apps);
            Reload();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(add); buttons.Children.Add(rem);

        var col = new StackPanel();
        col.Children.Add(list);
        col.Children.Add(buttons);
        return new Border
        {
            Background = CardBg,
            BorderBrush = CardStroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = col,
        };
    }

    // ---------- Tab: Appearance ----------
    private UIElement BuildAppearance()
    {
        var root = new StackPanel();

        root.Children.Add(Section("Theme"));
        var theme = new ComboBox { MinWidth = 160 };
        foreach (var t in new[] { "System", "Light", "Dark" }) theme.Items.Add(t);
        theme.SelectedIndex = ThemeService.Mode switch { "light" => 1, "dark" => 2, _ => 0 };
        theme.SelectionChanged += (_, _) =>
            ThemeService.Mode = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        root.Children.Add(Card(Row("Appearance",
            "“System” follows your Windows light or dark setting.", theme)));

        root.Children.Add(Section("Panel"));
        var pos = new ComboBox { MinWidth = 160 };
        foreach (var t in new[] { "Bottom", "Top", "Left", "Right" }) pos.Items.Add(t);
        pos.SelectedIndex = _settings.Get("panelPosition", "bottom") switch { "top" => 1, "left" => 2, "right" => 3, _ => 0 };
        pos.SelectionChanged += (_, _) =>
            _settings.Set("panelPosition", pos.SelectedIndex switch { 1 => "top", 2 => "left", 3 => "right", _ => "bottom" });
        root.Children.Add(Card(
            Row("Panel position", "Where the panel slides in from.", pos),
            Divider(),
            Row("Load link previews", "Fetch title and image for copied links (needs internet).",
                Toggle("linkPreviewEnabled", true, v => LinkPreviewService.Enabled = v))));

        return root;
    }

    // ---------- Tab: About ----------
    private UIElement BuildAbout()
    {
        var sp = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 28, 0, 28) };
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.png");
        if (System.IO.File.Exists(iconPath))
        {
            var img = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath)) { DecodePixelWidth = 144 };
            sp.Children.Add(new Image { Width = 72, Height = 72, Source = img, Margin = new Thickness(0, 0, 0, 6) });
        }
        sp.Children.Add(new TextBlock { Text = "xPaste", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = $"Version {UpdateService.CurrentVersionText}", FontSize = 13, Foreground = TextSecondary, HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = "Powered by LQ Team", FontSize = 13, Foreground = TextSecondary, HorizontalAlignment = HorizontalAlignment.Center });

        // Dòng trạng thái + thanh tiến trình (tải) + nút. Chỉ kiểm tra khi người dùng bấm nút.
        var status = new TextBlock
        {
            FontSize = 12.5, Foreground = TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0), MaxWidth = 380,
        };
        var bar = new ProgressBar { Width = 240, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var btn = new Button { Content = "Check for Updates", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };

        // Máy trạng thái nút: check → download → install (hoặc → page nếu không có bộ cài / lỗi).
        string mode = "check";
        UpdateInfo? pending = null;
        string? downloadedPath = null;

        async System.Threading.Tasks.Task RunStepAsync()
        {
            switch (mode)
            {
                case "check":
                    btn.IsEnabled = false;
                    status.Text = "Checking for updates…";
                    var info = await UpdateService.CheckAsync();
                    btn.IsEnabled = true;
                    if (info is null)
                    {
                        status.Text = $"You’re on the latest version ({UpdateService.CurrentVersionText}).";
                        btn.Content = "Check for Updates";
                        mode = "check";
                    }
                    else
                    {
                        pending = info;
                        status.Text = $"New version {info.Version.ToString(3)} is available.";
                        if (UpdateService.HasInstaller(info)) { mode = "download"; btn.Content = "Download & Install"; }
                        else { mode = "page"; btn.Content = "Open Download Page"; }
                    }
                    break;

                case "download":
                    btn.IsEnabled = false;
                    bar.Visibility = Visibility.Visible; bar.IsIndeterminate = false; bar.Value = 0;
                    status.Text = "Downloading update…";
                    try
                    {
                        var prog = new Progress<double>(p =>
                        {
                            if (p < 0) { bar.IsIndeterminate = true; }
                            else { bar.IsIndeterminate = false; bar.Value = p * 100; status.Text = $"Downloading update… {(int)(p * 100)}%"; }
                        });
                        downloadedPath = await UpdateService.DownloadInstallerAsync(pending!, prog);
                        bar.Visibility = Visibility.Collapsed;
                        status.Text = "Ready to install. xPaste will close to apply the update.";
                        mode = "install"; btn.Content = "Install & Restart"; btn.IsEnabled = true;
                    }
                    catch
                    {
                        bar.Visibility = Visibility.Collapsed;
                        status.Text = "Download failed — check your connection or open the download page.";
                        mode = "page"; btn.Content = "Open Download Page"; btn.IsEnabled = true;
                    }
                    break;

                case "install":
                    if (UpdateService.LaunchInstaller(downloadedPath!))
                        _quit?.Invoke();   // đóng app sạch → Inno Setup ghi đè file đang chạy
                    else
                    {
                        status.Text = "Couldn’t start the installer. Try the download page.";
                        mode = "page"; btn.Content = "Open Download Page";
                    }
                    break;

                case "page":
                    if (pending != null) UpdateService.OpenReleasePage(pending);
                    break;
            }
        }

        btn.Click += async (_, _) => await RunStepAsync();

        sp.Children.Add(status);
        sp.Children.Add(bar);
        sp.Children.Add(btn);

        // Vào tab About từ menu "…" trên panel → kiểm tra NGAY. Vào từ sidebar Settings → chờ bấm nút.
        if (_autoCheckOnAboutBuild)
        {
            _autoCheckOnAboutBuild = false;
            DispatcherQueue.TryEnqueue(async () => await RunStepAsync());
        }

        return Card(sp);
    }
}
