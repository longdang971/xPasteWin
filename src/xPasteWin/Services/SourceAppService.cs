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
        if (string.IsNullOrEmpty(exe)) return null;
        try
        {
            var fd = FileVersionInfo.GetVersionInfo(exe).FileDescription;
            return !string.IsNullOrWhiteSpace(fd) ? fd : Path.GetFileNameWithoutExtension(exe);
        }
        catch { return Path.GetFileNameWithoutExtension(exe); }
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
            var png = Path.Combine(CacheDir, Hash(exePath) + ".png");

            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return new AppVisual();
            using var bmp = icon.ToBitmap();
            if (!File.Exists(png) || new FileInfo(png).Length == 0)
                bmp.Save(png, ImageFormat.Png);

            uint argb = DominantColor(bmp, out bool has);
            return new AppVisual { IconPath = png, AccentArgb = argb, HasAccent = has };
        }
        catch { return new AppVisual(); }
    }

    // Trích màu CHỦ ĐẠO từ icon: thu nhỏ 32×32, duyệt pixel đục có màu (sat ≥ 0.25, brightness
    // 0.15–0.95), dồn vào histogram 24 vùng hue với trọng số = độ rực (sat). Vùng có TỔNG trọng số
    // lớn nhất (diện tích × độ rực) là màu chủ đạo — thay vì chọn 1 pixel rực nhất như trước (khiến
    // icon xanh dương bị vài chi tiết nhỏ rực hơn "cướp" màu). Lấy hue trung bình vòng (circular mean)
    // của vùng thắng + 2 vùng kề (mượt, tránh màu bị cắt đôi giữa 2 bucket) → HSB(hue,0.65,0.52).
    // Không có pixel màu (icon xám/đơn sắc) → màu trung bình RGB.
    private static uint DominantColor(Bitmap src, out bool has)
    {
        has = false;
        try
        {
            using var small = new Bitmap(src, new Size(32, 32));
            const int BUCKETS = 24;
            const double BucketWidth = 360.0 / BUCKETS;
            var wsum = new double[BUCKETS];
            var sinw = new double[BUCKETS];
            var cosw = new double[BUCKETS];
            long r = 0, g = 0, b = 0, n = 0;
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    var c = small.GetPixel(x, y);
                    if (c.A < 128) continue;
                    n++; r += c.R; g += c.G; b += c.B;
                    float bright = c.GetBrightness();
                    if (bright < 0.15f || bright > 0.95f) continue;
                    float sat = c.GetSaturation();
                    if (sat < 0.25f) continue;
                    float hue = c.GetHue();
                    int bi = (int)(hue / BucketWidth) % BUCKETS;
                    double w = sat;                       // trọng số theo độ rực → vùng màu rõ + rộng thắng
                    double rad = hue * Math.PI / 180.0;
                    wsum[bi] += w; sinw[bi] += Math.Sin(rad) * w; cosw[bi] += Math.Cos(rad) * w;
                }

            // Chọn bucket theo điểm đã LÀM MƯỢT (cộng nửa trọng số 2 bucket kề, vòng tròn) để một màu
            // chủ đạo bị trải sang 2 bucket không bị thua một màu phụ gọn trong 1 bucket.
            int best = -1; double bestScore = 0;
            for (int i = 0; i < BUCKETS; i++)
            {
                double score = wsum[i] + 0.5 * (wsum[(i + BUCKETS - 1) % BUCKETS] + wsum[(i + 1) % BUCKETS]);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best >= 0)
            {
                // Hue trung bình vòng của bucket thắng + 2 kề (gộp vector sin/cos đã trọng số).
                int lo = (best + BUCKETS - 1) % BUCKETS, hi = (best + 1) % BUCKETS;
                double s = sinw[best] + sinw[lo] + sinw[hi];
                double co = cosw[best] + cosw[lo] + cosw[hi];
                double meanHue = Math.Atan2(s, co) * 180.0 / Math.PI;
                if (meanHue < 0) meanHue += 360;
                has = true; return HsbToArgb((float)meanHue, 0.65, 0.52);
            }
            if (n > 0) { has = true; return 0xFF000000u | (uint)((r / n) << 16) | (uint)((g / n) << 8) | (uint)(b / n); }
        }
        catch { }
        return 0xFF007AFF;
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
}
