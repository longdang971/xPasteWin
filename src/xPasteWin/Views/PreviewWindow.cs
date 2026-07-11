using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using xPasteWin.Interop;
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
        if (content is FrameworkElement fe) fe.RequestedTheme = xPasteWin.Services.ThemeService.ElementTheme;
        Content = content;

        // WebView2 không tự giải phóng khi Window đóng → Close() tường minh để thu hồi msedgewebview2.exe.
        Closed += (_, _) => { try { _webView?.Close(); } catch { } _webView = null; };
    }

    private IntPtr SubProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == Win32.WM_NCCALCSIZE && w != IntPtr.Zero) return IntPtr.Zero;
        return Win32.CallWindowProc(_origProc, h, m, w, l);
    }

    /// <summary>Dựng cây visual (không giành focus vì đã có NOACTIVATE), đặt vị trí và hiện.</summary>
    public void ShowAt(int x, int y, int w, int h)
    {
        Activate(); // realize visual tree; NOACTIVATE ngăn giành foreground
        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, x, y, w, h,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }
}
