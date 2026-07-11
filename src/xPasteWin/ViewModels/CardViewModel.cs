using System;
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
    private bool _isFavicon;
    private bool _isFileIcon;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dq;

    public ClipboardItem Item { get; }

    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isCopied;

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
            _isFavicon = preview.IsFavicon;
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
        }
        if (_dq != null) _dq.TryEnqueue(Apply); else Apply();
    }

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(SelectionBrush));

    private static readonly Brush TransparentBrush = new SolidColorBrush(Colors.Transparent);

    /// <summary>Viền: màu accent khi được chọn (giống macOS), trong suốt khi không.</summary>
    public Brush SelectionBrush => IsSelected ? AccentBrush : TransparentBrush;

    public Guid Id => Item.Id;

    // --- Header ---
    public string Title
    {
        get
        {
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
    public Brush AccentBrush
    {
        get
        {
            var v = Visual();
            if (v is { HasAccent: true })
            {
                var a = v.AccentArgb;
                return new SolidColorBrush(Color.FromArgb(
                    (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a));
            }
            return new SolidColorBrush(Item.Type switch
            {
                ClipboardContentType.Text => Color.FromArgb(255, 0, 122, 255),
                ClipboardContentType.Url => Color.FromArgb(255, 26, 153, 77),
                ClipboardContentType.Image => Color.FromArgb(255, 175, 82, 222),
                ClipboardContentType.File => Color.FromArgb(255, 255, 149, 0),
                ClipboardContentType.Folder => Color.FromArgb(255, 0, 122, 255),
                _ => Color.FromArgb(255, 0, 122, 255),
            });
        }
    }

    private ImageSource? _sourceIcon;
    private bool _sourceIconLoaded;
    public ImageSource? SourceAppIcon
    {
        get
        {
            if (_sourceIconLoaded) return _sourceIcon;
            _sourceIconLoaded = true;
            var p = Visual()?.IconPath;
            if (p != null && File.Exists(p))
            {
                try { _sourceIcon = new BitmapImage(new Uri(p)); } catch { }
            }
            _sourceIcon ??= XPasteIcon; // không rõ app nguồn → dùng icon xPaste
            return _sourceIcon;
        }
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

    public Visibility ThumbVisibility => HasThumbnail ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextVisibility => (HasThumbnail || IsColor) ? Visibility.Collapsed : Visibility.Visible;
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
        get { _ = Thumbnail; return (Item.Type == ClipboardContentType.Url && !_isFavicon) ? Stretch.UniformToFill : Stretch.Uniform; }
    }
    public HorizontalAlignment ThumbAlign
    {
        get { _ = Thumbnail; return (_isFavicon || _isFileIcon) ? HorizontalAlignment.Center : HorizontalAlignment.Stretch; }
    }
    public VerticalAlignment ThumbVAlign
    {
        get { _ = Thumbnail; return (_isFavicon || _isFileIcon) ? VerticalAlignment.Center : VerticalAlignment.Stretch; }
    }
    // Favicon/icon: kích thước cố định nhỏ, căn giữa. og:image/ảnh: NaN (tự co) — bị chặn bởi
    // MaxWidth/MaxHeight = ActualWidth/ActualHeight THỰC của vùng nội dung (bind trong XAML), nên
    // ảnh lớn KHÔNG phình card / tràn layout mà không cần hardcode kích thước.
    public double ThumbFixedSize
    {
        get { _ = Thumbnail; return _isFileIcon ? 96 : _isFavicon ? 72 : double.NaN; }
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
                    return _linkTitle ?? $"{Item.Text?.Length ?? 0} characters";
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

    /// <summary>Nền footer: link có nền riêng; các loại khác dùng cùng nền nội dung cho liền mạch.</summary>
    public Brush FooterBackground =>
        Item.Type == ClipboardContentType.Url ? ThemeService.CardFooterUrlBrush : ThemeService.CardContentBrush;

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
