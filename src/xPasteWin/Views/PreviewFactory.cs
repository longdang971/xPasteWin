using System;
using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using xPasteWin.Models;
using xPasteWin.Services;
using xPasteWin.ViewModels;

namespace xPasteWin.Views;

/// <summary>
/// Nội dung cửa sổ preview (Quick Look) — tái tạo ItemPreviewWindow của macOS:
/// header (đóng + loại), nội dung theo loại (text cuộn/chọn được, ảnh lớn, danh sách file,
/// URL render bằng WebView2), footer thống kê.
/// </summary>
internal static class PreviewFactory
{
    // Màu theo theme (sáng/tối) từ ThemeService.
    private static Brush PanelBg => ThemeService.PreviewPanelBg;
    private static Brush ContentBg => ThemeService.PreviewContentBg;
    private static Brush Divider => ThemeService.PreviewDivider;
    private static Brush Secondary => ThemeService.SecondaryTextBrush;

    public static FrameworkElement Build(CardViewModel card, PanelViewModel vm, Action onClose, Action onNavigateAway, out WebView2? webView)
    {
        webView = null;
        var item = card.Item;
        bool isUrl = item.Type == ClipboardContentType.Url;

        // Nền trong suốt để lộ kính acrylic ở header/footer/mép; vùng nội dung có nền riêng để dễ đọc.
        var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: nút đóng tròn (trái) · tiêu đề loại · nút copy (phải)
        var header = new Grid { Padding = new Thickness(10, 8, 10, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var close = IconButton("", 12); // ChromeClose
        ToolTipService.SetToolTip(close, "Close");
        close.Click += (_, _) => onClose();
        Grid.SetColumn(close, 0);
        header.Children.Add(close);

        var titleBlock = new TextBlock
        {
            Text = TypeTitle(item), FontSize = 13, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        };
        Grid.SetColumn(titleBlock, 1);
        header.Children.Add(titleBlock);

        var copy = IconButton("", 14); // Copy
        ToolTipService.SetToolTip(copy, "Copy");
        copy.Click += (_, _) => vm.Copy(item);
        Grid.SetColumn(copy, 2);
        header.Children.Add(copy);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var topDiv = new Border { Height = 1, Background = Divider, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetRow(topDiv, 0);
        root.Children.Add(topDiv);

        // Nội dung
        var content = BuildContent(item, card, vm, ref webView);
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        // Footer thống kê. URL: hiện link + nút "Open in <Browser>" (giống macOS); loại khác: 1 dòng chữ.
        var footer = new Grid { Padding = new Thickness(12, 7, 12, 7) };
        if (isUrl && Uri.TryCreate(item.Text, UriKind.Absolute, out var footerUri))
        {
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var urlText = new TextBlock
            {
                Text = footerUri.AbsoluteUri, FontSize = 11, Foreground = Secondary,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(urlText, 0);
            var open = new Button
            {
                Content = $"Open in {xPasteWin.Services.DefaultBrowser.Name()}",
                FontSize = 11, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0),
            };
            open.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = footerUri.AbsoluteUri, UseShellExecute = true });
                }
                catch { }
                onNavigateAway(); // đã mở link ở trình duyệt → đóng preview + panel
            };
            Grid.SetColumn(open, 1);
            footer.Children.Add(urlText);
            footer.Children.Add(open);
        }
        else
        {
            footer.Children.Add(new TextBlock { Text = FooterStats(item), FontSize = 11, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
        }
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        var botDiv = new Border { Height = 1, Background = Divider, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(botDiv, 2);
        root.Children.Add(botDiv);

        // Bọc trong Border bo góc 14 + viền mảnh (khớp bo góc cửa sổ) — trông như popover macOS.
        // Nền trong suốt để kính acrylic của cửa sổ hiện xuyên qua.
        return new Border
        {
            Width = isUrl ? 560 : 420,
            Height = isUrl ? 440 : 340,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeService.SettingsCardStroke,
            // Neo lên trên: dải "mỏ" ở đáy cửa sổ để trống → kính hiện qua thành mũi tên chỉ xuống.
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = root,
        };
    }

    /// <summary>Nút icon nhỏ (đóng/copy) — nền trong suốt, bo góc, đổi nền khi hover (mặc định của Button).</summary>
    private static Button IconButton(string glyph, double size) => new Button
    {
        Content = new FontIcon { Glyph = glyph, FontSize = size, Foreground = Secondary },
        Background = new SolidColorBrush(Colors.Transparent),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(7, 5, 7, 5),
        CornerRadius = new CornerRadius(7),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static FrameworkElement BuildContent(ClipboardItem item, CardViewModel card, PanelViewModel vm, ref WebView2? webView)
    {
        switch (item.Type)
        {
            case ClipboardContentType.Url:
                var wv = new WebView2();
                webView = wv; // trả ra để PreviewWindow Close() khi đóng (tránh rò msedgewebview2.exe)
                if (Uri.TryCreate(item.Text, UriKind.Absolute, out var uri))
                {
                    try { wv.Source = uri; } catch { }
                }
                return wv;

            case ClipboardContentType.Image:
                return new Grid
                {
                    Background = ContentBg,
                    Children =
                    {
                        new Image
                        {
                            Source = card.Thumbnail,
                            Stretch = Stretch.Uniform,
                            Margin = new Thickness(12),
                        }
                    }
                };

            case ClipboardContentType.File:
            case ClipboardContentType.Folder:
                // File text đơn → hiện NỘI DUNG (monospace, ~256KB), chọn được — giống macOS.
                if (item.Type == ClipboardContentType.File && item.FilePaths is { Length: 1 }
                    && TextFileReader.IsTextFile(item.FilePaths[0]))
                {
                    var content = TextFileReader.ReadHead(item.FilePaths[0], TextFileReader.PreviewHeadBytes);
                    if (!string.IsNullOrEmpty(content))
                        return new ScrollViewer
                        {
                            Background = ContentBg,
                            Content = new TextBlock
                            {
                                Text = content, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                                Foreground = ThemeService.PrimaryTextBrush, IsTextSelectionEnabled = true,
                                TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(14),
                            },
                        };
                }
                var list = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
                foreach (var p in item.FilePaths ?? Array.Empty<string>())
                    list.Children.Add(FileRow(p));
                return new ScrollViewer { Background = ContentBg, Content = list };

            default: // Text
                // Có RTF/HTML → render giữ nguyên nền/màu/font/link giống card (vd Terminal nền đen).
                var rich = RichPreviewService.Analyze(item);
                if (rich != null) return BuildRichPreview(rich);
                return new ScrollViewer
                {
                    Background = ContentBg,
                    Content = new TextBlock
                    {
                        Text = item.DisplayText,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        Margin = new Thickness(14),
                        FontSize = 13,
                    }
                };
        }
    }

    /// <summary>Nội dung preview rich (toàn bộ, cuộn được): RTF → RichEditBox; HTML → RichTextBlock.</summary>
    private static FrameworkElement BuildRichPreview(RichPreviewService.RichInfo rich)
    {
        Color? fill = rich.FillArgb is { } a ? RichContentBuilder.ToColor(a) : null;
        Brush bg = fill is { } c ? new SolidColorBrush(c) : ContentBg;

        if (rich.Kind == RichPreviewService.RichKind.Rtf)
        {
            var box = new RichEditBox
            {
                IsReadOnly = true,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14),
                IsSpellCheckEnabled = false,
                TextWrapping = TextWrapping.Wrap,
            };
            RichContentBuilder.ApplyRtf(box, rich.Rtf);
            return new Grid { Background = bg, Children = { box } };
        }

        var rtb = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14),
            IsTextSelectionEnabled = true,
        };
        RichContentBuilder.PopulateHtml(rtb, rich.Html, DefaultFg(fill),
            fill ?? ((SolidColorBrush)ContentBg).Color);
        return new ScrollViewer { Background = bg, Content = rtb };
    }

