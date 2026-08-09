using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using xPasteWin.Interop;

namespace xPasteWin.Services;

/// <summary>
/// Xác định app đã copy nội dung (qua GetClipboardOwner → tiến trình → exe) và trích icon của app đó
/// để hiển thị trên card — tương đương frontmostApplication/sourceAppBundleID + app icon của macOS.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SourceAppService
{
    private static readonly ConcurrentDictionary<string, string?> IconCache = new();
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xPaste", "appicons");

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Đường dẫn exe của app đang sở hữu clipboard (gọi lúc WM_CLIPBOARDUPDATE). Null nếu không rõ.</summary>
    public static string? GetClipboardSourceExe()
    {
        var hwnd = Win32.GetClipboardOwner();
        if (hwnd == IntPtr.Zero) return null;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        return ExePathOf(pid);
    }

    /// <summary>Tên hiển thị của app sở hữu một cửa sổ (vd "Google Chrome") — cho nhãn "Paste to &lt;App&gt;".
    /// Ưu tiên FileDescription của exe, fallback tên file. Null nếu không rõ.</summary>
    public static string? GetWindowAppName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        var exe = ExePathOf(pid);
        return string.IsNullOrEmpty(exe) ? null : DisplayName(exe);
    }

    /// <summary>Tên hiển thị của một exe (vd "Google Chrome"): ưu tiên FileDescription, fallback tên
    /// file. Dùng cho nhãn app ở popover filter và token filter.</summary>
    public static string DisplayName(string exePath)
    {
        try
        {
            var fd = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
            return !string.IsNullOrWhiteSpace(fd) ? fd : Path.GetFileNameWithoutExtension(exePath);
        }
        catch { return Path.GetFileNameWithoutExtension(exePath); }
    }

    private static string? ExePathOf(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Giao diện của app nguồn: icon (PNG) + màu accent trích từ icon.</summary>
    public sealed class AppVisual
    {
        public string? IconPath { get; init; }
        public uint AccentArgb { get; init; }
        public bool HasAccent { get; init; }
    }

    private static readonly ConcurrentDictionary<string, AppVisual> VisualCache = new();

    /// <summary>Trích icon + màu accent của exe (cache). Null nếu không có exe.</summary>
    public static AppVisual? GetVisual(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        return VisualCache.GetOrAdd(exePath, Extract);
    }

    private static AppVisual Extract(string exePath)
    {
        try
        {
            if (!File.Exists(exePath)) return new AppVisual();
            Directory.CreateDirectory(CacheDir);
            // Hậu tố "-hi": tên file khác bản cũ để cache 32px đã lưu trước đây KHÔNG bị dùng lại.
            var png = Path.Combine(CacheDir, Hash(exePath) + "-hi.png");

            using var bmp = ExtractLargest(exePath);
            if (bmp == null) return new AppVisual();
            if (!File.Exists(png) || new FileInfo(png).Length == 0)
                bmp.Save(png, ImageFormat.Png);

            uint argb = DominantColor(bmp, out bool has);
            return new AppVisual { IconPath = png, AccentArgb = argb, HasAccent = has };
        }
        catch { return new AppVisual(); }
    }

    /// <summary>
    /// Lấy BẢN LỚN NHẤT trong nhóm icon của exe. Icon.ExtractAssociatedIcon chỉ trả bản 32×32 (cỡ
    /// "small" của shell): đưa lên ô 36×36 là đã phóng to, còn ở 150% DPI thì 32px phải kéo lên 54px
    /// thật → nhoè, răng cưa. PrivateExtractIcons cho phép chỉ định cỡ mong muốn nên lấy được bản
    /// 256×256 mà hầu hết app hiện đại đều nhúng. Thử nhỏ dần cho app chỉ có icon cỡ bé.
    /// </summary>
    private static Bitmap? ExtractLargest(string exePath)
    {
        foreach (int size in new[] { 256, 128, 64, 48, 32 })
        {
            var handles = new IntPtr[1];
            var ids = new uint[1];
            uint n;
            try { n = PrivateExtractIcons(exePath, 0, size, size, handles, ids, 1, 0); }
            catch { break; }
            if (n == 0 || handles[0] == IntPtr.Zero) continue;
            try
            {
                using var icon = Icon.FromHandle(handles[0]);
                return icon.ToBitmap();
            }
            catch { }
            finally { DestroyIcon(handles[0]); }
        }
        try
        {
            using var fallback = Icon.ExtractAssociatedIcon(exePath);
            return fallback?.ToBitmap();
        }
        catch { return null; }
    }

    // Trích màu THƯƠNG HIỆU từ icon — port đúng extractDominantColor của macOS.
    //
    // Chọn 1 pixel rực nhất KHÔNG hiệu quả: nó bỏ qua việc một màu phủ BAO NHIÊU icon, nên Chrome
    // ra màu xanh lá của viền vòng (mảng nhỏ, rực nhất) thay vì đĩa xanh dương ai cũng nhận ra.
    // Icon đặt "dấu ấn" ở GIỮA, chi tiết phụ ở RÌA — nên hue được dồn vào histogram với trọng số
    // theo KHOẢNG CÁCH TỚI TÂM (Gaussian σ=0.35) nhân độ rực (sat): xanh Chrome chỉ 9.5% diện tích
    // thô nhưng 61% ở phần ba trung tâm.
    //
    // Khác bản cũ (ép mọi header về HSB(hue,0.65,0.52) → cùng một tông): GIỮ hue+sat+bri thật của
    // app, chỉ kẹp đủ để chữ tiêu đề còn đọc được (sat≤0.85, bri∈[0.32,0.92]). Pixel xám (sat<0.18)
    // gom riêng: icon đen/trắng thuần (Terminal) không bị ép theo hue rò rỉ từ khử răng cưa.
    private const int DomSize = 32;
    private static uint DominantColor(Bitmap src, out bool has)
    {
        has = false;
        try
        {
            using var small = new Bitmap(src, new Size(DomSize, DomSize));
            const int bucketCount = 24;
            var bucketWeight = new double[bucketCount];
            var bucketR = new double[bucketCount];
            var bucketG = new double[bucketCount];
            var bucketB = new double[bucketCount];
            // Xám tách riêng: icon toàn đen/trắng không có hue để thắng, không được ép theo hue.
            double greyWeight = 0, greyR = 0, greyG = 0, greyB = 0;

            const double sigma = 0.35;
            for (int y = 0; y < DomSize; y++)
                for (int x = 0; x < DomSize; x++)
                {
                    var c = small.GetPixel(x, y);
                    if (c.A < 128) continue;                       // alpha > 0.5
                    double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
                    RgbToHsv(r, g, b, out double h, out double s, out _);

                    double dx = (x + 0.5) / DomSize - 0.5;
                    double dy = (y + 0.5) / DomSize - 0.5;
                    double dist = Math.Sqrt(dx * dx + dy * dy) / 0.5;
                    double centre = Math.Exp(-(dist * dist) / (2 * sigma * sigma));

                    if (s < 0.18)
                    {
                        greyWeight += centre; greyR += r * centre; greyG += g * centre; greyB += b * centre;
                        continue;
                    }
                    // Trọng số nhân thêm độ rực để mảng nhạt phía sau dấu ấn không lấn át được nó.
                    double w = centre * s;
                    int i = Math.Min(bucketCount - 1, (int)(h / 360.0 * bucketCount));
                    bucketWeight[i] += w; bucketR[i] += r * w; bucketG[i] += g * w; bucketB[i] += b * w;
                }

            int best = -1; double bestWeight = 0;
            for (int i = 0; i < bucketCount; i++)
                if (bucketWeight[i] > bestWeight) { bestWeight = bucketWeight[i]; best = i; }

            double rr, gg, bb;
            if (best >= 0 && bestWeight > greyWeight * 0.25)
            {
                rr = bucketR[best] / bestWeight; gg = bucketG[best] / bestWeight; bb = bucketB[best] / bestWeight;
            }
            else if (greyWeight > 0)
            {
                rr = greyR / greyWeight; gg = greyG / greyWeight; bb = greyB / greyWeight;
            }
            else return 0xFF007AFF;

            // Giữ hue/sat/bri của app; chỉ kẹp để tiêu đề còn đọc được (không ép về một tông tối).
            RgbToHsv(rr, gg, bb, out double hh, out double ss, out double vv);
            ss = Math.Min(ss, 0.85);
            vv = Math.Min(Math.Max(vv, 0.32), 0.92);
            has = true;
            return HsbToArgb((float)hh, ss, vv);
        }
        catch { }
        return 0xFF007AFF;
    }

    /// <summary>RGB (0..1) → HSV: hue 0..360, sat/val 0..1. Dùng HSV (brightness = max kênh) đúng như
    /// NSColor.getHue của macOS — KHÁC System.Drawing.GetBrightness/GetSaturation (vốn là HSL).</summary>
    private static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }
        double hh;
        if (max == r) hh = ((g - b) / d) % 6;
        else if (max == g) hh = (b - r) / d + 2;
        else hh = (r - g) / d + 4;
        hh *= 60;
        if (hh < 0) hh += 360;
        h = hh;
    }

    private static uint HsbToArgb(float hue, double sat, double bri)
    {
        double h = hue / 60.0;
        double c = bri * sat;
        double xx = c * (1 - Math.Abs(h % 2 - 1));
        double m = bri - c;
        double r1 = 0, g1 = 0, b1 = 0;
        switch ((int)Math.Floor(h) % 6)
        {
            case 0: r1 = c; g1 = xx; break;
            case 1: r1 = xx; g1 = c; break;
            case 2: g1 = c; b1 = xx; break;
            case 3: g1 = xx; b1 = c; break;
            case 4: r1 = xx; b1 = c; break;
            default: r1 = c; b1 = xx; break;
        }
        byte R = (byte)Math.Round((r1 + m) * 255);
        byte G = (byte)Math.Round((g1 + m) * 255);
        byte B = (byte)Math.Round((b1 + m) * 255);
        return 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant())));

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder buf, ref uint size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy,
        IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
