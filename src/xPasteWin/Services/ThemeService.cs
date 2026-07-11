using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace xPasteWin.Services;

/// <summary>
/// Quản lý giao diện sáng/tối/theo-hệ-thống (tương đương AppearanceManager của macOS). Cung cấp
/// ElementTheme để áp cho root cửa sổ + bảng màu (palette) cho các bề mặt dựng bằng code
/// (card, settings, preview, acrylic panel) — vì UI được vẽ màu tường minh chứ không dựa theme mặc định.
/// </summary>
public static class ThemeService
{
    private static ISettings? _settings;
    private static readonly UISettings Ui = new();
    private static DispatcherQueue? _dispatcher;
    private static bool _lastSystemDark;

    /// <summary>Phát khi mode đổi — cửa sổ nghe để áp lại theme + dựng lại phần tô màu bằng code.</summary>
    public static event Action? Changed;

    public static void Init(ISettings settings)
    {
        _settings = settings;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _lastSystemDark = SystemIsDark();
        Ui.ColorValuesChanged += OnSystemColorsChanged; // cập nhật live khi Windows đổi light/dark
    }

    // Windows đổi light/dark lúc app đang chạy (chỉ áp khi đang ở "system"). ColorValuesChanged chạy
    // trên thread nền → marshal về UI thread; chỉ phát khi light/dark THỰC SỰ đảo (bỏ qua đổi accent…).
    private static void OnSystemColorsChanged(UISettings sender, object args)
    {
        if (Mode != "system") return;
        _dispatcher?.TryEnqueue(() =>
        {
            bool dark = SystemIsDark();
            if (dark == _lastSystemDark) return;
            _lastSystemDark = dark;
            Changed?.Invoke();
        });
    }

    /// <summary>"system" | "light" | "dark".</summary>
    public static string Mode
    {
        get => _settings?.Get("appearanceMode", "system") ?? "system";
        set
        {
            if (Mode == value) return;
            _settings?.Set("appearanceMode", value);
            Changed?.Invoke();
        }
    }

    /// <summary>ElementTheme để gán cho root FrameworkElement của cửa sổ.</summary>
    public static ElementTheme ElementTheme => Mode switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>Theme hiệu dụng có phải tối không (giải quyết "system" theo màu nền hệ thống).</summary>
    public static bool IsDark => Mode switch
    {
        "light" => false,
        "dark" => true,
        _ => SystemIsDark(),
    };

    private static bool SystemIsDark()
    {
        try
        {
            // Dùng Foreground (chữ) để bám "App mode" giống ElementTheme.Default: chữ SÁNG ⇒ theme TỐI.
            // (Background không đáng tin trong app desktop/unpackaged — hay trả về đen cố định.)
            var c = Ui.GetColorValue(UIColorType.Foreground);
            return (c.R + c.G + c.B) / 3 > 128;
        }
        catch { return true; }
    }

    /// <summary>Áp ElementTheme cho root cửa sổ (built-in control + ThemeResource tự đổi theo).</summary>
    public static void Apply(FrameworkElement? root)
    {
        if (root != null) root.RequestedTheme = ElementTheme;
    }

    // ---------- Bảng màu (palette) cho bề mặt tô bằng code ----------
    private static Color C(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
    private static Color Pick(Color dark, Color light) => IsDark ? dark : light;
    private static Brush B(Color c) => new SolidColorBrush(c);

    // Card
    public static Color CardContent => Pick(C(0xFF, 0x1C, 0x1C, 0x1E), C(0xFF, 0xFF, 0xFF, 0xFF));
    public static Color CardFooterUrl => Pick(C(0xFF, 0x16, 0x16, 0x18), C(0xFF, 0xEC, 0xEC, 0xEF));
    public static Color PrimaryText => Pick(C(0xFF, 0xEC, 0xEC, 0xEC), C(0xFF, 0x1C, 0x1C, 0x1E));
    public static Color SecondaryText => Pick(C(0xFF, 0xA0, 0xA0, 0xA0), C(0xFF, 0x6D, 0x6D, 0x72));
    public static Color FaintText => Pick(C(0x80, 0xFF, 0xFF, 0xFF), C(0x80, 0x00, 0x00, 0x00));
    public static Color Divider => Pick(C(0x1F, 0xFF, 0xFF, 0xFF), C(0x1F, 0x00, 0x00, 0x00));

    public static Brush CardContentBrush => B(CardContent);
    public static Brush CardFooterUrlBrush => B(CardFooterUrl);
    public static Brush PrimaryTextBrush => B(PrimaryText);
    public static Brush SecondaryTextBrush => B(SecondaryText);
    public static Brush FaintTextBrush => B(FaintText);
    public static Brush DividerBrush => B(Divider);

    // Toolbar / search (panel)
    public static Brush TabActiveBrush => B(Pick(C(0x40, 0xFF, 0xFF, 0xFF), C(0x22, 0x00, 0x00, 0x00)));
    public static Brush TabClearBrush => B(C(0x00, 0x00, 0x00, 0x00));
    public static Color ToolbarIcon => Pick(C(0xFF, 0xCC, 0xCC, 0xCC), C(0xFF, 0x3C, 0x3C, 0x43));
    public static Color SearchFieldBg => Pick(C(0x66, 0x20, 0x20, 0x24), C(0x80, 0xE5, 0xE5, 0xEA));
    public static Brush ToolbarIconBrush => B(ToolbarIcon);
    public static Brush SearchFieldBgBrush => B(SearchFieldBg);

    // Settings
    public static Brush SettingsCardBg => B(Pick(C(0x14, 0xFF, 0xFF, 0xFF), C(0x0A, 0x00, 0x00, 0x00)));
    public static Brush SettingsCardStroke => B(Pick(C(0x24, 0xFF, 0xFF, 0xFF), C(0x14, 0x00, 0x00, 0x00)));
    public static Brush SettingsDivider => B(Pick(C(0x18, 0xFF, 0xFF, 0xFF), C(0x0F, 0x00, 0x00, 0x00)));
    public static Brush SettingsSidebarBg => B(Pick(C(0x18, 0xFF, 0xFF, 0xFF), C(0x0A, 0x00, 0x00, 0x00)));
    public static Brush SettingsSidebarBorder => B(Pick(C(0x22, 0xFF, 0xFF, 0xFF), C(0x14, 0x00, 0x00, 0x00)));
    public static Brush AccentText => B(Pick(C(0xFF, 0x4C, 0xA0, 0xFF), C(0xFF, 0x0A, 0x84, 0xFF)));
    public static Color TitleButtonFg => Pick(C(0xFF, 0xFF, 0xFF, 0xFF), C(0xFF, 0x1C, 0x1C, 0x1E));

    // Preview
    public static Brush PreviewPanelBg => B(Pick(C(0xFF, 0x1C, 0x1C, 0x1E), C(0xFF, 0xF6, 0xF6, 0xF6)));
    public static Brush PreviewContentBg => B(Pick(C(0xFF, 0x16, 0x16, 0x18), C(0xFF, 0xFF, 0xFF, 0xFF)));
    public static Brush PreviewDivider => B(Pick(C(0x28, 0xFF, 0xFF, 0xFF), C(0x1E, 0x00, 0x00, 0x00)));

    // Acrylic panel backdrop
    public static Color AcrylicTint => Pick(C(0xFF, 0x14, 0x14, 0x16), C(0xFF, 0xF2, 0xF2, 0xF4));
    public static float AcrylicTintOpacity => IsDark ? 0.75f : 0.6f;
    public static float AcrylicLuminosityOpacity => IsDark ? 0.9f : 0.85f;
}
