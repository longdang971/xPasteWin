using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using xPasteWin.Services;
using xPasteWin.ViewModels;

namespace xPasteWin.Views;

/// <summary>
/// Bảng filter sau nút filter của ô search: thu hẹp lịch sử theo LOẠI, theo APP đã copy, và theo
/// THỜI ĐIỂM copy. Port FilterPopover.swift — dựng bằng code thay vì XAML vì nội dung (danh sách app)
/// chỉ biết được lúc mở, và mỗi chip cần trạng thái hover/selected riêng.
/// </summary>
public static class FilterPopover
{
    private const double Width = 420;
    private const double Height = 330;

    /// <summary>Dựng nội dung popover. <paramref name="onChanged"/> chạy sau mỗi lần bật/tắt để
    /// người gọi vẽ lại token + nút filter (danh sách card do SearchFilters.Changed lo).</summary>
    public static FrameworkElement Build(PanelViewModel vm, Action onChanged)
    {
        var filters = vm.Filters;
        var root = new StackPanel { Spacing = 16, Padding = new Thickness(16) };

        // Rebuild tại chỗ: bật một chip đổi trạng thái của chính nó VÀ có thể làm hiện/ẩn nút
        // "Clear filters", nên vẽ lại cả bảng đơn giản hơn là vá từng mảnh.
        void Rebuild()
        {
            root.Children.Clear();

            root.Children.Add(Section("Type", Chips(FilterTypeInfo.All.Select(t =>
                new ChipSpec(t.Title(), t.Glyph(), null, filters.Types.Contains(t),
                             () => { filters.Toggle(t); Rebuild(); onChanged(); })))));

            var apps = vm.AppsInHistory();
            if (apps.Count > 0)
                root.Children.Add(Section("App", Chips(apps.Select(a =>
                    new ChipSpec(a.Name, null, a.IconPath, filters.Apps.Contains(a.ExePath),
                                 () => { filters.ToggleApp(a.ExePath); Rebuild(); onChanged(); })))));

            root.Children.Add(Section("Date", Chips(DateFilterInfo.All.Select(d =>
                new ChipSpec(d.Title(), FilterTypeInfo.CalendarGlyph, null, filters.Date == d,
                             () => { filters.ToggleDate(d); Rebuild(); onChanged(); })))));

            if (!filters.IsEmpty) root.Children.Add(ClearButton(filters, Rebuild, onChanged));
        }

        Rebuild();

        return new ScrollViewer
        {
            Width = Width,
            Height = Height,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root,
        };
    }

