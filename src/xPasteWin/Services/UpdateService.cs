using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace xPasteWin.Services;

/// <summary>Thông tin một bản phát hành mới hơn bản đang chạy.</summary>
public sealed record UpdateInfo(
    Version Version,     // version đã chuẩn hoá 3 phần (x.y.z)
    string TagName,      // tag gốc trên GitHub (vd "v1.1.0")
    string DownloadUrl,  // link tải asset .exe, HOẶC link trang release nếu không có asset
    string? AssetName,   // tên file bộ cài (null nếu release không đính kèm .exe)
    string ReleaseUrl,   // trang release để người dùng tự xem/tải
    string Notes);       // mô tả release (body)

/// <summary>
/// Kiểm tra cập nhật qua GitHub Releases. Không cần token, không cần server riêng.
/// Static theo đúng phong cách các service khác (ThemeService, LinkPreviewService…).
/// </summary>
public static class UpdateService
{
    // Repo phát hành. Đổi tại đây nếu chuyển repo/đổi chủ.
    private const string Owner = "longdang971";
    private const string Repo = "xPasteWin";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub API BẮT BUỘC User-Agent, thiếu sẽ trả 403.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("xPaste-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Version của app đang chạy (đọc từ &lt;Version&gt; trong csproj), chuẩn hoá về 3 phần.</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
        }
    }

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    public static bool HasInstaller(UpdateInfo info) => info.AssetName != null;

    /// <summary>
    /// Hỏi GitHub bản phát hành mới nhất. Trả về <see cref="UpdateInfo"/> nếu có bản MỚI HƠN,
    /// null nếu đã mới nhất HOẶC lỗi mạng (im lặng — dùng cho auto-check lúc khởi động).
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null; // 404 = repo chưa có release nào
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            // Bỏ qua bản nháp / thử nghiệm.
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var latest = ParseVersion(tag);
            if (latest is null || latest <= CurrentVersion) return null; // đã mới nhất

            string releaseUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            // Tìm asset .exe (bộ cài). Nếu không có, dùng link trang release để người dùng tự tải.
            string? assetUrl = null, assetName = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = name;
                        assetUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        break;
                    }
                }
            }

            return new UpdateInfo(latest, tag, assetUrl ?? releaseUrl, assetUrl != null ? assetName : null, releaseUrl, notes.Trim());
        }
        catch { return null; } // mạng hỏng/timeout/JSON lạ → coi như không có update
    }

    /// <summary>
    /// Tải bộ cài về %TEMP%\xPasteUpdate. <paramref name="progress"/> báo 0..1,
    /// hoặc -1 nếu server không cho biết tổng dung lượng (bar chạy vô định).
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (info.AssetName is null) throw new InvalidOperationException("Release không có bộ cài (.exe).");

        var dir = Path.Combine(Path.GetTempPath(), "xPasteUpdate");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, info.AssetName);

        using var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1L;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            progress?.Report(total > 0 ? (double)read / total : -1);
        }
        return dest;
    }

    /// <summary>Chạy bộ cài đã tải. Trả true nếu khởi chạy được → caller nên thoát app để Inno Setup ghi đè file.</summary>
    public static bool LaunchInstaller(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// <summary>Mở trang release trên trình duyệt (fallback khi không tải/cài tự động được).</summary>
    public static void OpenReleasePage(UpdateInfo info)
    {
        try
        {
            var target = string.IsNullOrEmpty(info.ReleaseUrl) ? info.DownloadUrl : info.ReleaseUrl;
            if (!string.IsNullOrEmpty(target))
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>Chuẩn hoá tag ("v1.1.0", "1.1", "1.2.0-beta"…) về Version 3 phần. null nếu không parse được.</summary>
    private static Version? ParseVersion(string tag)
    {
        tag = tag.Trim();
        if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) tag = tag[1..];
        int cut = tag.IndexOfAny(new[] { '-', '+' }); // bỏ hậu tố -beta / +build
        if (cut >= 0) tag = tag[..cut];
        if (!Version.TryParse(tag, out var v)) return null;
        return new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
    }
}
