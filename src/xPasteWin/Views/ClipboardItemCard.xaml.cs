using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using xPasteWin.Services;
using xPasteWin.ViewModels;

namespace xPasteWin.Views;

public sealed partial class ClipboardItemCard : UserControl
{
    private CardViewModel? _vm;

    public ClipboardItemCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            // Đổi item (card tái dùng): gỡ theo dõi VM cũ, gắn VM mới.
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as CardViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
            SetupIconShadow();
            SetupRichPreview();
            ApplyHighlights();
            SetCaretBlink(_vm?.IsRenaming == true); // card tái dùng: dừng nhấp nháy nếu item mới không đổi tên
        };

        // Hover: hiện viền + nút nổi (pin/xoá) giống macOS.
        PointerEntered += (_, _) => SetHover(true);
        PointerExited += (_, _) => SetHover(false);
        PinButton.Click += (_, _) => (DataContext as CardViewModel)?.OnTogglePin?.Invoke();
        DeleteButton.Click += (_, _) => (DataContext as CardViewModel)?.OnDelete?.Invoke();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CardViewModel.HighlightTerm)
            or nameof(CardViewModel.PreviewText) or nameof(CardViewModel.FooterText))
            ApplyHighlights();
        else if (e.PropertyName == nameof(CardViewModel.IsRenaming))
            SetCaretBlink(_vm?.IsRenaming == true);
    }

    // Nhấp nháy caret khi đổi tên (thay cho caret của TextBox — ta không dùng TextBox nữa).
    private Microsoft.UI.Xaml.DispatcherTimer? _caretTimer;
    private void SetCaretBlink(bool on)
    {
        if (on)
        {
            RenameCaret.Opacity = 1;
            _caretTimer ??= CreateCaretTimer();
            _caretTimer.Start();
        }
        else
        {
            _caretTimer?.Stop();
            RenameCaret.Opacity = 1;
        }
    }
    private Microsoft.UI.Xaml.DispatcherTimer CreateCaretTimer()
    {
        var t = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        t.Tick += (_, _) => RenameCaret.Opacity = RenameCaret.Opacity > 0.5 ? 0 : 1;
        return t;
    }

    private void SetHover(bool value)
    {
        if (DataContext is CardViewModel vm) vm.IsHovered = value;
    }

    /// <summary>Tô nền vàng đoạn khớp từ khoá tìm kiếm trên body + footer (giống SearchHighlight của mac).</summary>
    private void ApplyHighlights()
    {
        var term = _vm?.HighlightTerm ?? "";
        ApplyHighlight(BodyText, term);
        ApplyHighlight(FooterTextBlock, term);
        ApplyHighlight(UrlTextBlock, term);
        // Card render HTML (vd nội dung copy từ Chrome) → tô nền per-run + từ khoá trên RichTextBlock.
        if (_vm?.HtmlSpans is { } spans)
            RichContentBuilder.HighlightHtml(HtmlBox, spans, term,
                ThemeService.SearchHighlightBg, ThemeService.SearchHighlightDarkText,
                _vm.RichSurfaceColor);
    }

    private static void ApplyHighlight(TextBlock tb, string term)
    {
        try { tb.TextHighlighters.Clear(); } catch { return; }
        if (string.IsNullOrEmpty(term) || RichContentBuilder.RangeType == null) return;
        var text = tb.Text;
        if (string.IsNullOrEmpty(text)) return;

        var hl = new TextHighlighter { Background = new SolidColorBrush(ThemeService.SearchHighlightBg) };
        if (ThemeService.SearchHighlightDarkText) hl.Foreground = new SolidColorBrush(Colors.Black);

        int idx = 0, count = 0;
        while ((idx = text.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (RichContentBuilder.AddRange(hl, idx, term.Length)) count++;
            idx += term.Length;
        }
        if (count > 0) { try { tb.TextHighlighters.Add(hl); } catch { } }
    }

    /// <summary>Dựng rich preview để hiện đúng nền/màu/font/link của nội dung gốc: RTF (Terminal/Word…)
    /// nạp vào RichEditBox; HTML (web/editor) dựng Inline cho RichTextBlock. Chạy lại mỗi khi tái dùng card.</summary>
    private void SetupRichPreview()
    {
        var vm = DataContext as CardViewModel;

        // RTF → RichEditBox; HTML → RichTextBlock (dựng qua helper dùng chung với preview).
        RichContentBuilder.ApplyRtf(RichBox, vm?.RtfContent);

        HtmlBox.Blocks.Clear();
        if (vm?.HtmlSpans is { } spans)
            RichContentBuilder.PopulateHtml(HtmlBox, spans, vm.RichDefaultTextBrush, vm.RichSurfaceColor);
    }

    // Surface của icon dùng làm mask bóng — nạp MỘT lần cho mỗi app. Nhiều card thường cùng một app
    // nguồn; nạp lại theo từng card là decode lại cùng một file PNG hàng chục lần.
    private static readonly Dictionary<string, LoadedImageSurface> IconSurfaces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dựng drop shadow bám ĐÚNG HÌNH icon app nguồn: mask là alpha của chính ảnh icon, nên icon tròn
    /// đổ bóng tròn, icon dị hình đổ bóng theo đường viền thật — không phải bóng của một khung vuông.
    /// Đây là thứ tách icon khỏi nền header (vốn lấy màu từ chính icon đó) mà không cần khung bao.
    ///
    /// Bóng mềm và nhạt (blur 9, đục 30%, lệch 1.5px). Bản đầu để blur 7 / đục 55% — đủ đậm để thành
    /// một quầng đen rõ rệt, trên nền accent trông như vết bẩn hơn là chiều sâu.
    /// </summary>
    private void SetupIconShadow()
    {
        var path = (DataContext as CardViewModel)?.SourceAppIconPath;
        if (string.IsNullOrEmpty(path))
        {
            ElementCompositionPreview.SetElementChildVisual(IconShadowHost, null);
            return;
        }
        try
        {
            if (!IconSurfaces.TryGetValue(path, out var surface))
            {
                surface = LoadedImageSurface.StartLoadFromUri(new Uri(path));
                IconSurfaces[path] = surface;
            }

            var compositor = ElementCompositionPreview.GetElementVisual(IconShadowHost).Compositor;
            var mask = compositor.CreateSurfaceBrush(surface);
            mask.Stretch = CompositionStretch.Uniform;   // khớp Stretch của Image để bóng trùng khít icon

            var shadow = compositor.CreateDropShadow();
            shadow.Mask = mask;
            shadow.BlurRadius = 9;
            shadow.Opacity = 0.30f;
            shadow.Color = Colors.Black;
            shadow.Offset = new Vector3(0, 1.5f, 0);

            var sprite = compositor.CreateSpriteVisual();
            sprite.Size = new Vector2(34, 34);
            sprite.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(IconShadowHost, sprite);
        }
        catch
        {
            ElementCompositionPreview.SetElementChildVisual(IconShadowHost, null);
        }
    }
}
