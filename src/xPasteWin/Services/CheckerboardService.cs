using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;

namespace xPasteWin.Services;

/// <summary>
/// Bake ảnh "ô cờ" (alpha checkerboard) 1 lần cho mỗi theme (sáng/tối) rồi lưu PNG — nền sau ảnh
/// có vùng trong suốt để nói rõ "chỗ này trong suốt" (giống checkerboard của macOS ClipboardItemCard).
/// WinUI không tile được ImageBrush nên bake sẵn một mảng đủ lớn (256×256, ô 8px) và vẽ Stretch=None.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CheckerboardService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xPaste", "misc");

    private static string? _darkPng, _lightPng;

    /// <summary>Đường dẫn PNG ô cờ theo theme; null nếu lỗi.</summary>
    public static string? GetPng(bool dark)
    {
        if (dark && _darkPng != null) return _darkPng;
        if (!dark && _lightPng != null) return _lightPng;
        var p = Bake(dark);
        if (dark) _darkPng = p; else _lightPng = p;
        return p;
    }

    private static string? Bake(bool dark)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var png = Path.Combine(CacheDir, dark ? "checker-dark.png" : "checker-light.png");
            if (File.Exists(png) && new FileInfo(png).Length > 0) return png;

            const int square = 8, side = 256;
            // Sáng: nền trắng, ô 0.90; Tối: nền 0.17, ô 0.24 (đúng giá trị của macOS).
            var baseC = dark ? Color.FromArgb(255, 43, 43, 43) : Color.FromArgb(255, 255, 255, 255);
            var altC = dark ? Color.FromArgb(255, 61, 61, 61) : Color.FromArgb(255, 230, 230, 230);

            using var bmp = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var baseB = new SolidBrush(baseC))
            using (var altB = new SolidBrush(altC))
            {
                g.FillRectangle(baseB, 0, 0, side, side);
                for (int y = 0; y < side; y += square)
                    for (int x = 0; x < side; x += square)
                        if (((x / square) + (y / square)) % 2 == 1)
                            g.FillRectangle(altB, x, y, square, square);
            }
            bmp.Save(png, ImageFormat.Png);
            return png;
        }
        catch { return null; }
    }
}
