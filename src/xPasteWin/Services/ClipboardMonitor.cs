using System;
using System.Linq;
using System.Threading.Tasks;
using xPasteWin.Interop;
using xPasteWin.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace xPasteWin.Services;

public sealed class ClipboardMonitor
{
    private readonly MessageWindow _msg;
    private readonly ISettings _settings;
    private DateTime _skipUntil = DateTime.MinValue;

    public event Action<ClipboardItem>? ItemCaptured;

    public ClipboardMonitor(MessageWindow msg, ISettings settings)
    {
        _msg = msg;
        _settings = settings;
    }

    public void Start()
    {
        Win32.AddClipboardFormatListener(_msg.Handle);
        _msg.ClipboardUpdated += OnClipboardUpdated;
    }

    public void Stop()
    {
        _msg.ClipboardUpdated -= OnClipboardUpdated;
        Win32.RemoveClipboardFormatListener(_msg.Handle);
    }

    /// <summary>
    /// Bỏ qua các thay đổi clipboard trong ~600ms tới — dùng khi app tự ghi clipboard (copy/paste).
    /// Dùng cửa sổ thời gian vì SetContent + Flush có thể phát nhiều WM_CLIPBOARDUPDATE.
    /// </summary>
    public void MarkNextChangeAsOwn() => _skipUntil = DateTime.UtcNow.AddMilliseconds(600);

    private async void OnClipboardUpdated()
    {
        if (DateTime.UtcNow < _skipUntil) return;
        // Privacy: bỏ qua nội dung mật/tạm thời (format chuẩn của Windows / password manager).
        if (_settings.Get("ignoreConfidentialContent", true) &&
            Win32.IsClipboardFormatPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
        if (_settings.Get("ignoreTransientContent", true) &&
            Win32.ClipboardFormatDwordIsZero("CanIncludeInClipboardHistory")) return;

        // Bắt app nguồn NGAY (trước await): GetClipboardOwner trỏ tới app vừa copy.
        var sourceExe = SourceAppService.GetClipboardSourceExe();
        // Privacy: bỏ qua app trong danh sách ignore.
        if (sourceExe != null)
        {
            var ignored = _settings.Get("ignoredApps", Array.Empty<string>());
            if (ignored.Any(p => string.Equals(p, sourceExe, StringComparison.OrdinalIgnoreCase))) return;
        }
        try
        {
            var item = await CaptureAsync();
            if (item == null) return;

            // Never Save Matching Text: bỏ qua nội dung khớp pattern/regex người dùng đặt.
            if (item.Text != null &&
                ExclusionRules.ShouldExclude(item.Text, _settings.Get("excludePatterns", Array.Empty<string>())))
                return;

            // OCR ảnh → lưu chữ đọc được để tìm kiếm ảnh theo nội dung.
            if (item.Type == ClipboardContentType.Image && _settings.Get("ocrEnabled", true))
                item.OcrText = await OcrService.RecognizeAsync(item.ImageData);

            item.SourceApp = sourceExe;
            ItemCaptured?.Invoke(item);
        }
        catch { /* clipboard bận / định dạng lạ: bỏ qua, không crash */ }
    }

    private static async Task<ClipboardItem?> CaptureAsync()
    {
        var data = Clipboard.GetContent();

        // 1) File / folder
        if (data.Contains(StandardDataFormats.StorageItems))
        {
            var items = await data.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
            if (paths.Length > 0)
            {
                var type = ClipboardItem.AllDirectories(paths)
                    ? ClipboardContentType.Folder : ClipboardContentType.File;
                return new ClipboardItem { Type = type, FilePaths = paths };
            }
        }

        // 2) Ảnh
        if (data.Contains(StandardDataFormats.Bitmap))
        {
            var img = await ReadBitmapAsync(data);
            if (img is { } j)
                return new ClipboardItem
                {
                    Type = ClipboardContentType.Image,
                    ImageData = j.bytes,
                    ImageSize = j.bytes.Length,
                    ImageWidth = (int)j.width,
                    ImageHeight = (int)j.height,
                    ImageHash = ClipboardItem.MakeHash(j.bytes),
                };
        }

        // 3) Text (+ rich)
        if (data.Contains(StandardDataFormats.Text))
        {
            var text = await data.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) return null;
            var trimmed = text.Trim();

            byte[]? richData = null; string? richType = null;
            if (data.Contains(StandardDataFormats.Rtf))
            {
                var rtf = await data.GetRtfAsync();
                if (!string.IsNullOrEmpty(rtf))
                { richData = System.Text.Encoding.UTF8.GetBytes(rtf); richType = "rtf"; }
            }
            else if (data.Contains(StandardDataFormats.Html))
            {
                // CHỈ lưu khi tách được fragment (bỏ header CF_HTML). Không fallback về chuỗi CF_HTML thô,
                // vì khi dán BuildCfHtml sẽ bọc header thêm lần nữa → HTML lồng header, hỏng định dạng.
                var cf = await data.GetHtmlFormatAsync();
                var frag = ClipboardFormats.ExtractCfHtmlFragment(cf);
                if (!string.IsNullOrEmpty(frag))
                { richData = System.Text.Encoding.UTF8.GetBytes(frag); richType = "html"; }
            }

            bool isUrl = Uri.TryCreate(trimmed, UriKind.Absolute, out var u)
                         && (u.Scheme == "http" || u.Scheme == "https");
            return new ClipboardItem
            {
                Type = isUrl ? ClipboardContentType.Url : ClipboardContentType.Text,
                Text = text, RichData = richData, RichType = richType,
            };
        }
        return null;
    }