    private static FrameworkElement Section(string title, UIElement content)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeService.SecondaryTextBrush,
        });
        panel.Children.Add(content);
        return panel;
    }

    private sealed record ChipSpec(string Title, string? Glyph, string? IconPath, bool IsOn, Action OnTap);

    /// <summary>Lưới 3 cột như LazyVGrid của macOS: cột co giãn đều, cách nhau 8.</summary>
    private static UIElement Chips(IEnumerable<ChipSpec> specs)
    {
        var list = specs.ToList();
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (int c = 0; c < 3; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rows = (list.Count + 2) / 3;
        for (int r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < list.Count; i++)
        {
            var chip = Chip(list[i]);
            Grid.SetRow(chip, i / 3);
            Grid.SetColumn(chip, i % 3);
            grid.Children.Add(chip);
        }
        return grid;
    }

    /// <summary>Một pill bật/tắt. Chip đang bật lấy màu accent để nhiều filter cùng bật đọc được
    /// trong một cái liếc — đó chính là lý do dùng popover thay vì gõ truy vấn.</summary>
    private static FrameworkElement Chip(ChipSpec spec)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        if (spec.IconPath != null)
            content.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(spec.IconPath)) { DecodePixelType = DecodePixelType.Physical, DecodePixelWidth = 64 },
                Width = 16,
                Height = 16,
            });
        else if (spec.Glyph != null)
            content.Children.Add(new FontIcon
            {
                Glyph = spec.Glyph,
                FontSize = 12,
                Width = 16,
                Foreground = spec.IsOn ? WhiteBrush : ThemeService.PrimaryTextBrush,
            });
        content.Children.Add(new TextBlock
        {
            Text = spec.Title,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = spec.IsOn ? WhiteBrush : ThemeService.PrimaryTextBrush,
        });

        var border = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 8, 10, 8),
            Background = spec.IsOn ? ThemeService.SegmentActiveBrush : ThemeService.FilterChipBrush,
            Child = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // Hover chỉ đổi nền khi chip TẮT: chip bật đã là accent, làm sáng thêm chỉ gây nhiễu.
        border.PointerEntered += (_, _) => { if (!spec.IsOn) border.Background = ThemeService.FilterChipHoverBrush; };
        border.PointerExited += (_, _) => { if (!spec.IsOn) border.Background = ThemeService.FilterChipBrush; };
        border.Tapped += (_, e) => { e.Handled = true; spec.OnTap(); };
        ToolTipService.SetToolTip(border, spec.Title);
        return border;
    }

    private static FrameworkElement ClearButton(SearchFilters filters, Action rebuild, Action onChanged)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon
        {
            Glyph = FilterTypeInfo.CloseGlyph,
            FontSize = 12,
            Foreground = ThemeService.SecondaryTextBrush,
        });
        content.Children.Add(new TextBlock
        {
            Text = "Clear filters",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = ThemeService.SecondaryTextBrush,
        });
        var host = new Border
        {
            Child = content,
            Padding = new Thickness(2, 2, 2, 2),
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        host.Tapped += (_, e) => { e.Handled = true; filters.Clear(); rebuild(); onChanged(); };
        return host;
    }

    private static SolidColorBrush WhiteBrush => new(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
}

/// <summary>Một filter đang bật, bất kể thuộc mục nào — để hàng token đo và cắt chúng cùng nhau.</summary>
public sealed record ActiveFilter(string Id, string Title, string? Glyph, string? IconPath, Action Remove);

/// <summary>
/// Các filter đang bật, vẽ thành token BÊN TRONG ô search — để một danh sách đã bị thu hẹp luôn nói
/// thẳng ra <i>vì sao</i> nó hẹp. Bấm vào token là gỡ đúng filter đó. Port ActiveFilterTokens.swift.
/// </summary>
public static class ActiveFilterTokens
{
    /// <summary>Đo bề rộng nhãn 12px bằng chính engine chữ sẽ vẽ nó — tiêm một lần cho
    /// FilterTokenLayout, vốn cố ý không biết gì về WinUI để còn test được.</summary>
    private static double MeasureLabel(string title)
    {
        var probe = new TextBlock { Text = title, FontSize = 12 };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    /// <summary>
    /// Đổ token vào <paramref name="host"/>. Trả về số token bị giấu sau chip đếm.
    /// <paramref name="trackFlyout"/> nhận flyout của chip "+N" khi nó mở: panel ẩn mình theo hook
    /// chuột toàn cục, nên nó phải BIẾT có popup của chính mình đang mở, nếu không click vào popup bị
    /// tính là click ra ngoài panel.
    /// </summary>
    public static int Populate(Panel host, PanelViewModel vm, Action onChanged, Action<Flyout>? trackFlyout = null)
    {
        FilterTokenLayout.LabelMeasure ??= MeasureLabel;
        host.Children.Clear();
        var active = Active(vm, onChanged);
        if (active.Count == 0) return 0;

        int shown = FilterTokenLayout.VisibleCount(active.Select(a => a.Title).ToList(), FilterTokenLayout.RowBudget);
        var hidden = active.Skip(shown).ToList();

        foreach (var f in active.Take(shown)) host.Children.Add(Token(f));
        if (hidden.Count > 0) host.Children.Add(OverflowChip(hidden, trackFlyout));
        return hidden.Count;
    }

    /// <summary>Mọi filter đang bật gộp thành MỘT danh sách, theo đúng thứ tự hàng token vẽ: type,
    /// rồi app theo tên, rồi date. Gộp phẳng để phép tính tràn nhìn thấy một dãy thay vì ba.</summary>
    private static List<ActiveFilter> Active(PanelViewModel vm, Action onChanged)
    {
        var filters = vm.Filters;
        var all = FilterTypeInfo.All.Where(t => filters.Types.Contains(t))
            .Select(t => new ActiveFilter($"type:{t}", t.Title(), t.Glyph(), null,
                                          () => { filters.Toggle(t); onChanged(); }))
            .ToList();

        all.AddRange(filters.Apps
            .Select(p => (Path: p, Name: PanelViewModel.AppName(p)))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new ActiveFilter($"app:{a.Path}", a.Name, null,
                                          SourceAppService.GetVisual(a.Path)?.IconPath,
                                          () => { filters.ToggleApp(a.Path); onChanged(); })));

        if (filters.Date is { } d)
            all.Add(new ActiveFilter("date", d.Title(), FilterTypeInfo.CalendarGlyph, null,
                                     () => { filters.ToggleDate(d); onChanged(); }));
        return all;
    }

