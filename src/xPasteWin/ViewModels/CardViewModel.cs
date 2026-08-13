using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using xPasteWin.Models;
using xPasteWin.Services;

namespace xPasteWin.ViewModels;

/// <summary>
/// Bọc một <see cref="ClipboardItem"/> để hiển thị trên card (giống ClipboardItemCard của macOS):
/// tiêu đề, thời gian tương đối, footer, màu accent theo loại, thumbnail ảnh, trạng thái chọn/copied.
/// </summary>
public sealed partial class CardViewModel : ObservableObject
{
    private readonly ClipboardStore _store;
    private ImageSource? _thumb;
    private bool _thumbLoaded;
    private string? _linkTitle;
    private bool _linkHasPageTitle;
    private bool _isFavicon;
    private bool _isLogo;
    private bool _isFileIcon;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dq;

    public ClipboardItem Item { get; }

    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isCopied;
    // Từ khoá đang tìm (free text) để tô nền vàng đoạn khớp trên card. PanelViewModel cập nhật khi search.
    [ObservableProperty] private string highlightTerm = "";

    // Badge phím tắt Ctrl+1..9 (hiện khi giữ Ctrl, giống ⌘N của macOS). Index = vị trí trong danh sách.
    [ObservableProperty] private int index;
    [ObservableProperty] private bool showBadge;
    [ObservableProperty] private bool badgePlain; // Ctrl+Shift → dán thô (hiện thêm glyph)

    partial void OnIndexChanged(int value) => RaiseBadge();
    partial void OnShowBadgeChanged(bool value) => RaiseBadge();
    partial void OnBadgePlainChanged(bool value) => OnPropertyChanged(nameof(BadgePlainVisibility));

