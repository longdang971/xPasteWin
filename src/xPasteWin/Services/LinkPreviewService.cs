using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace xPasteWin.Services;

/// <summary>Kết quả preview cho một URL (giống LinkPreviewService của macOS).</summary>
public sealed class LinkPreview
{
    public string? Title { get; init; }
    public string? Domain { get; init; }
    public string? ImagePath { get; init; }
    /// <summary>Ảnh là favicon (hiện nhỏ, căn giữa) chứ không phải og:image (phủ đầy).</summary>
    public bool IsFavicon { get; init; }
}

/// <summary>
/// Lấy metadata (title/ảnh) của một trang web để hiển thị preview trên card — parse OpenGraph,
/// tải ảnh og:image (hoặc favicon), cache trong RAM + đĩa. Trả null nếu không lấy được.
/// </summary>
public static class LinkPreviewService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, Task<LinkPreview?>> Cache = new();

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xPaste", "linkcache");

    private static HttpClient CreateClient()
    {
        var h = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
            MaxResponseContentBufferSize = 4 * 1024 * 1024,
        };
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        return h;
    }

    /// <summary>Bật/tắt fetch link preview (setting "Load link previews" ở Privacy/Appearance).</summary>
    public static bool Enabled = true;

    public static Task<LinkPreview?> GetAsync(string url)
    {
        if (!Enabled) return Task.FromResult<LinkPreview?>(null);
        return Cache.GetOrAdd(url, u =>
        {
            var task = FetchAsync(u);
            // KHÔNG giữ kết quả thất bại (null / lỗi) trong cache → cho phép thử lại lần sau
            // (một URL lỗi mạng thoáng qua sẽ không bị "đóng băng" cả phiên).
            _ = task.ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion || t.Result == null)
                    Cache.TryRemove(u, out _);
            }, TaskScheduler.Default);
            return task;
        });
    }

    private static async Task<LinkPreview?> FetchAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != "http" && baseUri.Scheme != "https"))
            return null;
        try
        {
            Directory.CreateDirectory(CacheDir);
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!ct.Contains("html")) return new LinkPreview { Domain = baseUri.Host };
            var html = await resp.Content.ReadAsStringAsync();

            string? title = MetaContent(html, "og:title")
                            ?? MetaContent(html, "twitter:title")
                            ?? TitleTag(html);
            // Ưu tiên og:image (ảnh lớn). Không có/không tải được → favicon (thử nhiều nguồn).
            string? ogImage = MetaContent(html, "og:image") ?? MetaContent(html, "twitter:image");
            string? imagePath = null;
            bool isFavicon = false;
            if (ogImage != null && Uri.TryCreate(baseUri, ogImage, out var ogUri))
                imagePath = await DownloadImageAsync(url, ogUri);
            if (imagePath == null)
            {
                isFavicon = true;
                imagePath = await FetchFaviconAsync(url, baseUri, html);
            }

            return new LinkPreview
            {
                Title = WebUtility(title),
                Domain = baseUri.Host,
                ImagePath = imagePath,
                IsFavicon = isFavicon && imagePath != null,
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Lấy favicon thử nhiều nguồn theo thứ tự (giống fetchFavicon của macOS, đã bỏ DuckDuckGo):
    /// 1) &lt;link rel="icon/shortcut icon/apple-touch-icon"&gt; trong HTML;
    /// 2) Google favicon service. Dùng nguồn đầu tiên trả về ảnh hợp lệ.
    /// </summary>
    private static async Task<string?> FetchFaviconAsync(string sourceUrl, Uri baseUri, string html)
    {
        var host = baseUri.Host;
        var candidates = new System.Collections.Generic.List<string>();
        var htmlFav = FaviconUrl(html);
        if (htmlFav != null && Uri.TryCreate(baseUri, htmlFav, out var hu)) candidates.Add(hu.AbsoluteUri);
        candidates.Add($"https://www.google.com/s2/favicons?domain={host}&sz=64");

        var path = Path.Combine(CacheDir, Hash(sourceUrl) + ".img");
        foreach (var c in candidates)
        {
            try
            {
                using var resp = await Http.GetAsync(c);
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 70) continue; // bỏ ảnh rỗng / 1px tracking
                await File.WriteAllBytesAsync(path, bytes);
                return path;
            }
            catch { }
        }
        return null;
    }

    private static async Task<string?> DownloadImageAsync(string sourceUrl, Uri imgUri)
    {
        try
        {
            var path = Path.Combine(CacheDir, Hash(sourceUrl) + ".img");
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            var bytes = await Http.GetByteArrayAsync(imgUri);
            if (bytes.Length == 0) return null;
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch { return null; }
    }

    private static string? MetaContent(string html, string key)
    {
        foreach (Match m in Regex.Matches(html, "<meta[^>]+>", RegexOptions.IgnoreCase))
        {
            var tag = m.Value;
            var prop = Attr(tag, "property") ?? Attr(tag, "name");
            if (prop != null && prop.Equals(key, StringComparison.OrdinalIgnoreCase))
                return Attr(tag, "content");
        }
        return null;
    }

    private static string? TitleTag(string html)
    {
        var m = Regex.Match(html, "<title[^>]*>(.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string? FaviconUrl(string html)
    {
        foreach (Match m in Regex.Matches(html, "<link[^>]+>", RegexOptions.IgnoreCase))
        {
            var tag = m.Value;
            var rel = Attr(tag, "rel");
            if (rel != null && rel.Contains("icon", StringComparison.OrdinalIgnoreCase))
                return Attr(tag, "href");
        }
        return null;
    }

    private static string? Attr(string tag, string attr)
    {
        var m = Regex.Match(tag, attr + "\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? WebUtility(string? s) =>
        s == null ? null : System.Net.WebUtility.HtmlDecode(s).Trim();

    private static string Hash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }
}
