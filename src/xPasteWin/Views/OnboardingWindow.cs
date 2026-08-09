using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using xPasteWin.Interop;
using xPasteWin.Models;
using xPasteWin.Services;

namespace xPasteWin.Views;

/// <summary>
/// Màn hình chào lần chạy đầu (port OnboardingView của macOS): giới thiệu phím tắt + vài lựa chọn
/// nhanh (chạy nền, tray icon, thời gian giữ lịch sử). Bấm "Get Started" để bắt đầu dùng.
/// </summary>
public sealed class OnboardingWindow : Window
{
    private readonly ISettings _settings;
    private readonly Action _onDone;

    private static Brush TextSecondary => ThemeService.SecondaryTextBrush;

    public OnboardingWindow(ISettings settings, Action onDone)
    {
        _settings = settings;
        _onDone = onDone;

        Title = "Welcome to xPaste";
        var hwnd = WindowNative.GetWindowHandle(this);
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
        if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);

        double scale = Win32.GetDpiForWindow(hwnd) / 96.0; if (scale <= 0) scale = 1;
        AppWindow.Resize(new SizeInt32((int)(460 * scale), (int)(480 * scale)));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
        { p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; }

        SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;

        Content = BuildContent();
        if (Content is FrameworkElement fe) fe.RequestedTheme = ThemeService.ElementTheme;
        AppWindow.TitleBar.ButtonForegroundColor = ThemeService.TitleButtonFg;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
    }

    private UIElement BuildContent()
    {
        var root = new StackPanel { Padding = new Thickness(28, 40, 28, 24), Spacing = 10 };

        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.png");
        if (System.IO.File.Exists(iconPath))
            root.Children.Add(new Image
            {
                Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center,
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath)) { DecodePixelWidth = 128 },
            });

        root.Children.Add(new TextBlock
        {
            Text = "Welcome to xPaste", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var hotkey = _settings.Get("hotkeyDisplay", "Ctrl+Shift+V");
        root.Children.Add(new TextBlock
        {
            Text = $"Press {hotkey} anytime to open your clipboard history.",
            FontSize = 13, Foreground = TextSecondary, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var launch = new ToggleSwitch { IsOn = LaunchAtLoginService.IsEnabled, OnContent = null, OffContent = null, MinWidth = 0 };
        launch.Toggled += (_, _) => LaunchAtLoginService.Set(launch.IsOn);
        var tray = new ToggleSwitch { IsOn = _settings.Get("showTrayIcon", true), OnContent = null, OffContent = null, MinWidth = 0 };
        tray.Toggled += (_, _) => _settings.Set("showTrayIcon", tray.IsOn);

        root.Children.Add(Card(
            Row("Run in background", "Start xPaste when you sign in to Windows.", launch),
            Divider(),
            Row("Show tray icon", "Open the panel from the tray or with the shortcut.", tray)));

        root.Children.Add(new TextBlock
        {
            Text = "History is kept forever until you remove it — change this anytime in Settings.",
            FontSize = 12, Foreground = TextSecondary, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 4, 2, 0),
        });

        var start = new Button
        {
            Content = "Get Started",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 12, 0, 0),
            Background = ThemeService.AccentBrush,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            Padding = new Thickness(0, 8, 0, 8),
        };
        start.Click += (_, _) => { _onDone(); Close(); };
        root.Children.Add(start);

        return root;
    }

    private static Border Card(params UIElement[] children)
    {
        var sp = new StackPanel();
        foreach (var c in children) sp.Children.Add(c);
        return new Border
        {
            Background = ThemeService.SettingsCardBg,
            BorderBrush = ThemeService.SettingsCardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = sp,
        };
    }

    private static Grid Row(string title, string subtitle, FrameworkElement control)
    {
        var g = new Grid { Padding = new Thickness(14, 10, 14, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        left.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        left.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = TextSecondary, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(left, 0);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(control, 1);
        g.Children.Add(left); g.Children.Add(control);
        return g;
    }

    private static Border Divider() => new()
    {
        Height = 1, Margin = new Thickness(14, 0, 0, 0), Background = ThemeService.SettingsDivider,
    };
}