    private void RaiseBadge()
    {
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(FooterBadgeVisibility));
        OnPropertyChanged(nameof(ColorBadgeVisibility));
        OnPropertyChanged(nameof(ImageBadgeVisibility));
        OnPropertyChanged(nameof(BadgePlainVisibility));
        OnPropertyChanged(nameof(FooterContentInset));
    }

    private bool BadgeOn => ShowBadge && Index < 9; // chỉ 9 card đầu có phím tắt
    /// <summary>Nhãn badge (1..9).</summary>
    public string BadgeText => (Index + 1).ToString();

    // 3 kiểu badge đúng như macOS: card có footer → số KHÔNG nền, màu phụ, thẳng hàng chữ "N characters";
    // ô màu → số KHÔNG nền, màu theo độ sáng swatch (ColorTextBrush); ảnh → pill nền mờ, màu chính.
    public Visibility FooterBadgeVisibility =>
        BadgeOn && !IsImage && !IsColor ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ColorBadgeVisibility =>
        BadgeOn && IsColor ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ImageBadgeVisibility =>
        BadgeOn && IsImage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgePlainVisibility =>
        BadgeOn && BadgePlain ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Chừa lề phải cho chữ footer để không đè lên badge khi giữ Ctrl (reflow như HStack của mac).</summary>
    public Thickness FooterContentInset =>
        FooterBadgeVisibility == Visibility.Visible ? new Thickness(0, 0, 26, 0) : new Thickness(0);

    public CardViewModel(ClipboardItem item, ClipboardStore store)
    {
        Item = item;
        _store = store;
        _dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (item.Type == ClipboardContentType.Url && !string.IsNullOrWhiteSpace(item.Text))
            _ = LoadLinkPreviewAsync(item.Text!);
    }

    private async System.Threading.Tasks.Task LoadLinkPreviewAsync(string url)
    {
        var preview = await LinkPreviewService.GetAsync(url);
        if (preview == null) return;
        void Apply()
        {
            _linkTitle = preview.Title;
            _linkHasPageTitle = preview.HasPageTitle;
            _isFavicon = preview.IsFavicon;
            _isLogo = preview.IsLogo;
            if (preview.ImagePath != null && File.Exists(preview.ImagePath))
            {
                try { _thumb = new BitmapImage(new Uri(preview.ImagePath)); _thumbLoaded = true; } catch { }
            }
            OnPropertyChanged(nameof(Thumbnail));
            OnPropertyChanged(nameof(HasThumbnail));
            OnPropertyChanged(nameof(ThumbVisibility));
            OnPropertyChanged(nameof(TextVisibility));
            OnPropertyChanged(nameof(FooterText));
            OnPropertyChanged(nameof(ThumbStretch));
            OnPropertyChanged(nameof(ThumbAlign));
            OnPropertyChanged(nameof(ThumbVAlign));
            OnPropertyChanged(nameof(ThumbFixedSize));
            // URL vừa có preview → bố cục footer/inset/fade/nền đổi theo.
            OnPropertyChanged(nameof(UrlHasPreview));
            OnPropertyChanged(nameof(UrlLineVisibility));
            OnPropertyChanged(nameof(FooterFontSize));
            OnPropertyChanged(nameof(FooterFontWeight));
            OnPropertyChanged(nameof(FooterHeight));
            OnPropertyChanged(nameof(FooterVisibility));
            OnPropertyChanged(nameof(FooterBackground));
            OnPropertyChanged(nameof(ContentInsetThickness));
            OnPropertyChanged(nameof(FadeVisibility));
            OnPropertyChanged(nameof(ContentBackdropBrush));
            // Có ảnh preview thì nội dung THÔI chảy dưới footer ⇒ rich preview (nền/chữ RTF-HTML) tắt
            // theo. Không phát các thuộc tính này thì card giữ nguyên nền rich cũ dưới ảnh mới.
            OnPropertyChanged(nameof(RtfVisibility));
            OnPropertyChanged(nameof(HtmlVisibility));
            OnPropertyChanged(nameof(CardSurfaceBrush));
            OnPropertyChanged(nameof(FooterTextBrush));
            OnPropertyChanged(nameof(BottomFadeBrush));
        }
        if (_dq != null) _dq.TryEnqueue(Apply); else Apply();
    }

    [ObservableProperty] private bool isHovered;

    // Đổi tên NGAY trên header (inline, giống macOS): TextBox hiện thay tiêu đề khi IsRenaming.
    [ObservableProperty] private bool isRenaming;
    [ObservableProperty] private string renameDraft = "";
    partial void OnIsRenamingChanged(bool value)
    {
        OnPropertyChanged(nameof(TitleVisibility));
        OnPropertyChanged(nameof(RenameVisibility));
    }
    public Visibility TitleVisibility => IsRenaming ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RenameVisibility => IsRenaming ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(RingBrush));
    partial void OnIsHoveredChanged(bool value)
    {
        OnPropertyChanged(nameof(RingBrush));
        OnPropertyChanged(nameof(PinVisibility));       // ẩn chỉ báo pin tĩnh khi hover (nút hover thay thế)
        OnPropertyChanged(nameof(HoverActionsVisibility));
    }

    private static readonly Brush TransparentBrush = new SolidColorBrush(Colors.Transparent);

    /// <summary>Viền: xanh accent khi được chọn HOẶC đang hover (giống macOS), trong suốt khi không.</summary>
    public Brush RingBrush =>
        IsSelected || IsHovered ? ThemeService.SelectionRingBrush : TransparentBrush;

    // --- Nút hover nổi (pin + xoá) ở góc trên phải, hiện khi hover (giống CardHoverActions của mac) ---
    public Visibility HoverActionsVisibility => IsHovered ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Callback do PanelViewModel gán để pin/xoá đi qua store + refresh danh sách.</summary>
    public Action? OnTogglePin;
    public Action? OnDelete;

    public string PinButtonGlyph => Item.IsPinned ? "" : ""; // Unpin / Pin
    public string PinButtonTooltip => Item.IsPinned ? "Unpin" : "Pin";
    // Pin: đỏ khi CHƯA ghim (màu báo trạng thái, đọc trên nền sáng/tối); đã ghim → tint theo nền.
    public Brush PinButtonBrush =>
        Item.IsPinned ? HoverIconBrush : new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A));

    /// <summary>Màu icon nút hover (trash + unpin) — tương phản với nền thực của card.</summary>
    public Brush HoverIconBrush
    {
        get
        {
            var c = SurfaceColor;
            double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            return new SolidColorBrush(lum < 0.5
                ? Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E));
        }
    }

    /// <summary>Cập nhật hiển thị nút/chỉ báo pin sau khi đổi trạng thái ghim.</summary>
    public void RaisePinState()
    {
        OnPropertyChanged(nameof(PinVisibility));
        OnPropertyChanged(nameof(PinButtonGlyph));
        OnPropertyChanged(nameof(PinButtonTooltip));
        OnPropertyChanged(nameof(PinButtonBrush));
    }

    public Guid Id => Item.Id;

    // --- Header ---
    public string Title
    {
        get
        {
            // Tên do người dùng đặt (rename) thắng mọi tiêu đề suy ra — điểm mấu chốt của "snippet".
            if (!string.IsNullOrEmpty(Item.Label)) return Item.Label!;
            if (DetectedColor() != null) return "Color";
            if (DetectedFilePath() is { } fp) return Directory.Exists(fp) ? "Folder" : "File";
            return Item.Type switch
            {
                ClipboardContentType.Text => "Text",
                ClipboardContentType.Url => "Link",
                ClipboardContentType.Image => "Image",
                ClipboardContentType.Folder => FilesCount == 1 ? "1 folder" : $"{FilesCount} folders",
                _ => FilesCount == 1 ? "1 file" : $"{FilesCount} files",
            };
        }
    }

    public string RelativeTime => Relative(Item.Timestamp);

    private SourceAppService.AppVisual? _visual;
    private bool _visualLoaded;
    private SourceAppService.AppVisual? Visual()
    {
        if (!_visualLoaded) { _visualLoaded = true; _visual = SourceAppService.GetVisual(Item.SourceApp); }
        return _visual;
    }

    // Màu accent header: trích từ màu chủ đạo của ICON app nguồn (giống macOS); nếu không có icon
    // thì fallback theo loại nội dung.
    private Color AccentColor
    {
        get
        {
            var v = Visual();
            if (v is { HasAccent: true })
            {
                var a = v.AccentArgb;
                return Color.FromArgb((byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a);
            }
            return Item.Type switch
            {
                ClipboardContentType.Text => Color.FromArgb(255, 0, 122, 255),
                ClipboardContentType.Url => Color.FromArgb(255, 26, 153, 77),
                ClipboardContentType.Image => Color.FromArgb(255, 175, 82, 222),
                ClipboardContentType.File => Color.FromArgb(255, 255, 149, 0),
                ClipboardContentType.Folder => Color.FromArgb(255, 0, 122, 255),
                _ => Color.FromArgb(255, 0, 122, 255),
            };
        }
    }

    public Brush AccentBrush => new SolidColorBrush(AccentColor);

    /// <summary>Header nhạt cần chữ tối. Ngưỡng 0.62 cao hơn mốc 0.5 giữa: chữ trắng vẫn đọc tốt trên
    /// các màu thương hiệu bão hoà (xanh Mail/Xcode) có luminance nhỉnh qua 0.5 (giống macOS isPaleColor).</summary>
    private bool IsPaleAccent
    {
        get
        {
            var c = AccentColor;
            double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            return lum > 0.62;
        }
    }

    /// <summary>Màu tiêu đề trên header: đen 78% nếu nền nhạt, trắng nếu nền đậm (macOS onAccent).</summary>
    public Brush OnAccentBrush => IsPaleAccent
        ? new SolidColorBrush(Color.FromArgb(0xC7, 0, 0, 0))
        : new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    /// <summary>Màu dòng phụ (thời gian) trên header = onAccent × 0.75 độ mờ.</summary>
    public Brush OnAccentSecondaryBrush => IsPaleAccent
        ? new SolidColorBrush(Color.FromArgb(0x95, 0, 0, 0))
        : new SolidColorBrush(Color.FromArgb(0xBF, 0xFF, 0xFF, 0xFF));

    /// <summary>URL người dùng đã copy (dòng phụ dưới tiêu đề trên card URL). Rỗng nếu không phải URL.</summary>
    public string UrlText => Item.Type == ClipboardContentType.Url ? (Item.Text ?? "") : "";
    public Visibility UrlLineVisibility =>
        Item.Type == ClipboardContentType.Url && UrlHasPreview && !string.IsNullOrEmpty(Item.Text)
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Cỡ chữ tiêu đề footer: card URL (có preview) to hơn cho dễ đọc; loại khác 12.</summary>
    public double FooterFontSize => UrlHasPreview ? 13 : 12;

    /// <summary>Card URL lấy được TIÊU ĐỀ THẬT của trang → in đậm dòng đó, tách hẳn khỏi URL xám nhạt
    /// ngay bên dưới. Tên miền suy ra (trang chặn bot / trả JSON) không phải tiêu đề nên để chữ thường.</summary>
    public Windows.UI.Text.FontWeight FooterFontWeight =>
        IsUrl && _linkHasPageTitle
            ? Microsoft.UI.Text.FontWeights.Bold
            : Microsoft.UI.Text.FontWeights.Normal;

    // ---------- Bố cục nội dung/footer (port từ ClipboardItemCard.swift của macOS) ----------
    private bool IsImage => Item.Type == ClipboardContentType.Image;
    private bool IsUrl => Item.Type == ClipboardContentType.Url;

    /// <summary>Card URL đã có preview (og:image hoặc favicon) → footer 52 hiện tiêu đề + URL, ảnh fill.</summary>
    private bool UrlHasPreview { get { _ = Thumbnail; return IsUrl && _thumb != null; } }

    /// <summary>Text/URL chảy XUỐNG DƯỚI footer với gradient mờ (text tan vào footer) — chỉ text thuần
    /// hoặc URL chưa có preview. Ảnh/ô màu/file icon KHÔNG chảy (trông như lỗi layout).</summary>
    private bool ContentFlowsUnderFooter =>
        (Item.Type == ClipboardContentType.Text && !IsColor && DetectedFilePath() == null) ||
        (IsUrl && !UrlHasPreview);

    /// <summary>Chiều cao strip footer: URL có preview = 52 (2 dòng), còn lại = 30.</summary>
    public double FooterHeight => UrlHasPreview ? 52 : 30;

    /// <summary>Chừa đáy vùng nội dung để nội dung "căn giữa" (file icon/favicon/ảnh link) dừng TRÊN footer;
    /// text-chảy-dưới, ảnh, ô màu thì fill hết (inset 0).</summary>
    public Thickness ContentInsetThickness =>
        new(0, 0, 0, ContentFlowsUnderFooter || IsImage || IsColor ? 0 : FooterHeight);

    /// <summary>Ảnh và ô màu tràn tới đáy → KHÔNG có strip footer (mac dùng pill nổi / swatch full).</summary>
    public Visibility FooterVisibility =>
        IsImage || IsColor ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Gradient mờ chỉ hiện với text chảy dưới footer.</summary>
    public Visibility FadeVisibility =>
        ContentFlowsUnderFooter ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Nền footer: text-chảy-dưới trong suốt (để lộ gradient); còn lại nền card đục.</summary>
    public Brush FooterBackground =>
        ContentFlowsUnderFooter ? TransparentBrush : ThemeService.CardContentBrush;

    /// <summary>Gradient từ trong suốt → màu BỀ MẶT card (trên→dưới) để text tan dần vào footer.</summary>
    public Brush BottomFadeBrush
    {
        get
        {
            var c = SurfaceColor;
            var g = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
            g.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, c.R, c.G, c.B), Offset = 0 });
            g.GradientStops.Add(new GradientStop { Color = c, Offset = 1 });
            return g;
        }
    }

    // ---------- Rich preview (render RTF: nền/màu/font/link) — port RichTextRenderer của macOS ----------
    private RichPreviewService.RichInfo? _rich;
    private bool _richChecked;
    private RichPreviewService.RichInfo? Rich()
    {
        if (!_richChecked) { _richChecked = true; _rich = RichPreviewService.Analyze(Item); }
        return _rich;
    }

    /// <summary>Card này render rich (RTF/HTML) thay cho text thường: text/URL chảy-dưới có rich hợp lệ.</summary>
    public bool HasRichPreview => ContentFlowsUnderFooter && Rich() != null;
    public Visibility RtfVisibility =>
        HasRichPreview && Rich()!.Kind == RichPreviewService.RichKind.Rtf ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HtmlVisibility =>
        HasRichPreview && Rich()!.Kind == RichPreviewService.RichKind.Html ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Chuỗi RTF để code-behind nạp vào RichEditBox (chỉ khi Kind==Rtf).</summary>
    public string? RtfContent => RtfVisibility == Visibility.Visible ? Rich()!.Rtf : null;
    /// <summary>Danh sách span HTML để code-behind dựng Inline cho RichTextBlock (chỉ khi Kind==Html).</summary>
    public IReadOnlyList<HtmlSpan>? HtmlSpans => HtmlVisibility == Visibility.Visible ? Rich()!.Html : null;

    /// <summary>Màu chữ mặc định cho run HTML không khai màu — tương phản với nền thực.</summary>
    public Brush RichDefaultTextBrush
    {
        get
        {
            var c = SurfaceColor;
            double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            return new SolidColorBrush(lum < 0.5
                ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1E));
        }
    }

    /// <summary>Màu nền trích từ RTF (vd nền đen Terminal); null = giữ nền card mặc định.</summary>
    private Color? RichFill
    {
        get
        {
            if (!HasRichPreview) return null;
            var a = Rich()!.FillArgb;
            return a is { } v ? Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v) : null;
        }
    }

    private Color SurfaceColor => RichFill ?? ((SolidColorBrush)ThemeService.CardContentBrush).Color;

    /// <summary>Màu nền THỰC phía sau nội dung card (nền rich nếu có, không thì nền card theo theme).
    /// Bộ dựng rich dùng nó để bỏ qua highlighter nền trùng màu — xem RichContentBuilder.AddBackground.</summary>
    public Color RichSurfaceColor => SurfaceColor;

    /// <summary>Nền bề mặt card (vùng nội dung): màu RTF nếu có, không thì nền card theo theme.</summary>
    public Brush CardSurfaceBrush =>
        RichFill is { } c ? new SolidColorBrush(c) : ThemeService.CardContentBrush;

    /// <summary>
    /// Nền capsule chứa hai nút pin/xoá nổi trên card.
    ///
    /// KHÔNG dùng PillBrush (màu cố định #2C2C2E tối / #FFFFFF sáng) như trước: ở theme sáng nền card
    /// cũng là #FFFFFF nên capsule trùng khít nền, chỉ còn cái viền mờ để nhìn ra. Ở đây lấy chính nền
    /// card (kể cả nền RTF của card rich) rồi đẩy đi một nấc — nền tối thì sáng lên, nền sáng thì tối
    /// đi — nên capsule luôn tách khỏi thứ nằm dưới nó dù card mang màu gì.
    /// </summary>
    public Brush HoverActionsBrush
    {
        get
        {
            var c = SurfaceColor;
            return new SolidColorBrush(IsDarkSurface(c) ? Mix(c, 0xFF, 0.16) : Mix(c, 0x00, 0.08));
        }
    }

    /// <summary>Viền capsule — cùng hướng tương phản với nền capsule, đẩy mạnh hơn một chút để mép
    /// capsule không tan vào ảnh khi nó nổi trên card ảnh.</summary>
    public Brush HoverActionsStrokeBrush
    {
        get
        {
            var c = SurfaceColor;
            return new SolidColorBrush(IsDarkSurface(c) ? Mix(c, 0xFF, 0.30) : Mix(c, 0x00, 0.16));
        }
    }

    private static bool IsDarkSurface(Color c) =>
        (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 < 0.5;

    /// <summary>Pha màu <paramref name="c"/> về phía <paramref name="toward"/> (0x00 đen / 0xFF trắng)
    /// theo tỉ lệ <paramref name="amount"/>. Giữ nguyên alpha đục.</summary>
    private static Color Mix(Color c, byte toward, double amount)
    {
        byte M(byte v) => (byte)Math.Round(v + (toward - v) * amount);
        return Color.FromArgb(0xFF, M(c.R), M(c.G), M(c.B));
    }

    /// <summary>Màu chữ footer/badge: card thường giữ màu phụ theo theme; card rich (có nền RTF) đổi
    /// sáng/tối theo nền thực để luôn đọc được (giống footerTextColor(on:) của mac).</summary>
    public Brush FooterTextBrush
    {
        get
        {
            if (RichFill is not { } c) return ThemeService.SecondaryTextBrush;
            double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            return new SolidColorBrush(lum < 0.5
                ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xB3, 0x00, 0x00, 0x00));
        }
    }

    /// <summary>Nền sau nội dung căn giữa: ảnh → ô cờ (alpha checkerboard); link-favicon → nền mờ; còn lại trong suốt.</summary>
    public Brush ContentBackdropBrush
    {
        get
        {
            if (IsImage)
            {
                var p = CheckerboardService.GetPng(ThemeService.IsDark);
                if (p != null)
                {
                    try
                    {
                        return new ImageBrush
                        {
                            ImageSource = new BitmapImage(new Uri(p)),
                            Stretch = Stretch.None,
                            AlignmentX = AlignmentX.Left,
                            AlignmentY = AlignmentY.Top,
                        };
                    }
                    catch { }
                }
                return TransparentBrush;
            }
            _ = Thumbnail; // đảm bảo _isFavicon/_isLogo đã set
            if (IsUrl && _thumb != null && (_isFavicon || _isLogo)) return ThemeService.CardMutedBrush;
            return TransparentBrush;
        }
    }

    // Kích thước pixel ảnh (pill nổi) — đọc header ảnh 1 lần, không giải mã toàn bộ.
    private string? _pixelDim;
    private bool _pixelChecked;
    private void EnsurePixel()
    {
        if (_pixelChecked) return;
        _pixelChecked = true;
        if (!IsImage) return;
        var p = _store.ImagePath(Item.Id);
        if (p == null || !File.Exists(p)) return;
        try
        {
            using var s = File.OpenRead(p);
            using var img = System.Drawing.Image.FromStream(s, false, false);
            _pixelDim = $"{img.Width} × {img.Height}";
        }
        catch { }
    }

    public string PixelDimText { get { EnsurePixel(); return _pixelDim ?? ""; } }
    public Visibility PillVisibility =>
        IsImage && !string.IsNullOrEmpty(PixelDimText) ? Visibility.Visible : Visibility.Collapsed;
    public Brush PillBrush => ThemeService.PillBrush;

    private ImageSource? _sourceIcon;
    private bool _sourceIconLoaded;
    public ImageSource? SourceAppIcon
    {
        get
        {
            if (_sourceIconLoaded) return _sourceIcon;
            _sourceIconLoaded = true;
            var p = Visual()?.IconPath;
            if (p != null && File.Exists(p)) _sourceIcon = SharedIcon(p);
            _sourceIcon ??= XPasteIcon; // không rõ app nguồn → dùng icon xPaste
            return _sourceIcon;
        }
    }

    // Icon app nguồn giờ là ảnh 256px: giải mã riêng cho từng card sẽ tốn bộ nhớ và CPU vô ích khi
    // nhiều card cùng một app. Dùng chung một BitmapImage cho mỗi đường dẫn, và ép WIC giải mã sẵn ở
    // 144px (36pt × 4, đủ cho cả màn 400%) — thu nhỏ bằng WIC nét hơn để GPU kéo thẳng từ 256px.
    private static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static ImageSource? SharedIcon(string path)
    {
        if (IconCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var img = new BitmapImage { DecodePixelType = DecodePixelType.Physical, DecodePixelWidth = 144 };
            img.UriSource = new Uri(path);
            IconCache[path] = img;
            return img;
        }
        catch { return null; }
    }

    public Visibility SourceIconVisibility =>
        SourceAppIcon != null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Tên app nguồn (tooltip icon); không rõ → "xPaste".</summary>
    public string SourceAppName
    {
        get
        {
            if (!string.IsNullOrEmpty(Item.SourceApp))
            {
                try { return Path.GetFileNameWithoutExtension(Item.SourceApp!); } catch { }
            }
            return "xPaste";
        }
    }

    // Icon xPaste dùng khi không rõ app nguồn (nạp 1 lần, trên UI thread).
    private static ImageSource? _xpasteIcon;
    private static bool _xpasteIconTried;
    private static ImageSource? XPasteIcon
    {
        get
        {
            if (_xpasteIconTried) return _xpasteIcon;
            _xpasteIconTried = true;
            var p = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.png");
            if (File.Exists(p)) { try { _xpasteIcon = new BitmapImage(new Uri(p)); } catch { } }
            return _xpasteIcon;
        }
    }

    /// <summary>Đường dẫn PNG icon app nguồn (để dựng drop shadow bám theo hình icon); fallback icon xPaste.</summary>
    public string? SourceAppIconPath
    {
        get
        {
            var p = Visual()?.IconPath;
            if (p != null) return p;
            var fb = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.png");
            return File.Exists(fb) ? fb : null;
        }
    }

    // --- Content ---
    public string PreviewText => Item.DisplayText;

    public bool HasThumbnail => Thumbnail != null;

    public Visibility ThumbVisibility => HasThumbnail && !HasFileText ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextVisibility =>
        (HasThumbnail || IsColor || HasRichPreview || HasFileText) ? Visibility.Collapsed : Visibility.Visible;

    // --- Preview nội dung file text trên card (đọc ~8KB đầu; giống macOS) ---
    private string? _fileText;
    private bool _fileTextChecked;
    public string? FileTextPreview
    {
        get
        {
            if (_fileTextChecked) return _fileText;
            _fileTextChecked = true;
            var p = TextFilePath();
            if (p != null) _fileText = TextFileReader.ReadHead(p, TextFileReader.CardHeadBytes)?.TrimEnd();
            return _fileText;
        }
    }
    public bool HasFileText => !string.IsNullOrEmpty(FileTextPreview);
    public Visibility FileTextVisibility => HasFileText ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Đường dẫn file văn bản của item (file đơn hoặc text là đường dẫn); null nếu không phải file text.</summary>
    private string? TextFilePath()
    {
        string? p = Item.Type == ClipboardContentType.File && Item.FilePaths is { Length: 1 } ? Item.FilePaths[0]
                  : Item.Type == ClipboardContentType.Text ? DetectedFilePath() : null;
        if (p != null && !Directory.Exists(p) && TextFileReader.IsTextFile(p)) return p;
        return null;
    }
    public Visibility PinVisibility => Item.IsPinned ? Visibility.Visible : Visibility.Collapsed;

    // --- Ô màu (#hex/rgb/hsl) ---
    private Color? _detectedColor;
    private bool _colorChecked;
    private Color? DetectedColor()
    {
        if (_colorChecked) return _detectedColor;
        _colorChecked = true;
        if (Item.Type == ClipboardContentType.Text)
            _detectedColor = ColorParser.Parse(Item.Text);
        return _detectedColor;
    }

    public bool IsColor => DetectedColor() != null;
    public Visibility ColorVisibility => IsColor ? Visibility.Visible : Visibility.Collapsed;
    public Brush ColorSwatchBrush =>
        DetectedColor() is { } c ? new SolidColorBrush(c) : TransparentBrush;
    public Brush ColorTextBrush =>
        DetectedColor() is { } c && ColorParser.IsLight(c)
            ? new SolidColorBrush(Color.FromArgb(0xA6, 0, 0, 0))
            : new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));

    // Hiển thị thumbnail: og:image phủ đầy (scaledToFill); favicon 72×72 và icon file/folder ~96 căn giữa.
    // Đọc _ = Thumbnail trước để _isFavicon/_isFileIcon được set (thứ tự bind không đảm bảo).
    public Stretch ThumbStretch
    {
        // Chỉ og:image LỚN (không phải favicon/logo/icon) mới phủ đầy; còn lại fit để không phóng to vỡ nét.
        get { _ = Thumbnail; return (Item.Type == ClipboardContentType.Url && !_isFavicon && !_isLogo) ? Stretch.UniformToFill : Stretch.Uniform; }
    }
    public HorizontalAlignment ThumbAlign
    {
        get { _ = Thumbnail; return (_isFavicon || _isLogo || _isFileIcon) ? HorizontalAlignment.Center : HorizontalAlignment.Stretch; }
    }
    public VerticalAlignment ThumbVAlign
    {
        get { _ = Thumbnail; return (_isFavicon || _isLogo || _isFileIcon) ? VerticalAlignment.Center : VerticalAlignment.Stretch; }
    }
    // Favicon 72 / logo 120 / icon file 120: kích thước cố định, căn giữa. og:image/ảnh: NaN (tự co) — bị
    // chặn bởi MaxWidth/MaxHeight = kích thước THỰC của vùng nội dung nên không phình card / tràn layout.
    public double ThumbFixedSize
    {
        get { _ = Thumbnail; return _isFileIcon ? 120 : _isLogo ? 120 : _isFavicon ? 72 : double.NaN; }
    }

    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbLoaded) return _thumb;
            _thumbLoaded = true;
            string? path = Item.Type switch
            {
                ClipboardContentType.Image => _store.ImagePath(Item.Id),           // ảnh: file jpg đã lưu
                ClipboardContentType.Text => ImageFilePathText(),                  // text là đường dẫn ảnh
                ClipboardContentType.File or ClipboardContentType.Folder => FirstImageFile(), // file copy là ảnh
                _ => null,
            };
            if (path != null && File.Exists(path))
            {
                try { _thumb = new BitmapImage(new Uri(path)); } catch { _thumb = null; }
            }
            // Không phải ảnh nhưng là file/folder (hoặc text là đường dẫn tồn tại) → icon hệ thống, hiện nhỏ giữa card.
            if (_thumb == null)
            {
                var iconPath = FileIconPath();
                if (iconPath != null && File.Exists(iconPath))
                {
                    try { _thumb = new BitmapImage(new Uri(iconPath)); _isFileIcon = true; } catch { _thumb = null; }
                }
            }
            return _thumb;
            // (URL: _thumb được gán bất đồng bộ qua LoadLinkPreviewAsync)
        }
    }

    /// <summary>Đường dẫn PNG icon hệ thống cho file/folder (item file/folder, hoặc text là đường dẫn).</summary>
    private string? FileIconPath()
    {
        string? target = Item.Type switch
        {
            ClipboardContentType.File or ClipboardContentType.Folder => Item.FilePaths?.FirstOrDefault(),
            ClipboardContentType.Text => DetectedFilePath(),
            _ => null,
        };
        return target != null ? FileIconService.GetIconPng(target) : null;
    }

    private static readonly string[] ImageExts =
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff" };
    private string? _imagePathText;
    private bool _imagePathChecked;

    private static bool IsImageExt(string p) =>
        ImageExts.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>File copy (từ Explorer) mà file đầu tiên là ảnh → trả đường dẫn để hiện thumbnail.</summary>
    private string? FirstImageFile()
    {
        var p = Item.FilePaths?.FirstOrDefault();
        if (p != null && IsImageExt(p))
        {
            try { if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    /// <summary>Nếu item là Text và nội dung là đường dẫn tới một file ảnh tồn tại → trả đường dẫn đó.</summary>
    private string? ImageFilePathText()
    {
        if (_imagePathChecked) return _imagePathText;
        _imagePathChecked = true;
        if (Item.Type == ClipboardContentType.Text && Item.Text is { } t)
        {
            var p = t.Trim().Trim('"');
            if (p.Length is > 3 and < 260 &&
                ImageExts.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                try { if (File.Exists(p)) _imagePathText = p; } catch { }
            }
        }
        return _imagePathText;
    }

    private string? _filePathText;
    private bool _filePathChecked;

    /// <summary>Nếu item là Text và nội dung là đường dẫn Windows (rooted) tới file/folder TỒN TẠI → trả đường dẫn.
    /// Tương đương detectedFilePath của macOS (đổi tiêu đề "File"/"Folder" + hiện icon).</summary>
    private string? DetectedFilePath()
    {
        if (_filePathChecked) return _filePathText;
        _filePathChecked = true;
        if (Item.Type == ClipboardContentType.Text && Item.Text is { } t)
        {
            var p = t.Trim().Trim('"');
            if (p.Length is > 3 and < 260 && !p.Contains('\n') && !p.Contains('\r'))
            {
                try
                {
                    if (Path.IsPathFullyQualified(p) && (File.Exists(p) || Directory.Exists(p)))
                        _filePathText = p;
                }
                catch { }
            }
        }
        return _filePathText;
    }

    // --- Footer ---
    public string FooterText
    {
        get
        {
            // File/folder và text-là-đường-dẫn: footer hiện ĐƯỜNG DẪN (giống macOS fileFooter).
            if (DetectedFilePath() is { } fp) return fp;
            switch (Item.Type)
            {
                case ClipboardContentType.Url:
                    // Chưa lấy được gì thì vẫn hiện TÊN MIỀN — "147 characters" trên một card Link là
                    // thông tin vô dụng, còn tên miền luôn nhận ra được ngay.
                    return _linkTitle ?? UrlHost() ?? $"{Item.Text?.Length ?? 0} characters";
                case ClipboardContentType.Image:
                    return $"{Math.Max(1, (Item.ImageSize ?? 0) / 1024)} KB";
                case ClipboardContentType.Folder:
                case ClipboardContentType.File:
                    return Item.FilePaths?.FirstOrDefault()
                        ?? (Item.Type == ClipboardContentType.Folder
                            ? (FilesCount == 1 ? "1 folder" : $"{FilesCount} folders")
                            : (FilesCount == 1 ? "1 file" : $"{FilesCount} files"));
                default:
                    var img = ImageFilePathText();
                    return img != null ? Path.GetFileName(img) : $"{Item.Text?.Length ?? 0} characters";
            }
        }
    }

    /// <summary>Tên miền của URL đã copy (bỏ "www."); null nếu không phân tích được.</summary>
    private string? UrlHost()
    {
        if (!IsUrl || string.IsNullOrWhiteSpace(Item.Text)) return null;
        if (!Uri.TryCreate(Item.Text!.Trim(), UriKind.Absolute, out var u)) return null;
        var h = u.Host;
        if (string.IsNullOrEmpty(h)) return null;
        return h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
    }

    private int FilesCount => Item.FilePaths?.Length ?? 0;

    /// <summary>Footer căn giữa, trừ link/file/folder/đường-dẫn căn trái (hiện chuỗi dài).</summary>
    public HorizontalAlignment FooterAlignment =>
        Item.Type is ClipboardContentType.Url or ClipboardContentType.File or ClipboardContentType.Folder
            || DetectedFilePath() != null
            ? HorizontalAlignment.Left : HorizontalAlignment.Center;

    // Màu bề mặt card theo theme (sáng/tối) — lấy từ ThemeService để đồng bộ toàn app.
    public Brush CardContentBrush => ThemeService.CardContentBrush;
    public Brush PrimaryTextBrush => ThemeService.PrimaryTextBrush;
    public Brush SecondaryTextBrush => ThemeService.SecondaryTextBrush;

    /// <summary>Cập nhật các thuộc tính phụ thuộc trạng thái item (gọi sau khi pin/đổi).</summary>
    public void NotifyChanged()
    {
        OnPropertyChanged(nameof(PinVisibility));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(RelativeTime));
    }

    private static string Relative(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalSeconds < 5) return "just now";
        if (d.TotalMinutes < 1) return $"{(int)d.TotalSeconds}s ago";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return t.LocalDateTime.ToString("MMM d");
    }
}