    /// <summary>Màu chữ mặc định cho HTML run không khai màu — tương phản với nền preview.</summary>
    private static Brush DefaultFg(Color? fill)
    {
        if (fill is not { } c) return ThemeService.PrimaryTextBrush;
        double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        return new SolidColorBrush(lum < 0.5 ? Colors.White : Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1E));
    }

    private static FrameworkElement FileRow(string path)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock
        {
            Text = Path.GetFileName(path.TrimEnd('\\', '/')),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        };
        Grid.SetColumn(name, 0);
        var reveal = new Button { Content = "Reveal", FontSize = 11, Padding = new Thickness(8, 2, 8, 2) };
        reveal.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
            }
            catch { }
        };
        Grid.SetColumn(reveal, 1);
        row.Children.Add(name);
        row.Children.Add(reveal);
        return row;
    }

    private static string TypeTitle(ClipboardItem item) => item.Type switch
    {
        ClipboardContentType.Url => "Link",
        ClipboardContentType.Image => "Image",
        ClipboardContentType.File => "File",
        ClipboardContentType.Folder => "Folder",
        _ => "Text",
    };

    private static string FooterStats(ClipboardItem item)
    {
        switch (item.Type)
        {
            case ClipboardContentType.Url:
                return item.Text ?? "";
            case ClipboardContentType.Image:
                return item.ImageWidth is { } iw && item.ImageHeight is { } ih
                    ? $"{iw} × {ih}"
                    : $"{Math.Max(1, (item.ImageSize ?? 0) / 1024)} KB";
            case ClipboardContentType.File:
                return (item.FilePaths?.Length ?? 0) == 1 ? "1 file" : $"{item.FilePaths?.Length ?? 0} files";
            case ClipboardContentType.Folder:
                return (item.FilePaths?.Length ?? 0) == 1 ? "1 folder" : $"{item.FilePaths?.Length ?? 0} folders";
            default:
                var t = item.Text ?? "";
                int words = t.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
                int lines = t.Length == 0 ? 0 : t.Split('\n').Length;
                return $"{t.Length} characters · {words} words · {lines} lines";
        }
    }
}

