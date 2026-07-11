using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinRT;
using WinRT.Interop;
using xPasteWin.Interop;
using xPasteWin.Models;
using xPasteWin.Services;
using xPasteWin.ViewModels;

namespace xPasteWin.Views;

public sealed partial class PanelWindow : Window
{
    private readonly ISettings _settings;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;
    private readonly IntPtr _hwnd;
    private PanelViewModel? _vm;
    private readonly GlobalHooks _hooks = new();
    private string _query = "";
    // Search đang mở NHƯNG người dùng đã click chọn một kết quả → "focus" phím tắt ở card (Space
    // preview, mũi tên…) thay vì gõ vào ô search. Ô search vẫn hiện; click lại vào ô search để gõ tiếp.
    private bool _browsingResults;

    // Panel responsive: nội dung dàn trang ở kích thước thiết kế gốc (macOS) rồi thu nhỏ đồng đều
    // qua ScaleTransform để chiều dày chỉ chiếm ~PanelScreenRatio màn hình ở MỌI độ phân giải/scale,
    // giữ giao diện toàn vẹn (không méo, không cắt).
    private const double PanelDesignThickness = 320; // chiều dày thiết kế (điểm logic, giống macOS)
    private const double PanelScreenRatio = 0.28;    // panel chiếm ~28% cạnh ngắn màn hình
    private const double PanelMinThickness = 200;    // sàn chiều dày để card không quá nhỏ khi màn hẹp
    private double _panelScaleFactor = 1;            // f = chiềuDàyThực / thiếtKế, áp cho ContentHost
    private double _panelLongLogical;                // chiều dài panel (full W hoặc full H) theo điểm logic
    private bool _panelHorizontal = true;            // bottom/top = ngang; left/right = dọc

    public ListView Cards => CardList;

    public bool IsPanelVisible { get; private set; }
    public event Action? Opened;
    public event Action? Hidden;
    /// <summary>Yêu cầu ẩn panel (Escape).</summary>
    public event Action? CloseRequested;
    /// <summary>Menu "…": mở Settings / Quit.</summary>
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public PanelWindow(ISettings settings)
    {
        _settings = settings;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        ApplyPresenter();
        SetupAcrylic();
        ApplyTheme();
        ThemeService.Changed += OnThemeChanged; // cập nhật live khi đổi theme (Settings hoặc Windows)

        // Ẩn khỏi taskbar + không giành focus (giống nonactivatingPanel macOS).
        SetExStyle(activatable: false);

        DisableDwmBorder();
        RemoveNonClientBorder();
    }