    private static FrameworkElement Token(ActiveFilter f)
    {
        var iconHost = new Grid { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        UIElement? idle = null;
        if (f.IconPath != null)
            idle = new Image
            {
                Source = new BitmapImage(new Uri(f.IconPath)) { DecodePixelType = DecodePixelType.Physical, DecodePixelWidth = 56 },
                Width = 14,
                Height = 14,
            };
        else if (f.Glyph != null)
            idle = new FontIcon { Glyph = f.Glyph, FontSize = 10, Foreground = ThemeService.PrimaryTextBrush };
        var cross = new FontIcon
        {
            Glyph = FilterTypeInfo.CloseGlyph,
            FontSize = 9,
            Foreground = ThemeService.PrimaryTextBrush,
            Visibility = Visibility.Collapsed,
        };
        if (idle != null) iconHost.Children.Add(idle);
        iconHost.Children.Add(cross);

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(iconHost);
        content.Children.Add(new TextBlock
        {
            Text = f.Title,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeService.PrimaryTextBrush,
        });

        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4, 3, 7, 3),
            Background = ThemeService.FilterTokenBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        };
        // Hover đổi icon thành dấu X: token vừa là nhãn vừa là nút gỡ, đổi icon nói rõ bấm vào sẽ mất.
        border.PointerEntered += (_, _) =>
        {
            border.Background = ThemeService.FilterTokenHoverBrush;
            if (idle != null) idle.Visibility = Visibility.Collapsed;
            cross.Visibility = Visibility.Visible;
        };
        border.PointerExited += (_, _) =>
        {
            border.Background = ThemeService.FilterTokenBrush;
            cross.Visibility = Visibility.Collapsed;
            if (idle != null) idle.Visibility = Visibility.Visible;
        };
        border.Tapped += (_, e) => { e.Handled = true; f.Remove(); };
        ToolTipService.SetToolTip(border, "Remove this filter");
        return border;
    }

    /// <summary>Đại diện cho những filter không vừa. Bấm vào để liệt kê chúng, mỗi cái vẫn gỡ được —
    /// nếu không thì hàng token là đường duy nhất để tháo một filter ra.</summary>
    private static FrameworkElement OverflowChip(List<ActiveFilter> hidden, Action<Flyout>? trackFlyout)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        content.Children.Add(new FontIcon
        {
            Glyph = FilterTypeInfo.PlusGlyph,
            FontSize = 9,
            Foreground = ThemeService.PrimaryTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = hidden.Count.ToString(),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = ThemeService.PrimaryTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var list = new StackPanel { Spacing = 4, Padding = new Thickness(10) };
        foreach (var f in hidden) list.Children.Add(Token(f));

        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 7, 3),
            Background = ThemeService.FilterTokenBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        };
        var flyout = new Flyout { Content = list, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        border.Tapped += (_, e) => { e.Handled = true; trackFlyout?.Invoke(flyout); flyout.ShowAt(border); };
        ToolTipService.SetToolTip(border, $"{hidden.Count} more filter{(hidden.Count == 1 ? "" : "s")}");
        return border;
    }
}