    private const int MaxImageBytes = 1_000_000;
    private const int MaxPngBytes = 3_000_000;

    private static async Task<(byte[] bytes, uint width, uint height)?> ReadBitmapAsync(DataPackageView data)
    {
        var streamRef = await data.GetBitmapAsync();
        using var stream = await streamRef.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        // Lấy pixel Bgra8 với alpha THẲNG (straight) để mã hoá PNG/JPEG đúng màu vùng trong suốt.
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var pixels = pixelData.DetachPixelData();
        uint w = decoder.PixelWidth, h = decoder.PixelHeight;

        // Ảnh có kênh alpha (trong suốt) → PNG để GIỮ nền trong suốt (checkerboard mới có tác dụng);
        // nếu PNG quá lớn thì đành JPEG (mất trong suốt) để không phình đĩa.
        if (HasAlpha(pixels))
        {
            var png = await EncodePngAsync(pixels, w, h);
            if (png.Length <= MaxPngBytes) return (png, w, h);
        }

        // Lặp giảm chất lượng JPEG tới khi ≤1MB (giống NSImage.compressedJPEGData của macOS).
        byte[]? best = null;
        foreach (var q in new[] { 0.85f, 0.6f, 0.35f, 0.1f })
        {
            var bytes = await EncodeJpegAsync(pixels, w, h, q);
            if (best == null || bytes.Length < best.Length) best = bytes;
            if (bytes.Length <= MaxImageBytes) break;
        }
        return best == null ? null : (best, w, h);
    }

    private static bool HasAlpha(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] < 255) return true;
        return false;
    }

    private static async Task<byte[]> EncodePngAsync(byte[] pixels, uint w, uint h)
    {
        using var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, w, h, 96, 96, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[outStream.Size];
        outStream.Seek(0);
        using var reader = new DataReader(outStream);
        await reader.LoadAsync((uint)outStream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static async Task<byte[]> EncodeJpegAsync(byte[] pixels, uint w, uint h, float quality)
    {
        using var outStream = new InMemoryRandomAccessStream();
        var props = new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) }
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream, props);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, w, h, 96, 96, pixels);
        await encoder.FlushAsync();

        var bytes = new byte[outStream.Size];
        outStream.Seek(0);
        using var reader = new DataReader(outStream);
        await reader.LoadAsync((uint)outStream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