    private void DisableDwmBorder()
    {
        uint none = Win32.DWMWA_COLOR_NONE;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_BORDER_COLOR, ref none, sizeof(uint));
    }

    private void ApplyScreenShareAffinity(IntPtr hwnd) =>
        Win32.SetWindowDisplayAffinity(hwnd,
            _settings.Get("showDuringScreenSharing", true) ? Win32.WDA_NONE : Win32.WDA_EXCLUDEFROMCAPTURE);

    /// <summary>Cấu hình hướng danh sách + scroll + độ rộng search theo vị trí panel:
    /// bottom/top → ngang; left/right → dọc (panel hẹp nên search cũng thu lại).</summary>
    private void ConfigureLayout()
    {
        var pos = _settings.Get("panelPosition", "bottom");
        bool horizontal = pos is "bottom" or "top";

        // Lần mở panel ĐẦU TIÊN, ListView chưa chạy layout pass nên ItemsPanelRoot còn null → thử lại
        // qua vài vòng dispatcher (bounded để không lặp vô hạn khi danh sách rỗng).
        void ApplyOrientation(int attempts)
        {
            if (CardList.ItemsPanelRoot is ItemsStackPanel isp)
                isp.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
            else if (attempts > 0)
                DispatcherQueue.TryEnqueue(() => ApplyOrientation(attempts - 1));
        }
        ApplyOrientation(5);

        ScrollViewer.SetHorizontalScrollMode(CardList, horizontal ? ScrollMode.Enabled : ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollMode(CardList, horizontal ? ScrollMode.Disabled : ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(CardList, ScrollBarVisibility.Hidden);
        ScrollViewer.SetVerticalScrollBarVisibility(CardList, ScrollBarVisibility.Hidden);

        SearchBar.Width = horizontal ? 460 : 280;
        SearchScale.CenterX = SearchBar.Width / 2;

        // Thu nhỏ đồng đều toàn bộ nội dung: dàn ở kích thước thiết kế rồi scale f.
        // Chiều dày = PanelDesignThickness → sau scale thành chiều dày thực (~% màn hình).
        // Chiều dài = chiềuDài / f → sau scale thành full W/H (giữ nguyên độ dài panel).
        double f = _panelScaleFactor;
        PanelScale.ScaleX = f;
        PanelScale.ScaleY = f;
        if (_panelHorizontal)
        {
            ContentHost.Width = _panelLongLogical / f;
            ContentHost.Height = PanelDesignThickness;
        }
        else
        {
            ContentHost.Width = PanelDesignThickness;
            ContentHost.Height = _panelLongLogical / f;
        }
    }

    // Subclass window proc để bỏ hoàn toàn vùng non-client (xoá vạch trắng 1px ở mép trên của
    // cửa sổ WinUI borderless). WM_NCCALCSIZE trả 0 => client area = toàn bộ cửa sổ.
    private Win32.WndProcDelegate? _wndProc;
    private IntPtr _origWndProc;
    private void RemoveNonClientBorder()
    {
        _wndProc = SubclassProc;
        _origWndProc = Win32.GetWindowLongPtr(_hwnd, Win32.GWLP_WNDPROC);
        Win32.SetWindowLongPtr(_hwnd, Win32.GWLP_WNDPROC,
            System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProc));
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED);
    }

    private IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_NCCALCSIZE && wParam != IntPtr.Zero)
            return IntPtr.Zero; // không dành pixel nào cho khung/viền
        return Win32.CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>Gắn ViewModel + nối toàn bộ tương tác (toolbar, phím tắt, chọn, context menu).</summary>
    public void Bind(PanelViewModel vm)
    {
        _vm = vm;
        CardList.ItemsSource = vm.Cards;

        // Item đang preview bị xoá/lọc mất khỏi danh sách → đóng preview "ma" (kiểm sau khi Refresh xong).
        vm.Cards.CollectionChanged += (_, _) =>
        {
            if (!PreviewOpen) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!PreviewOpen) return;
                foreach (var c in vm.Cards) if (c.Id == _previewItemId) return;
                HidePreview();
            });
        };

        SearchToggleButton.Click += (_, _) => OpenSearch(true);
        SearchBox.TextChanged += (_, _) =>
        {
            vm.SearchQuery = SearchBox.Text;
            UpdateEmptyState();
        };
        // Click lại vào ô search (được focus) → quay về chế độ gõ (phím ký tự vào ô tìm kiếm).
        SearchBox.GotFocus += (_, _) => _browsingResults = false;

        TabAllButton.Click += (_, _) => SetTab(ClipboardTab.All);
        TabPinButton.Click += (_, _) => SetTab(ClipboardTab.Pinned);
        TabAllCompact.Click += (_, _) => SetTab(ClipboardTab.All);
        TabPinCompact.Click += (_, _) => SetTab(ClipboardTab.Pinned);
        MoreButton.Click += (_, _) => ShowMoreMenu();

        CardList.Tapped += OnTapped;
        CardList.DoubleTapped += OnDoubleTapped;
        CardList.RightTapped += OnRightTapped;
        RootGrid.PointerPressed += OnBackgroundPressed;

        // Panel KHÔNG giành focus (NOACTIVATE) → app đích luôn giữ foreground để dán chắc chắn.
        // Phím điều hướng/search + click-ra-ngoài đi qua hook toàn cục (giống macOS nonactivating panel).
        _hooks.KeyDown += OnHookKey;
        _hooks.MouseDown += OnHookMouse;

        UpdateTabVisual();
    }

    private bool _suppressHide;

    // ---------- Hook chuột: click ra ngoài panel/preview → đóng ----------
    private static bool InRect(IntPtr hwnd, int x, int y) =>
        Win32.GetWindowRect(hwnd, out var r) && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    private void OnHookMouse(int x, int y)
    {
        // Menu/dialog đang mở (_suppressHide) → KHÔNG ẩn gì (kể cả preview), tránh đóng panel giữa chừng.
        if (!IsPanelVisible || _suppressHide) return;
        bool insidePanel = InRect(_hwnd, x, y);
        if (PreviewOpen)
        {
            if (InRect(_previewHwnd, x, y)) return;      // click trong preview → giữ nguyên
            DispatcherQueue.TryEnqueue(HidePreview);       // click ngoài preview → đóng preview
            if (!insidePanel)
                DispatcherQueue.TryEnqueue(() => { if (IsPanelVisible) HidePanel(); });
            return;
        }
        if (!insidePanel)
            DispatcherQueue.TryEnqueue(() => { if (IsPanelVisible && !_suppressHide) HidePanel(); });
    }

    // ---------- Toolbar ----------
    private void SetTab(ClipboardTab tab)
    {
        if (_vm == null) return;
        _vm.ActiveTab = tab;
        UpdateTabVisual();
        UpdateEmptyState();
    }

    private void UpdateTabVisual()
    {
        if (_vm == null) return;
        bool all = _vm.ActiveTab == ClipboardTab.All;
        var active = ThemeService.TabActiveBrush; // phủ trắng ở dark / phủ đen ở light → luôn thấy tab chọn
        var clear = ThemeService.TabClearBrush;
        TabAllButton.Background = all ? active : clear;
        TabPinButton.Background = all ? clear : active;
    }

    private void OpenSearch(bool open)
    {
        if (_vm == null) return;
        _vm.IsSearchOpen = open;
        _browsingResults = false;
        if (open)
        {
            _query = "";
            SearchBox.Text = "";
            NormalBar.Opacity = 0;
            NormalBar.Visibility = Visibility.Collapsed;
            MoreButton.Visibility = Visibility.Collapsed;
            SearchBar.Visibility = Visibility.Visible;
            AnimateSearch(true);
            // Focus ô nhập để hiện con trỏ nhấp nháy ngay lần đầu mở (panel NOACTIVATE nên caret
            // không tự xuất hiện). Enqueue để chạy sau khi SearchBar đã hiện.
            DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));
        }
        else
        {
            _query = "";
            SearchBox.Text = "";
            AnimateSearch(false);
            SearchBar.Visibility = Visibility.Collapsed;
            NormalBar.Visibility = Visibility.Visible;
            NormalBar.Opacity = 1;
            MoreButton.Visibility = Visibility.Visible;
        }
    }

    private void AnimateSearch(bool show)
    {
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var op = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EnableDependentAnimation = true,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(op, SearchBar);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(op, "Opacity");
        var sc = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = show ? 1 : 0.5,
            Duration = TimeSpan.FromMilliseconds(180),
            EnableDependentAnimation = true,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(sc, SearchScale);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(sc, "ScaleX");
        sb.Children.Add(op);
        sb.Children.Add(sc);
        sb.Begin();
    }

    private void ShowMoreMenu()
    {
        var menu = new MenuFlyout();
        var clear = new MenuFlyoutItem { Text = "Clear History", Icon = new SymbolIcon(Symbol.Delete) };
        clear.Click += async (_, _) => await ClearHistoryConfirmAsync();
        menu.Items.Add(clear);
        menu.Items.Add(new MenuFlyoutSeparator());
        var settings = new MenuFlyoutItem { Text = "Settings…", Icon = new SymbolIcon(Symbol.Setting) };
        settings.Click += (_, _) => { HidePanel(); SettingsRequested?.Invoke(); };
        menu.Items.Add(settings);
        var quit = new MenuFlyoutItem { Text = "Quit xPaste" };
        quit.Click += (_, _) => QuitRequested?.Invoke();
        menu.Items.Add(quit);

        _suppressHide = true;
        menu.Closed += (_, _) => _suppressHide = false;
        menu.ShowAt(MoreButton);
    }

    // ---------- Dialog xác nhận lái bằng bàn phím (panel non-activating) ----------
    private ContentDialog? _confirmDialog;
    private bool _confirmPrimaryFocused;
    private bool _confirmResultPrimary;

    /// <summary>Hiện dialog xác nhận trong panel KHÔNG giữ focus: bàn phím lái qua hook toàn cục
    /// (←/→ đổi nút, Enter chọn nút đang sáng, Esc huỷ) thay vì lọt xuống điều hướng clipboard.
    /// Mặc định sáng nút chính để Enter xác nhận ngay. Trả true nếu chọn nút chính.</summary>
    private async System.Threading.Tasks.Task<bool> ShowConfirmAsync(ContentDialog dlg)
    {
        dlg.DefaultButton = ContentDialogButton.Primary;
        _confirmPrimaryFocused = true;
        _confirmResultPrimary = false;
        _confirmDialog = dlg;
        _suppressHide = true;
        ContentDialogResult res;
        try { res = await dlg.ShowAsync(); }
        finally { _confirmDialog = null; _suppressHide = false; }
        return res == ContentDialogResult.Primary || _confirmResultPrimary;
    }

    private async System.Threading.Tasks.Task ClearHistoryConfirmAsync()
    {
        var dlg = new ContentDialog
        {
            Title = "Clear history",
            Content = "Delete all unpinned clipboard history?",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = ThemeService.ElementTheme,
        };
        if (await ShowConfirmAsync(dlg)) { _vm?.ClearHistory(); UpdateEmptyState(); }
    }

    private void UpdateEmptyState()
    {
        bool empty = (_vm?.Cards.Count ?? 0) == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty && _vm != null)
            EmptyState.Text = !string.IsNullOrEmpty(_vm.SearchQuery) ? "No results"
                : _vm.ActiveTab == ClipboardTab.Pinned ? "No pinned items"
                : "Nothing copied yet";
    }

    // ---------- Selection / click ----------
    private static bool IsCtrlDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static CardViewModel? CardFrom(object source) =>
        (source as FrameworkElement)?.DataContext as CardViewModel;

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_vm == null) return;
        var card = CardFrom(e.OriginalSource);
        if (card == null) { _vm.ClearSelection(); return; }
        if (IsCtrlDown()) _vm.ToggleSelect(card.Id);
        else _vm.SelectSingle(card.Id);
        // Chọn kết quả khi đang search → GIỮ nguyên ô search, chỉ chuyển "focus" phím tắt sang card
        // (Space preview, mũi tên, Enter…). Focus CardList để rời con trỏ khỏi ô search.
        if (_vm.IsSearchOpen)
        {
            _browsingResults = true;
            CardList.Focus(FocusState.Programmatic);
        }
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var card = CardFrom(e.OriginalSource);
        if (_vm != null && card != null) _vm.Paste(card.Item);
    }

    private void OnBackgroundPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_vm == null) return;
        bool onCard = CardFrom(e.OriginalSource) != null;
        // Search đang mở + click ra ngoài thanh search (vùng trống/toolbar, không phải card) → đóng search.
        // Giữ search mở khi click vào card để còn dán từ kết quả đã lọc.
        if (_vm.IsSearchOpen && !onCard && !IsWithinSearchBar(e.OriginalSource))
            OpenSearch(false);
        if (!onCard) _vm.ClearSelection();
    }

    private bool IsWithinSearchBar(object source)
    {
        var el = source as DependencyObject;
        while (el != null)
        {
            if (ReferenceEquals(el, SearchBar)) return true;
            el = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(el);
        }
        return false;
    }

    // ---------- Context menu ----------
    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var el = e.OriginalSource as FrameworkElement;
        var card = el?.DataContext as CardViewModel;
        if (_vm == null || card == null) return;
        if (!_vm.Selection.IsSelected(card.Id)) _vm.SelectSingle(card.Id);

        var menu = new MenuFlyout();
        void Add(string text, Action act) { var mi = new MenuFlyoutItem { Text = text }; mi.Click += (_, _) => act(); menu.Items.Add(mi); }

        var suffix = string.IsNullOrEmpty(_vm.TargetAppName) ? "" : $" to {_vm.TargetAppName}";
        Add($"Paste{suffix}", () => _vm.Paste(card.Item));
        if (card.Item.Text != null) Add("Paste as Plain Text", () => _vm.PastePlain(card.Item));
        Add("Copy", () => _vm.Copy(card.Item));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (PanelViewModel.CanOpenUrl(card.Item)) Add("Open URL", () => _vm.OpenUrl(card.Item));
        Add("Delete", () => _vm.Delete(card.Item));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(card.Item.IsPinned ? "Unpin" : "Pin", () => { _vm.TogglePin(card.Item); UpdateEmptyState(); });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Preview", () => ShowPreview(card));

        _suppressHide = true;
        menu.Closed += (_, _) => _suppressHide = false;
        menu.ShowAt(el, e.GetPosition(el));
    }

    // ---------- Hook bàn phím (panel không có focus) ----------
    private const int VK_BACK = 0x08, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11,
                      VK_MENU = 0x12, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_LEFT = 0x25,
                      VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_DELETE = 0x2E,
                      VK_A = 0x41, VK_C = 0x43, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
    private void Enq(Action a) => DispatcherQueue.TryEnqueue(() => a());

    /// <summary>Xử lý phím từ hook. Trả true để "nuốt" phím (không lọt vào app đích).</summary>
    private bool OnHookKey(int vk)
    {
        if (_vm == null || !IsPanelVisible) return false;

        // Dialog xác nhận đang mở: panel KHÔNG giữ focus nên phải lái dialog bằng hook —
        // ←/→ chuyển giữa 2 nút, Enter chọn nút đang sáng, Esc huỷ. Nuốt MỌI phím còn lại để
        // không điều hướng/đổi selection clipboard phía sau (nguyên nhân xoá nhầm item).
        if (_confirmDialog is { } cd)
        {
            switch (vk)
            {
                case VK_LEFT: case VK_RIGHT:
                    Enq(() =>
                    {
                        _confirmPrimaryFocused = !_confirmPrimaryFocused;
                        cd.DefaultButton = _confirmPrimaryFocused
                            ? ContentDialogButton.Primary : ContentDialogButton.Close;
                    });
                    return true;
                case VK_RETURN:
                    Enq(() => { _confirmResultPrimary = _confirmPrimaryFocused; cd.Hide(); });
                    return true;
                case VK_ESCAPE:
                    Enq(() => { _confirmResultPrimary = false; cd.Hide(); });
                    return true;
                default:
                    return true;
            }
        }

        bool ctrl = Down(VK_CONTROL), shift = Down(VK_SHIFT);

        // Đang GÕ search (search mở & chưa chọn kết quả): phím ký tự vào ô tìm kiếm.
        if (_vm.IsSearchOpen && !_browsingResults)
        {
            switch (vk)
            {
                case VK_ESCAPE: Enq(() => OpenSearch(false)); return true;
                case VK_RETURN: Enq(() => PastePrimary(shift)); return true;
                case VK_DOWN: case VK_RIGHT: Enq(() => ScrollTo(_vm.MoveSelection(+1))); return true;
                case VK_UP: case VK_LEFT: Enq(() => ScrollTo(_vm.MoveSelection(-1))); return true;
                case VK_BACK: Enq(SearchBackspace); return true;
                default:
                    // Tổ hợp có Ctrl/Alt/Win → để lọt (vd hotkey đóng panel). Ngược lại NUỐT mọi phím
                    // trần (gõ vào search nếu in được) → KHÔNG rơi dấu câu/ký tự xuống app đích.
                    if (ctrl || Down(VK_MENU) || Down(VK_LWIN) || Down(VK_RWIN)) return false;
                    char c = CharFromKey(vk, shift);
                    if (c != '\0') Enq(() => AppendSearch(c));
                    return true;
            }
        }

        switch (vk)
        {
            case VK_LEFT: case VK_UP: Enq(() => ScrollTo(_vm.MoveSelection(-1))); return true;
            case VK_RIGHT: case VK_DOWN: Enq(() => ScrollTo(_vm.MoveSelection(+1))); return true;
            case VK_A when ctrl: Enq(() => _vm.SelectAll()); return true;
            case VK_C when ctrl: Enq(CopyPrimary); return true;
            case VK_RETURN: Enq(() => PastePrimary(shift)); return true;
            case VK_SPACE: Enq(PreviewPrimary); return true;
            // Đang browse (search mở): Backspace = SỬA truy vấn (quay lại gõ), KHÔNG xoá item.
            case VK_BACK when _vm.IsSearchOpen:
                Enq(() => { _browsingResults = false; SearchBox.Focus(FocusState.Programmatic); SearchBackspace(); });
                return true;
            case VK_DELETE: case VK_BACK: Enq(() => _ = DeleteWithConfirmAsync()); return true;
            case VK_ESCAPE:
                // Đóng theo lớp: preview → search → panel.
                if (PreviewOpen) Enq(HidePreview);
                else if (_vm.IsSearchOpen) Enq(() => OpenSearch(false));
                else Enq(() => CloseRequested?.Invoke());
                return true;
            default:
                // nuốt phím in được trần (kể cả dấu câu) để không lọt vào app đích khi panel đang mở
                if (!ctrl && !Down(VK_MENU) && !Down(VK_LWIN) && !Down(VK_RWIN)
                    && CharFromKey(vk, shift) != '\0') return true;
                return false;
        }
    }

    private void PastePrimary(bool plain)
    {
        var p = _vm?.PrimaryCard();
        if (p == null) return;
        if (plain) _vm!.PastePlain(p.Item); else _vm!.Paste(p.Item);
    }

    private void CopyPrimary() { var p = _vm?.PrimaryCard(); if (p != null) _vm!.Copy(p.Item); }
    private void PreviewPrimary() { var p = _vm?.PrimaryCard(); if (p != null) TogglePreview(p); }
    private void AppendSearch(char c) { _query += c; SearchBox.Text = _query; }
    private void SearchBackspace() { if (_query.Length > 0) { _query = _query[..^1]; SearchBox.Text = _query; } }

    /// <summary>VK → ký tự thực theo layout hiện tại (kể cả dấu câu/ký hiệu, tôn trọng Shift/CapsLock).
    /// Thay cách map thủ công cũ (chỉ A-Z/0-9/space) vốn làm dấu câu lọt xuống app đích + không gõ được
    /// URL/email/đường dẫn vào ô search. '\0' nếu không phải ký tự in được.</summary>
    private static char CharFromKey(int vk, bool shift)
    {
        var state = new byte[256];
        if (shift) state[0x10] = 0x80;                                  // VK_SHIFT nhấn
        if ((Win32.GetAsyncKeyState(0x14) & 0x0001) != 0) state[0x14] = 0x01; // CapsLock bật
        uint scan = Win32.MapVirtualKey((uint)vk, 0);                   // MAPVK_VK_TO_VSC
        var sb = new System.Text.StringBuilder(4);
        int n = Win32.ToUnicode((uint)vk, scan, state, sb, sb.Capacity, 0);
        return (n == 1 && !char.IsControl(sb[0])) ? sb[0] : '\0';
    }

    private async System.Threading.Tasks.Task DeleteWithConfirmAsync()
    {
        if (_vm == null || _vm.Selection.Count == 0) return;
        if (_vm.NeedsDeleteConfirm)
        {
            var dlg = new ContentDialog
            {
                Title = "Delete items",
                Content = $"Delete {_vm.Selection.Count} selected items?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = ThemeService.ElementTheme,
            };
            if (!await ShowConfirmAsync(dlg)) return;
        }
        _vm.DeleteSelected();
        UpdateEmptyState();
    }

    private void ScrollTo(Guid? id)
    {
        if (id is { } g && _vm?.Card(g) is { } card)
            CardList.ScrollIntoView(card, ScrollIntoViewAlignment.Default);
    }

    // ---------- Preview (Quick Look): cửa sổ riêng nổi trên panel ----------
    private PreviewWindow? _previewWindow;
    private IntPtr _previewHwnd;
    private Guid _previewItemId;

    private bool PreviewOpen => _previewWindow != null;

    private void TogglePreview(CardViewModel card)
    {
        if (PreviewOpen) { HidePreview(); return; }
        ShowPreview(card);
    }

    private void ShowPreview(CardViewModel card)
    {
        HidePreview();
        bool isUrl = card.Item.Type == ClipboardContentType.Url;
        double scale = Win32.ScaleForMonitor(Win32.MonitorFromWindow(_hwnd, Win32.MONITOR_DEFAULTTONEAREST));
        int w = (int)Math.Round((isUrl ? 560 : 420) * scale);
        int h = (int)Math.Round((isUrl ? 440 : 340) * scale);

        // onClose (nút ✕) chỉ đóng preview; onNavigateAway (Open in Browser) đóng cả panel
        // (HidePanel tự gọi HidePreview bên trong).
        _previewWindow = new PreviewWindow(card, _vm!, HidePreview, HidePanel);
        _previewHwnd = _previewWindow.Hwnd;
        _previewItemId = card.Id;

        // Đặt giữa theo chiều ngang panel, nổi ngay phía trên mép panel.
        Win32.GetWindowRect(_hwnd, out var pr);
        int cx = pr.Left + ((pr.Right - pr.Left) - w) / 2;
        int cy = pr.Top - h - (int)Math.Round(12 * scale);
        if (cy < 0) cy = 0;
        _previewWindow.ShowAt(cx, cy, w, h);
        ApplyScreenShareAffinity(_previewHwnd); // preview cũng ẩn/hiện khi screen share
    }

    private void HidePreview()
    {
        if (_previewWindow == null) return;
        var win = _previewWindow;
        _previewWindow = null;
        _previewHwnd = IntPtr.Zero;
        try { win.Close(); } catch { }
    }

    // ---------- Win32 style / hiển thị ----------
    private void SetExStyle(bool activatable)
    {
        var ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex |= Win32.WS_EX_TOOLWINDOW;
        if (activatable) ex &= ~(long)Win32.WS_EX_NOACTIVATE;
        else ex |= Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
    }

    private void ApplyPresenter()
    {
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.SetBorderAndTitleBar(false, false);
        }
    }

    private void SetupAcrylic()
    {
        if (!DesktopAcrylicController.IsSupported()) return;
        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
        _acrylic = new DesktopAcrylicController();
        ApplyAcrylicTheme();
        _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
    }

    private void ApplyAcrylicTheme()
    {
        if (_acrylic == null) return;
        _acrylic.TintColor = ThemeService.AcrylicTint;
        _acrylic.TintOpacity = ThemeService.AcrylicTintOpacity;
        _acrylic.LuminosityOpacity = ThemeService.AcrylicLuminosityOpacity;
    }

    /// <summary>Áp theme cho panel: RequestedTheme làm toolbar/ThemeResource + card tự đổi màu; cập nhật acrylic.</summary>
    public void ApplyTheme()
    {
        RootGrid.RequestedTheme = ThemeService.ElementTheme;
        ApplyAcrylicTheme();
    }

    // Changed luôn phát trên UI thread (ThemeService marshal). Áp lại theme + dựng lại card (brush mới)
    // nếu panel đang mở.
    private void OnThemeChanged()
    {
        ApplyTheme();
        UpdateTabVisual();               // highlight tab theo theme mới
        if (IsPanelVisible) _vm?.Refresh();
    }

    public void HideImmediately()
    {
        var (x, y, w, h) = TargetRect();
        var (ox, oy) = OffscreenOrigin(x, y, w, h);
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, ox, oy, w, h, Win32.SWP_HIDEWINDOW);
        IsPanelVisible = false;
        _hooks.Uninstall();
    }

    public void ShowPanel()
    {
        var (x, y, w, h) = TargetRect();
        var (sx, sy) = OffscreenOrigin(x, y, w, h);
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, sx, sy, w, h,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        IsPanelVisible = true;

        // Bật hook toàn cục để nhận phím + click ngoài mà KHÔNG giành focus của app đích.
        _hooks.Install();
        DisableDwmBorder(); // áp lại mỗi lần hiện (phòng khi hệ thống vẽ lại viền)
        ApplyTheme();       // cập nhật theme (phòng khi đổi trong Settings lúc panel ẩn)
        ApplyScreenShareAffinity(_hwnd); // ẩn/hiện panel khi screen share (setting Privacy)
        ConfigureLayout(); // hướng list + search theo vị trí panel (ngang/dọc)

        // Auto-select item đầu để bàn phím dùng được ngay (giống panelDidOpen macOS).
        if (_vm != null && _vm.Cards.Count > 0)
        {
            _vm.SelectSingle(_vm.Cards[0].Id);
            DispatcherQueue.TryEnqueue(() => ScrollTo(_vm.Cards[0].Id));
        }
        UpdateEmptyState();

        Opened?.Invoke();
        Slide(sx, sy, x, y, w, h, durationMs: 110, easeOut: true, null);
    }

    public void HidePanel()
    {
        if (!IsPanelVisible) return;
        IsPanelVisible = false;
        _hooks.Uninstall();
        if (_vm != null && _vm.IsSearchOpen) OpenSearch(false);
        HidePreview();
        var (x, y, w, h) = TargetRect();
        var (ex, ey) = OffscreenOrigin(x, y, w, h);
        Slide(x, y, ex, ey, w, h, durationMs: 100, easeOut: false, () =>
        {
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, ex, ey, w, h, Win32.SWP_HIDEWINDOW);
            Hidden?.Invoke();
        });
    }

    private (int x, int y, int w, int h) TargetRect()
    {
        Win32.GetCursorPos(out var pt);
        var mon = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(mon, ref mi);
        var work = mi.rcWork;
        int fullW = work.Right - work.Left, fullH = work.Bottom - work.Top;
        // SetWindowPos dùng pixel VẬT LÝ, còn XAML layout theo pixel LOGIC. DPI phải lấy theo MÀN HÌNH
        // ĐÍCH (nơi con trỏ), không phải màn hình cửa sổ đang nằm — nếu không panel sai kích thước khi
        // hai màn khác DPI.
        double scale = Win32.ScaleForMonitor(mon);

        var pos = _settings.Get("panelPosition", "bottom");
        _panelHorizontal = pos is "bottom" or "top";
        // Chiều dày panel = % cạnh ngắn màn hình (điểm logic), kẹp trong [min, thiết kế] để không
        // chiếm quá nhiều mà card cũng không nhỏ quá. Nội dung scale theo f = dày/thiếtKế nên luôn toàn vẹn.
        double screenShortLogical = (_panelHorizontal ? fullH : fullW) / scale;
        double thickLogical = Math.Clamp(PanelScreenRatio * screenShortLogical, PanelMinThickness, PanelDesignThickness);
        _panelScaleFactor = thickLogical / PanelDesignThickness;
        _panelLongLogical = (_panelHorizontal ? fullW : fullH) / scale;
        int thick = (int)Math.Round(thickLogical * scale);
        return pos switch
        {
            "top"   => (work.Left, work.Top, fullW, thick),
            "left"  => (work.Left, work.Top, thick, fullH),
            "right" => (work.Right - thick, work.Top, thick, fullH),
            _       => (work.Left, work.Bottom - thick, fullW, thick),
        };
    }

    private (int x, int y) OffscreenOrigin(int x, int y, int w, int h) =>
        _settings.Get("panelPosition", "bottom") switch
        {
            "top"   => (x, y - h),
            "left"  => (x - w, y),
            "right" => (x + w, y),
            _       => (x, y + h),
        };

    private void Slide(int fromX, int fromY, int toX, int toY, int w, int h,
                       int durationMs, bool easeOut, Action? done)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1000.0 / 120);
        var start = DateTime.UtcNow;
        timer.Tick += (_, _) =>
        {
            double raw = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / durationMs);
            double e = easeOut ? 1 - (1 - raw) * (1 - raw) : raw * raw;
            int cx = (int)(fromX + (toX - fromX) * e);
            int cy = (int)(fromY + (toY - fromY) * e);
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, cx, cy, w, h, Win32.SWP_NOACTIVATE);
            if (raw >= 1) { timer.Stop(); done?.Invoke(); }
        };
        timer.Start();
    }
}
