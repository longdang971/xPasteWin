using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace xPasteWin.Services;

/// <summary>
/// Nhận diện chữ trong ảnh bằng Windows.Media.Ocr (tương đương OCRService/Vision của macOS) để
/// tìm kiếm ảnh theo nội dung chữ. Engine tạo theo ngôn ngữ hồ sơ người dùng; null nếu máy chưa có
/// gói OCR. Bỏ qua ảnh quá lớn so với giới hạn của engine.
/// </summary>
public static class OcrService
{
    private static OcrEngine? _engine;
    private static bool _tried;

    private static OcrEngine? Engine
    {
        get
        {
            if (!_tried)
            {
                _tried = true;
                try { _engine = OcrEngine.TryCreateFromUserProfileLanguages(); } catch { _engine = null; }
            }
            return _engine;
        }
    }

    public static bool Available => Engine != null;

    /// <summary>Trả về chữ đọc được trong ảnh (null nếu không có engine / lỗi / không có chữ).</summary>
    public static async Task<string?> RecognizeAsync(byte[]? imageBytes)
    {
        var engine = Engine;
        if (engine == null || imageBytes is not { Length: > 0 }) return null;
        try
        {
            using var ms = new InMemoryRandomAccessStream();
            var writer = new DataWriter(ms);
            writer.WriteBytes(imageBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            ms.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(ms);
            if (decoder.PixelWidth > OcrEngine.MaxImageDimension ||
                decoder.PixelHeight > OcrEngine.MaxImageDimension) return null;

            using var bmp = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bmp);
            return string.IsNullOrWhiteSpace(result.Text) ? null : result.Text;
        }
        catch { return null; }
    }
}
