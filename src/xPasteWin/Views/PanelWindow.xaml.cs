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
using Microsoft.UI.Xaml.Media;
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

    // Panel "nổi" tách khỏi mép màn hình rồi bo góc — giống xPaste macOS (screenInset=8, cornerRadius=20).
    private const double PanelScreenInset = 8;       // khoảng cách panel với mép màn hình (điểm logic)
    private const double PanelCornerRadius = 20;     // bán kính bo góc panel (điểm logic)
    private int _panelGap;                            // inset thực (px vật lý) — để tính vị trí offscreen
    private double _monitorScale = 1;                 // scale màn hình đích — để đổi bán kính bo góc ra px vật lý

    public ListView Cards => CardList;

    public bool IsPanelVisible { get; private set; }
    public event Action? Opened;
    public event Action? Hidden;
    /// <summary>Yêu cầu ẩn panel (Escape).</summary>
    public event Action? CloseRequested;
    /// <summary>Menu "…": mở Settings / Quit / Check for Updates.</summary>
    public event Action? SettingsRequested;
    public event Action? QuitRequested;
    public event Action? UpdateRequested;
    /// <summary>Máy sắp NGỦ hoặc phiên sắp KẾT THÚC (đăng xuất/tắt/khởi động lại) → App xoá lịch sử nếu bật.
    /// Phát ĐỒNG BỘ trên UI thread từ window proc để kịp xoá trước khi tiến trình bị kết thúc.</summary>
    public event Action? SystemEnding;

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

        // Xoá lịch sử SỚM khi PHIÊN KẾT THÚC (đăng xuất/tắt/khởi động lại). Panel là cửa sổ top-level
        // (dù đang ẩn) nên NHẬN được WM_ENDSESSION — khác MessageWindow (message-only, HWND_MESSAGE)
        // vốn KHÔNG nhận. Đây là lớp "best-effort" (xoá đĩa ngay lúc đăng xuất nếu kịp); dù message này
        // không tới/không kịp thì App vẫn xoá sạch ở LẦN KHỞI ĐỘNG kế tiếp (xem App.OnLaunched) nên
        // sau khi đăng nhập lại lịch sử chắc chắn trống.
        if (msg == Win32.WM_ENDSESSION && wParam != IntPtr.Zero)
            SystemEnding?.Invoke();

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
        FilterButton.Tapped += (_, e) => { e.Handled = true; ShowFilterPopover(); };
        // Filter có thể bị xoá từ nơi khác (nút Clear, Backspace, đóng search) → hàng token và nút
        // filter bám theo một chỗ duy nhất là sự kiện này.
        vm.Filters.Changed += RefreshFilterUi;
        SearchBox.TextChanged += (_, _) =>
        {
            vm.SearchQuery = SearchBox.Text;
            UpdateCaret();
            UpdateEmptyState();
        };
        UpdateCaret();
        // Click lại vào ô search (được focus) → quay về chế độ gõ (phím ký tự vào ô tìm kiếm).
        SearchBox.GotFocus += (_, _) => { _browsingResults = false; _searchFocused = true; ApplySearchFieldTheme(); };
        SearchBox.LostFocus += (_, _) => { _searchFocused = false; ApplySearchFieldTheme(); };
        // Thumb tab bám đúng segment khi bố cục có kích thước / đổi kích thước.
        TabRow.SizeChanged += (_, _) => PositionThumbs(false);

        TabAllButton.Tapped += (_, _) => SetTab(ClipboardTab.All);
        TabPinButton.Tapped += (_, _) => SetTab(ClipboardTab.Pinned);
        TabAllCompact.Tapped += (_, _) => SetTab(ClipboardTab.All);
        TabPinCompact.Tapped += (_, _) => SetTab(ClipboardTab.Pinned);
        MoreButton.Click += (_, _) => ShowMoreMenu();

        CardList.Tapped += OnTapped;
        CardList.DoubleTapped += OnDoubleTapped;
        CardList.RightTapped += OnRightTapped;
        RootGrid.PointerPressed += OnBackgroundPressed;

        // Panel KHÔNG giành focus (NOACTIVATE) → app đích luôn giữ foreground để dán chắc chắn.
        // Phím điều hướng/search + click-ra-ngoài đi qua hook toàn cục (giống macOS nonactivating panel).
        _hooks.KeyDown += OnHookKey;
        _hooks.KeyUp += OnHookKeyUp;
        _hooks.MouseDown += OnHookMouse;

        UpdateTabVisual();
    }

    private bool _suppressHide;

    // ---------- Hook chuột: click ra ngoài panel/preview → đóng ----------
    private static bool InRect(IntPtr hwnd, int x, int y) =>
        Win32.GetWindowRect(hwnd, out var r) && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    private void OnHookMouse(int x, int y)
    {
        if (!IsPanelVisible) return;
        // Click bất kỳ khi đang đổi tên → lưu (quy tắc Finder: click ra ngoài là xác nhận).
        if (_renamingCard != null) Enq(CommitRename);

        // Menu "…" đang mở: click vào panel HOẶC menu (cùng process) → để chúng tự xử lý (chọn mục /
        // light-dismiss). Click ra NGOÀI (desktop/app khác) → đóng cả menu lẫn panel ngay một cú click
        // (trước đây phải click 2 lần: 1 lần tắt menu, 1 lần tắt panel).
        if (_moreMenu is { } moreMenu)
        {
            if (IsOwnWindowAt(x, y)) return;
            DispatcherQueue.TryEnqueue(() => { moreMenu.Hide(); if (IsPanelVisible) HidePanel(); });
            return;
        }

        // Popover filter: y hệt menu "…". Nó cao 330 trong khi panel chỉ ~250, nên WinUI dựng nó
        // thành cửa sổ RIÊNG nằm ngoài khung panel — thiếu nhánh này thì click vào chip filter bị
        // tính là "click ra ngoài panel" và panel đóng ngay trước khi chip kịp nhận cú click.
        if (_filterFlyout is { } filterFlyout)
        {
            if (IsOwnWindowAt(x, y)) return;
            DispatcherQueue.TryEnqueue(() => { filterFlyout.Hide(); if (IsPanelVisible) HidePanel(); });
            return;
        }

        // Dialog xác nhận / context menu thẻ đang mở → KHÔNG ẩn gì (kể cả preview), tránh đóng panel giữa chừng.
        if (_suppressHide) return;
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
        UpdateTabVisual(animate: true);
        UpdateEmptyState();
    }

    private void UpdateTabVisual(bool animate = false)
    {
        if (_vm == null) return;
        bool all = _vm.ActiveTab == ClipboardTab.All;
        var onActive = ThemeService.SegmentActiveTextBrush;  // chữ/icon trên "thumb": trắng
        var idle = ThemeService.SecondaryTextBrush;          // chữ/icon tab không chọn

        TabTrack.Background = ThemeService.SegmentTrackBrush;
        TabTrackCompact.Background = ThemeService.SegmentTrackBrush;
        TabThumb.Background = ThemeService.SegmentActiveBrush;
        TabCompactThumb.Background = ThemeService.SegmentActiveBrush;

        TabAllText.Foreground = TabAllIcon.Foreground = all ? onActive : idle;
        TabPinText.Foreground = TabPinIcon.Foreground = all ? idle : onActive;
        TabAllCompactIcon.Foreground = all ? onActive : idle;
        TabPinCompactIcon.Foreground = all ? idle : onActive;

        PositionThumbs(animate);
    }

    /// <summary>Trượt "thumb" xanh tới tab đang chọn (hiệu ứng gạt công tắc). Chờ layout nếu chưa có kích thước.</summary>
    private void PositionThumbs(bool animate)
    {
        if (_vm == null) return;
        bool all = _vm.ActiveTab == ClipboardTab.All;
        SlideThumb(TabCompactThumbT, all ? 0 : 34, animate); // compact: mỗi ô 34px

        double allW = TabAllButton.ActualWidth, pinW = TabPinButton.ActualWidth;
        if (allW <= 0 || pinW <= 0) return; // chưa layout → TabRow.SizeChanged sẽ gọi lại (không retry vô hạn)
        const double spacing = 4;            // = TabRow.Spacing
        MoveThumb(TabThumb, TabThumbT, all ? 0 : allW + spacing, all ? allW : pinW, animate);
    }

    // Thumb tab thường: vừa trượt X vừa co giãn Width theo segment đang chọn.
    private static void MoveThumb(Microsoft.UI.Xaml.Controls.Border thumb,
        Microsoft.UI.Xaml.Media.TranslateTransform t, double toX, double toW, bool animate)
    {
        if (!animate) { thumb.Width = toW; t.X = toX; return; }
        var ease = new Microsoft.UI.Xaml.Media.Animation.CubicEase
        { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(260);
        var ax = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = toX, Duration = dur, EasingFunction = ease };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(ax, t);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(ax, "X");
        var aw = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = toW, Duration = dur, EasingFunction = ease, EnableDependentAnimation = true };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(aw, thumb);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(aw, "Width");
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(ax); sb.Children.Add(aw); sb.Begin();
    }

    private static void SlideThumb(Microsoft.UI.Xaml.Media.TranslateTransform t, double toX, bool animate)
    {
        if (!animate) { t.X = toX; return; }
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = toX,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
        };
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, t);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "X");
        sb.Children.Add(anim);
        sb.Begin();
    }

    private bool _searchFocused;
    /// <summary>
    /// Màu chữ + con trỏ nhập của ô search. WinUI 3 không expose caret: không có TextBox.CaretBrush,
    /// cũng không có theme resource nào tên *Caret* (đã dò metadata Windows App SDK). Caret được vẽ
    /// theo Foreground của ContentElement bên trong template — tức các resource TextControlForeground*
    /// — chứ KHÔNG theo TextBox.Foreground (thứ chỉ tô chữ). Hai kênh tách rời nên:
    ///  • Chữ gõ vào: luôn PrimaryText của app (#ECECEC tối / #1C1C1E sáng) — trung tính, không xanh/đỏ.
    ///  • Caret: cùng màu chữ khi đang gõ; trong suốt khi ô trống để bỏ gạch dọc nhấp nháy dính sát
    ///    placeholder "Search". Đổi .Color trên chính instance brush → có hiệu lực ngay.
    /// </summary>
    private void UpdateCaret()
    {
        SearchBox.Foreground = ThemeService.PrimaryTextBrush;
        var caret = SearchBox.Text.Length == 0 ? Microsoft.UI.Colors.Transparent : ThemeService.PrimaryText;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            if (SearchBox.Resources[key] is SolidColorBrush b) b.Color = caret;
    }

    /// <summary>Ô search đồng bộ segmented tab: nền track mờ, viền xanh accent khi focus.</summary>
    private void ApplySearchFieldTheme()
    {
        SearchFieldBorder.Background = ThemeService.SegmentTrackBrush;
        SearchFieldBorder.BorderBrush = _searchFocused
            ? ThemeService.SegmentActiveBrush : ThemeService.SettingsCardStroke;
        UpdateCaret();
    }

    /// <summary>Popup đang mở của thanh search (bảng filter hoặc danh sách "+N" của hàng token). Hook
    /// chuột toàn cục cần biết để không nhầm click vào popup thành click ra ngoài panel.</summary>
    private Flyout? _filterFlyout;

    private void TrackFilterFlyout(Flyout flyout)
    {
        _filterFlyout = flyout;
        flyout.Closed += (_, _) => { if (ReferenceEquals(_filterFlyout, flyout)) _filterFlyout = null; };
    }

    /// <summary>Bảng filter neo vào nút filter trong ô search. Dựng lại mỗi lần mở: danh sách app phải
    /// đọc lịch sử ở thời điểm mở, không phải lúc panel được tạo.</summary>
    private void ShowFilterPopover()
    {
        if (_vm == null) return;
        var flyout = new Flyout
        {
            Content = FilterPopover.Build(_vm, RefreshFilterUi),
            Placement = FlyoutPlacementMode.Bottom,
            ShouldConstrainToRootBounds = false,
        };
        TrackFilterFlyout(flyout);
        flyout.ShowAt(FilterButton);
    }

    /// <summary>Vẽ lại hàng token + trạng thái nút filter (accent + chấm khi còn filter bật).</summary>
    private void RefreshFilterUi()
    {
        if (_vm == null) return;
        ActiveFilterTokens.Populate(FilterTokenRow, _vm, RefreshFilterUi, TrackFilterFlyout);
        bool active = !_vm.Filters.IsEmpty;
        FilterIcon.Foreground = active ? ThemeService.SegmentActiveBrush : ThemeService.ToolbarIconBrush;
        FilterDot.Fill = ThemeService.SegmentActiveBrush;
        FilterDot.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyState();
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
            // Đóng ô search thì bỏ luôn token filter của nó: để filter còn áp mà không còn gì hiển thị
            // sẽ trông như các item đã biến mất.
            _filterFlyout?.Hide();
            _vm.Filters.Clear();
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

    // Menu "…" hiện tại (nếu đang mở). Giữ tham chiếu để đóng khi panel ẩn/hiện lại + để LL-hook
    // biết menu đang mở mà xử lý click ra ngoài.
    private MenuFlyout? _moreMenu;

    private void ShowMoreMenu()
    {
        var menu = new MenuFlyout();
        var clear = new MenuFlyoutItem { Text = "Clear History", Icon = new SymbolIcon(Symbol.Delete) };
        clear.Click += async (_, _) => await ClearHistoryConfirmAsync();
        menu.Items.Add(clear);

        var update = new MenuFlyoutItem { Text = "Check for Updates…", Icon = new SymbolIcon(Symbol.Refresh) };
        update.Click += (_, _) => { HidePanel(); UpdateRequested?.Invoke(); };
        menu.Items.Add(update);

        menu.Items.Add(new MenuFlyoutSeparator());
        var settings = new MenuFlyoutItem { Text = "Settings…", Icon = new SymbolIcon(Symbol.Setting) };
        settings.Click += (_, _) => { HidePanel(); SettingsRequested?.Invoke(); };
        menu.Items.Add(settings);
        var quit = new MenuFlyoutItem { Text = "Quit xPaste" };
        quit.Click += (_, _) => QuitRequested?.Invoke();
        menu.Items.Add(quit);

        _moreMenu = menu;
        menu.Closed += (_, _) => _moreMenu = null;
        menu.ShowAt(MoreButton);
    }

    // Cửa sổ dưới điểm (x,y) có thuộc process xPaste không (panel HOẶC popup menu "…")?
    // Dùng để phân biệt click vào UI của mình với click ra ngoài (desktop/app khác).
    private static bool IsOwnWindowAt(int x, int y)
    {
        var hw = Win32.WindowFromPoint(new Win32.POINT { X = x, Y = y });
        if (hw == IntPtr.Zero) return false;
        Win32.GetWindowThreadProcessId(hw, out uint pid);
        return pid == (uint)Environment.ProcessId;
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

    // ---------- Đổi tên item (rename) NGAY trên header (inline) — nhập lái bằng hook vì panel không giữ focus ----------
    private CardViewModel? _renamingCard;

    private void StartRename(CardViewModel card)
    {
        CommitRename(); // đóng phiên rename đang mở (nếu có) trước khi mở cái mới
        _renamingCard = card;
        card.RenameDraft = card.Item.Label ?? card.Title;
        card.IsRenaming = true;
    }

    private void CommitRename()
    {
        if (_renamingCard is not { } c) return;
        _renamingCard = null;
        var label = (c.RenameDraft ?? "").Trim();
        c.IsRenaming = false;
        _vm?.SetLabel(c, string.IsNullOrEmpty(label) ? null : label);
    }

    private void CancelRename()
    {
        if (_renamingCard is not { } c) return;
        _renamingCard = null;
        c.IsRenaming = false;
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
            // Filter đang bật cũng là một truy vấn, dù ô gõ trống: "Nothing copied yet" ở đây sẽ nói
            // sai rằng lịch sử rỗng.
            EmptyState.Text = !string.IsNullOrEmpty(_vm.SearchQuery) || !_vm.Filters.IsEmpty ? "No results"
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
        if (_vm == null || card == null) return;
        // Double-click trên HEADER → đổi tên tại chỗ (như macOS); nơi khác → dán.
        if (IsWithinHeader(e.OriginalSource)) { StartRename(card); return; }
        _vm.Paste(card.Item);
    }

    private static bool IsWithinHeader(object source)
    {
        var el = source as DependencyObject;
        while (el != null)
        {
            if (el is FrameworkElement fe && fe.Tag as string == "header") return true;
            el = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(el);
        }
        return false;
    }

    private void OnBackgroundPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_vm == null) return;
        bool onCard = CardFrom(e.OriginalSource) != null;
        // Bảng filter đang mở: click này là cú nhấn đóng nó (light dismiss), không phải ý muốn đóng
        // search — đóng search ở đây sẽ kéo mất luôn cái neo của bảng.
        if (_filterFlyout != null) return;
        // Search đang mở + click ra ngoài thanh search (vùng trống/toolbar, không phải card) → đóng search.
        // Giữ search mở khi click vào card để còn dán từ kết quả đã lọc.
        if (_vm.IsSearchOpen && !onCard && !IsWithinSearchBar(e.OriginalSource))
            OpenSearch(false);
        // Click vùng trống: thu multi-selection về MỘT thẻ, không bỏ trắng — hàng thẻ luôn còn một
        // thẻ sáng để ⏎ / Backspace tác động.
        if (!onCard) _vm.CollapseSelection();
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

        // "Paste as…" — các phép biến đổi văn bản áp dụng được (Trim, JSON, URL encode, Domain…).
        MenuFlyoutSubItem? pasteAs = null;
        foreach (var tr in TextTransform.For(card.Item.Text, card.Item.Type))
        {
            pasteAs ??= new MenuFlyoutSubItem { Text = "Paste as…" };
            var t = tr;
            var mi = new MenuFlyoutItem { Text = t.Name };
            mi.Click += (_, _) => _vm!.PasteTransformed(card.Item, t);
            pasteAs.Items.Add(mi);
        }
        if (pasteAs != null) menu.Items.Add(pasteAs);

        Add("Copy", () => _vm.Copy(card.Item));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (PanelViewModel.CanOpenUrl(card.Item)) Add("Open URL", () => _vm.OpenUrl(card.Item));
        Add("Delete", () => _vm.Delete(card.Item));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Rename…", () => StartRename(card));
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
                      VK_1 = 0x31, VK_9 = 0x39,
                      VK_A = 0x41, VK_C = 0x43, VK_LWIN = 0x5B, VK_RWIN = 0x5C,
                      VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;

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

        // Đang đổi tên (rename inline trên header): nhập lái bằng hook. Enter lưu, Esc huỷ, Backspace xoá,
        // ký tự in được nối vào tên. Nuốt MỌI phím để không lọt xuống app/clipboard/điều hướng.
        if (_renamingCard is { } rc)
        {
            switch (vk)
            {
                case VK_RETURN: Enq(CommitRename); return true;
                case VK_ESCAPE: Enq(CancelRename); return true;
                case VK_BACK: Enq(() => { if (rc.RenameDraft.Length > 0) rc.RenameDraft = rc.RenameDraft[..^1]; }); return true;
                default:
                    if (ctrl || Down(VK_MENU) || Down(VK_LWIN) || Down(VK_RWIN)) return true;
                    char ch = CharFromKey(vk, shift);
                    if (ch != '\0') Enq(() => rc.RenameDraft += ch);
                    return true;
            }
        }

        // Ctrl+1..9: dán nhanh card thứ N (Ctrl+Shift+N = dán thô). Hoạt động cả khi đang search.
        if (ctrl && vk >= VK_1 && vk <= VK_9)
        {
            Enq(() => PasteAt(vk - VK_1, shift));
            return true;
        }
        // Giữ Ctrl → hiện badge số trên card; giữ Shift (khi đã có Ctrl) → badge báo dán thô.
        // KHÔNG nuốt phím modifier (return false) để tổ hợp khác vẫn hoạt động.
        if (vk is VK_LCONTROL or VK_RCONTROL or VK_CONTROL) { Enq(() => _vm.SetBadges(true, shift)); return false; }
        if (vk is VK_LSHIFT or VK_RSHIFT or VK_SHIFT) { if (ctrl) Enq(() => _vm.SetBadges(true, true)); return false; }

        // Đang GÕ search (search mở & chưa chọn kết quả): phím ký tự vào ô tìm kiếm.
        if (_vm.IsSearchOpen && !_browsingResults)
        {
            switch (vk)
            {
                // Escape đóng theo lớp: bảng filter trước, rồi mới tới ô search.
                case VK_ESCAPE:
                    Enq(() => { if (_filterFlyout != null) _filterFlyout.Hide(); else OpenSearch(false); });
                    return true;
                case VK_RETURN: Enq(() => PasteEnter(shift)); return true;
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
            case VK_RETURN: Enq(() => PasteEnter(shift)); return true;
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

    /// <summary>Enter: chọn nhiều → dán nối bằng separator; chọn một → dán item chính.</summary>
    private void PasteEnter(bool plain)
    {
        if (_vm == null) return;
        if (_vm.Selection.Count > 1) _vm.PasteSelectedJoined(plain);
        else PastePrimary(plain);
    }

    /// <summary>Dán card thứ <paramref name="index"/> (0-based) — phím tắt Ctrl+1..9.</summary>
    private void PasteAt(int index, bool plain)
    {
        var c = _vm?.CardAt(index);
        if (c == null) return;
        if (plain) _vm!.PastePlain(c.Item); else _vm!.Paste(c.Item);
    }

    /// <summary>Nhả Ctrl → ẩn badge; nhả Shift (còn giữ Ctrl) → badge bỏ chế độ dán thô.</summary>
    private void OnHookKeyUp(int vk)
    {
        if (_vm == null || !IsPanelVisible) return;
        if (vk is VK_LCONTROL or VK_RCONTROL or VK_CONTROL) Enq(() => _vm.SetBadges(false, false));
        else if (vk is VK_LSHIFT or VK_RSHIFT or VK_SHIFT) { if (Down(VK_CONTROL)) Enq(() => _vm.SetBadges(true, false)); }
    }

    private void CopyPrimary() { var p = _vm?.PrimaryCard(); if (p != null) _vm!.Copy(p.Item); }
    private void PreviewPrimary() { var p = _vm?.PrimaryCard(); if (p != null) TogglePreview(p); }
    // Gán Text lập trình sẽ reset caret về đầu → đưa SelectionStart về CUỐI để con trỏ nhấp nháy sau chữ cuối.
    private void CaretToEnd() { SearchBox.SelectionStart = SearchBox.Text.Length; }
    private void AppendSearch(char c) { _query += c; SearchBox.Text = _query; CaretToEnd(); }
    /// <summary>Backspace trên ô TRỐNG xoá token filter cuối, đúng cách mọi ô nhập dạng token hành xử.
    /// Còn chữ thì xoá chữ trước — không ai muốn gõ hụt một ký tự lại mất luôn cả bộ lọc.</summary>
    private void SearchBackspace()
    {
        if (_query.Length > 0) { _query = _query[..^1]; SearchBox.Text = _query; CaretToEnd(); return; }
        if (_vm is { } vm && !vm.Filters.IsEmpty) vm.Filters.RemoveLastToken(PanelViewModel.AppName);
        CaretToEnd();
    }

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

    // ScrollViewer bên trong ListView (lấy 1 lần qua visual tree) để reset vị trí cuộn về đầu.
    private ScrollViewer? _cardScroll;
    private ScrollViewer? CardScroll => _cardScroll ??= FindScrollViewer(CardList);

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var found = FindScrollViewer(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Ép danh sách về đầu (offset 0 cả ngang/dọc) và GIỮ ở đó qua vài lượt layout kế tiếp.
    ///
    /// Một lần ChangeView không đủ, mà "gọi tới khi offset đọc ra 0" cũng KHÔNG đủ — đó là chỗ hỏng
    /// của bản trước: offset chỉ cập nhật sau lượt layout, nên ngay sau khi danh sách đổi nó vẫn đọc
    /// ra 0 và vòng lặp dừng ngay; cú dịch offset thật (ListView bù chỗ cho item vừa chèn vào đầu, hay
    /// việc realize item khi panel hiện ra) xảy ra SAU đó và không còn ai kéo về nữa.
    ///
    /// Nên ở đây không kiểm tra gì cả: cứ đặt lại offset đều đặn suốt <paramref name="ticks"/> lượt
    /// dispatcher, phủ trọn quãng panel trượt ra và realize xong. Vài lệnh ChangeView thừa không tốn gì.
    /// </summary>
    private void ResetScrollToStart(int ticks)
    {
        CardScroll?.ChangeView(0, 0, null, true);
        if (ticks > 0) DispatcherQueue.TryEnqueue(() => ResetScrollToStart(ticks - 1));
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
        var mon = Win32.MonitorFromWindow(_hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        double scale = Win32.ScaleForMonitor(mon);
        int w = (int)Math.Round((isUrl ? 560 : 420) * scale);
        int h = (int)Math.Round((isUrl ? 440 : 340) * scale);

        // onClose (nút ✕) chỉ đóng preview; onNavigateAway (Open in Browser) đóng cả panel
        // (HidePanel tự gọi HidePreview bên trong).
        _previewWindow = new PreviewWindow(card, _vm!, HidePreview, HidePanel);
        _previewHwnd = _previewWindow.Hwnd;
        _previewItemId = card.Id;

        Win32.GetWindowRect(_hwnd, out var pr);
        var pos = _settings.Get("panelPosition", "bottom");

        // Tâm NGANG + ĐỈNH card được preview (toạ độ màn hình vật lý) — để mỏ trỏ SÁT card, không phải mép panel.
        int cardCenterX = pr.Left + (pr.Right - pr.Left) / 2;
        int cardTopY = pr.Top;
        if (CardList.ContainerFromItem(card) is FrameworkElement fe)
        {
            try
            {
                var p = fe.TransformToVisual(RootGrid)
                          .TransformPoint(new Windows.Foundation.Point(fe.ActualWidth / 2, 0));
                cardCenterX = pr.Left + (int)Math.Round(p.X * scale);
                cardTopY = pr.Top + (int)Math.Round(p.Y * scale);
            }
            catch { }
        }

        // Panel dưới đáy: preview NỔI TRÊN card + "mỏ" tam giác chỉ XUỐNG SÁT card (như macOS).
        if (pos == "bottom")
        {
            int beakH = (int)Math.Round(9 * scale);
            int totalH = h + beakH;
            int margin = (int)Math.Round(6 * scale);

            var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            Win32.GetMonitorInfo(mon, ref mi);
            var work = mi.rcWork;

            int px = Math.Clamp(cardCenterX - w / 2,
                work.Left + margin, Math.Max(work.Left + margin, work.Right - w - margin));
            // Đỉnh mỏ (đáy cửa sổ) nằm ngay TRÊN đỉnh card 3px → preview chồm xuống che toolbar, mỏ sát card.
            int tipY = cardTopY - (int)Math.Round(3 * scale);
            int py = tipY - totalH;
            if (py < work.Top + margin) py = work.Top + margin;

            _previewWindow.ShowAt(px, py, w, totalH, beakH, cardCenterX - px);
        }
        else
        {
            // Vị trí khác (top/left/right): giữ căn giữa panel, nổi phía trên, không mỏ.
            int cx = pr.Left + ((pr.Right - pr.Left) - w) / 2;
            int cy = pr.Top - h - (int)Math.Round(12 * scale);
            if (cy < 0) cy = 0;
            _previewWindow.ShowAt(cx, cy, w, h);
        }
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
        ApplySearchFieldTheme();
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

    /// <summary>
    /// Dựng sẵn panel NGẦM ngay lúc khởi động (offscreen, KHÔNG kích hoạt): trả trước toàn bộ chi phí
    /// "cold" của LẦN MỞ ĐẦU — tạo container ListView, chạy layout, rasterize chữ (BitmapCache). Nhờ vậy
    /// lần đầu người dùng nhấn hotkey, panel chỉ "hiện lại" nên nhanh như các lần sau (không giật/đợi).
    /// Caller phải gọi SAU khi VM đã có card (Refresh) để container thực sự được realize.
    /// </summary>
    public void Prewarm()
    {
        var (x, y, w, h) = TargetRect();
        var (ox, oy) = OffscreenOrigin(x, y, w, h);
        // Hiện OFFSCREEN (ngoài mọi màn hình) → vô hình với người dùng nhưng DWM vẫn composite, nên
        // ListView realize container + rasterize thật. KHÔNG bật hook, KHÔNG đặt IsPanelVisible=true.
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, ox, oy, w, h,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        ApplyRoundedRegion(w, h);
        ApplyTheme();
        ConfigureLayout();

        // Chờ vài khung để realize + (nếu có) hai-pha chữ diễn ra XONG khi còn offscreen rồi mới ẩn hẳn.
        int frames = 4;
        void HideWhenWarm()
        {
            if (--frames > 0)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, HideWhenWarm);
                return;
            }
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, ox, oy, w, h, Win32.SWP_HIDEWINDOW);
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, HideWhenWarm);
    }

    public void ShowPanel()
    {
        // Menu "…" / popover filter có thể còn mở từ lần trước (panel ẩn bằng hotkey/tray khi chúng
        // đang bật) → đóng lại.
        _moreMenu?.Hide();
        _filterFlyout?.Hide();
        // Hạ cờ ẩn để các lượt Refresh khi panel đang mở lại đặt selection về thẻ đầu như thường.
        // (Bản thân lần mở này không cần rebase: bên dưới đã chọn thẳng thẻ đầu.)
        if (_vm != null) _vm.IsHidingPanel = false;

        var (x, y, w, h) = TargetRect();
        var (sx, sy) = OffscreenOrigin(x, y, w, h);
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, sx, sy, w, h,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        ApplyRoundedRegion(w, h); // bo góc theo kích thước hiện tại (đặt lại mỗi lần mở phòng khi đổi màn hình)
        IsPanelVisible = true;

        // Bật hook toàn cục để nhận phím + click ngoài mà KHÔNG giành focus của app đích.
        _hooks.Install();
        DisableDwmBorder(); // áp lại mỗi lần hiện (phòng khi hệ thống vẽ lại viền)
        ApplyTheme();       // cập nhật theme (phòng khi đổi trong Settings lúc panel ẩn)
        ApplyScreenShareAffinity(_hwnd); // ẩn/hiện panel khi screen share (setting Privacy)
        ConfigureLayout(); // hướng list + search theo vị trí panel (ngang/dọc)

        // Auto-select item đầu để bàn phím dùng được ngay (giống panelDidOpen macOS).
        if (_vm != null && _vm.Cards.Count > 0) _vm.SelectSingle(_vm.Cards[0].Id);
        // Cuộn hẳn về ĐẦU (offset 0), không dùng ScrollIntoView — nếu lần trước cuộn xa, ScrollIntoView
        // chỉ kéo item0 sát mép (mất padding). Reset về 0 để mỗi lần mở item luôn ở đúng vị trí đầu.
        // Ngoài khối if: danh sách rỗng lúc mở (đang lọc) rồi có item ngay sau đó vẫn phải bắt đầu từ 0.
        // MỘT lần là đủ: danh sách đã được đồng bộ từ lúc copy/dán (App gọi Refresh ngay khi store đổi)
        // nên ở đây không còn cú chèn/dời nào đẩy offset đi nữa. Lặp ChangeView suốt lúc panel trượt ra
        // chỉ tổ thêm việc cho đúng khung hình đang chạy animation.
        ResetScrollToStart(0);
        UpdateEmptyState();
        _vm?.SetBadges(false, false); // badge chỉ hiện khi giữ Ctrl

        Opened?.Invoke();
        // Chốt lại một lần khi panel đã trượt ra xong, phòng khi việc realize item trong lúc trượt còn
        // đẩy offset đi. Sau animation nên không tranh khung hình với nó.
        // 80ms: đo được toàn bộ phần tính toán khi mở panel < 1ms, nên thời gian mở CHÍNH LÀ animation
        // này. 80 vẫn đủ ~5 khung ở 60Hz để mắt thấy chuyển động, không cụt như 60.
        Slide(sx, sy, x, y, w, h, durationMs: 80, easeOut: true, () => ResetScrollToStart(1));
    }

    public void HidePanel()
    {
        if (!IsPanelVisible) return;
        CommitRename(); // lưu tên đang sửa (nếu có) khi ẩn panel
        IsPanelVisible = false;
        _hooks.Uninstall();
        _vm?.SetBadges(false, false);
        // Đóng search + xoá filter bên dưới làm hàng thẻ đổi; cờ này chặn việc đặt lại selection về
        // thẻ đầu ngay lúc panel đang trượt đi, vốn chỉ tổ nháy một viền sáng rồi biến mất.
        if (_vm != null) _vm.IsHidingPanel = true;
        if (_vm != null && _vm.IsSearchOpen) OpenSearch(false);
        HidePreview();
        var (x, y, w, h) = TargetRect();
        var (ex, ey) = OffscreenOrigin(x, y, w, h);
        Slide(x, y, ex, ey, w, h, durationMs: 100, easeOut: false, () =>
        {
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, ex, ey, w, h, Win32.SWP_HIDEWINDOW);
            // Kéo danh sách về đầu NGAY KHI ĐÓNG, lúc bố cục đã đứng yên và không còn ai nhìn: lần mở
            // sau chỉ còn là xác nhận offset đã ở 0, thay vì phải giành với lượt layout đang chạy.
            // Lặp thoải mái ở đây vì panel đã ẩn — không có animation nào để làm giật.
            ResetScrollToStart(6);
            Hidden?.Invoke();
        });
    }

    /// <summary>Cắt cửa sổ (kể cả nền acrylic) thành hình chữ nhật bo góc để panel bo tròn như macOS.
    /// Region theo toạ độ cửa sổ (px vật lý) nên KHÔNG đổi khi trượt — chỉ cần đặt lại khi đổi kích thước.</summary>
    private void ApplyRoundedRegion(int w, int h)
    {
        int d = (int)Math.Round(PanelCornerRadius * _monitorScale) * 2; // đường kính ellipse góc = 2×bán kính
        // +1 vì CreateRoundRectRgn loại trừ mép phải/dưới. Hệ thống tiếp quản region (không tự Delete).
        var rgn = Win32.CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d);
        Win32.SetWindowRgn(_hwnd, rgn, true);
    }

    private (int x, int y, int w, int h) TargetRect()
    {
        Win32.GetCursorPos(out var pt);
        var mon = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(mon, ref mi);
        // Dùng rcMonitor (toàn màn hình vật lý) thay vì rcWork (đã trừ taskbar) → panel PHỦ ĐÈ lên
        // taskbar/Start menu. Panel là HWND_TOPMOST nên nổi trên cả taskbar.
        var work = mi.rcMonitor;
        int fullW = work.Right - work.Left, fullH = work.Bottom - work.Top;
        // SetWindowPos dùng pixel VẬT LÝ, còn XAML layout theo pixel LOGIC. DPI phải lấy theo MÀN HÌNH
        // ĐÍCH (nơi con trỏ), không phải màn hình cửa sổ đang nằm — nếu không panel sai kích thước khi
        // hai màn khác DPI.
        double scale = Win32.ScaleForMonitor(mon);
        _monitorScale = scale;

        var pos = _settings.Get("panelPosition", "bottom");
        _panelHorizontal = pos is "bottom" or "top";
        // Khoảng cách panel với mép màn hình (điểm logic → px vật lý). Panel "nổi" tách khỏi mép:
        // chừa gap ở cạnh neo + hai cạnh dọc theo chiều dài (giống macOS insetBy).
        int gap = (int)Math.Round(PanelScreenInset * scale);
        _panelGap = gap;
        // Chiều dày panel = % cạnh ngắn màn hình (điểm logic), kẹp trong [min, thiết kế] để không
        // chiếm quá nhiều mà card cũng không nhỏ quá. Nội dung scale theo f = dày/thiếtKế nên luôn toàn vẹn.
        double screenShortLogical = (_panelHorizontal ? fullH : fullW) / scale;
        double thickLogical = Math.Clamp(PanelScreenRatio * screenShortLogical, PanelMinThickness, PanelDesignThickness);
        _panelScaleFactor = thickLogical / PanelDesignThickness;
        // Chiều dài panel đã trừ 2 lần gap (hai đầu) → nội dung fill vừa khít vùng đã thu hẹp.
        _panelLongLogical = ((_panelHorizontal ? fullW : fullH) - 2 * gap) / scale;
        int thick = (int)Math.Round(thickLogical * scale);
        return pos switch
        {
            "top"   => (work.Left + gap, work.Top + gap, fullW - 2 * gap, thick),
            "left"  => (work.Left + gap, work.Top + gap, thick, fullH - 2 * gap),
            "right" => (work.Right - thick - gap, work.Top + gap, thick, fullH - 2 * gap),
            _       => (work.Left + gap, work.Bottom - thick - gap, fullW - 2 * gap, thick),
        };
    }

    // Trượt panel hẳn ra ngoài màn hình: cộng thêm _panelGap để phần "nổi" (cách mép) cũng khuất hẳn.
    private (int x, int y) OffscreenOrigin(int x, int y, int w, int h) =>
        _settings.Get("panelPosition", "bottom") switch
        {
            "top"   => (x, y - h - _panelGap),
            "left"  => (x - w - _panelGap, y),
            "right" => (x + w + _panelGap, y),
            _       => (x, y + h + _panelGap),
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
