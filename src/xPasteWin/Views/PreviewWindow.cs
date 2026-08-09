using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT;
using WinRT.Interop;
using xPasteWin.Interop;
using xPasteWin.Services;
using xPasteWin.ViewModels;

namespace xPasteWin.Views;

/// <summary>
/// Cửa sổ preview riêng (Quick Look) — nổi phía trên panel, topmost, KHÔNG giành focus
/// (giống ItemPreviewWindow của macOS). Dùng cửa sổ riêng vì Flyout bị giới hạn trong ranh
/// giới cửa sổ panel (chỉ cao ~320px) nên preview lớn sẽ bị cắt.
/// </summary>
public sealed class PreviewWindow : Window
{
    public IntPtr Hwnd { get; }
    private Win32.WndProcDelegate? _wndProc;
    private IntPtr _origProc;
    private Microsoft.UI.Xaml.Controls.WebView2? _webView;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;

    public PreviewWindow(CardViewModel card, PanelViewModel vm, Action onClose, Action onNavigateAway)
    {
        Hwnd = WindowNative.GetWindowHandle(this);

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false;
            p.SetBorderAndTitleBar(false, false);
        }

        var ex = Win32.GetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

        // Bỏ non-client border (vạch trắng) như panel.
        _wndProc = SubProc;
        _origProc = Win32.GetWindowLongPtr(Hwnd, Win32.GWLP_WNDPROC);
        Win32.SetWindowLongPtr(Hwnd, Win32.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));
        uint none = Win32.DWMWA_COLOR_NONE;
        Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_BORDER_COLOR, ref none, sizeof(uint));

        var content = PreviewFactory.Build(card, vm, onClose, onNavigateAway, out _webView);
        if (content is FrameworkElement fe) fe.RequestedTheme = ThemeService.ElementTheme;
        Content = content;

        // Nền kính acrylic (như vibrancy của popover macOS). Nếu máy không hỗ trợ → nền đặc dự phòng.
        if (!SetupAcrylic() && content is Microsoft.UI.Xaml.Controls.Border b)
            b.Background = ThemeService.PreviewPanelBg;

        // WebView2 không tự giải phóng khi Window đóng → Close() tường minh để thu hồi msedgewebview2.exe.
        Closed += (_, _) =>
        {
            try { _webView?.Close(); } catch { } _webView = null;
            try { _acrylic?.Dispose(); } catch { } _acrylic = null;
        };
    }

    private bool SetupAcrylic()
    {
        if (!DesktopAcrylicController.IsSupported()) return false;
        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
        _acrylic = new DesktopAcrylicController
        {
            TintColor = ThemeService.AcrylicTint,
            TintOpacity = ThemeService.AcrylicTintOpacity,
            LuminosityOpacity = ThemeService.AcrylicLuminosityOpacity,
        };
        _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
        return true;
    }

    private IntPtr SubProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == Win32.WM_NCCALCSIZE && w != IntPtr.Zero) return IntPtr.Zero;
        return Win32.CallWindowProc(_origProc, h, m, w, l);
    }

    /// <summary>Dựng cây visual (không giành focus vì đã có NOACTIVATE), đặt vị trí và hiện.
    /// <paramref name="beakH"/> &gt; 0 → thêm "mỏ" tam giác chỉ xuống, đỉnh ở giữa <paramref name="beakCenterX"/>
    /// (toạ độ px trong cửa sổ) — trỏ đúng vào card được preview, như popover macOS.</summary>
    public void ShowAt(int x, int y, int w, int h, int beakH = 0, int beakCenterX = 0)
    {
        Activate(); // realize visual tree; NOACTIVATE ngăn giành foreground
        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, x, y, w, h,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

        double scale = Win32.ScaleForMonitor(Win32.MonitorFromWindow(Hwnd, Win32.MONITOR_DEFAULTTONEAREST));
        int d = (int)Math.Round(14 * scale) * 2; // đường kính bo góc thân (khớp Border 14)
        int bodyH = h - beakH;
        var body = Win32.CreateRoundRectRgn(0, 0, w + 1, bodyH + 1, d, d);

        if (beakH > 0)
        {
            int half = (int)Math.Round(beakH * 1.3);            // nửa bề rộng chân mỏ
            int c = Math.Clamp(beakCenterX, d / 2 + half, w - d / 2 - half);
            var pts = new[]
            {
                new Win32.POINT { X = c - half, Y = bodyH - 1 },
                new Win32.POINT { X = c + half, Y = bodyH - 1 },
                new Win32.POINT { X = c,        Y = bodyH - 1 + beakH },
            };
            var beak = Win32.CreatePolygonRgn(pts, 3, 1);
            var combined = Win32.CreateRectRgn(0, 0, 0, 0);
            Win32.CombineRgn(combined, body, beak, Win32.RGN_OR);
            Win32.SetWindowRgn(Hwnd, combined, true); // hệ thống tiếp quản 'combined'
            Win32.DeleteObject(body);
            Win32.DeleteObject(beak);
        }
        else
        {
            Win32.SetWindowRgn(Hwnd, body, true);
        }
    }
}
