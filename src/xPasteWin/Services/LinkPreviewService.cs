using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
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
    /// <summary>og:image nhưng NHỎ / gần vuông (logo) → hiện căn giữa vừa phải, KHÔNG phủ đầy (tránh phóng to vỡ nét).</summary>
    public bool IsLogo { get; init; }
    public int? ImgW { get; init; }
    public int? ImgH { get; init; }

    /// <summary><see cref="Title"/> là tiêu đề THẬT của trang (og:title/&lt;title&gt;) chứ không phải tên
    /// miền suy ra khi trang không cho đọc. Card in đậm dòng này để tách khỏi URL bên dưới.</summary>
    public bool HasPageTitle { get; init; }

    /// <summary>Đã hỏi được máy chủ (dù trang trả JSON hay lỗi 403) → kết quả đáng ghi xuống đĩa.
    /// false = hỏng mạng/timeout: chỉ hiện tạm tên miền và sẽ thử lại lượt sau.</summary>
    public bool Resolved { get; init; }
}

/// <summary>
/// Lấy metadata (title/ảnh) của một trang web để hiển thị preview trên card — parse OpenGraph,
/// tải ảnh og:image (hoặc favicon), cache trong RAM + đĩa.
///
/// Nguyên tắc: card URL KHÔNG BAO GIỜ trống. Trang không trả HTML (endpoint API trả JSON), trả lỗi
/// 401/403/404, hay chặn bot — vẫn hiện tên miền + favicon của miền đó. Chỉ khi đứt mạng hoàn toàn mới
/// coi là chưa giải được và thử lại ở lượt sau.
/// </summary>
public static class LinkPreviewService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, Task<LinkPreview?>> Cache = new();

    /// <summary>Chặn bão request khi panel mở ra và hàng chục card cùng fetch một lúc: quá nhiều kết nối
    /// song song trên đường truyền yếu làm mọi request cùng chạm timeout và KHÔNG card nào có preview.</summary>
    private static readonly SemaphoreSlim Gate = new(6, 6);

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xPaste", "linkcache");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static HttpClient CreateClient()
    {
        var h = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            // 8s là quá sát: trang nặng (Google Drive ~120KB HTML) qua mạng chậm/VPN hay vượt mốc đó,
            // và mỗi lần vượt là card mất hẳn preview cho tới khi dựng lại VM.
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 8 * 1024 * 1024,
        };
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return h;
    }

    /// <summary>Bật/tắt fetch link preview (setting "Load link previews" ở Privacy/Appearance).</summary>
    public static bool Enabled = true;

    public static Task<LinkPreview?> GetAsync(string url)
    {
        if (!Enabled) return Task.FromResult<LinkPreview?>(null);
        return Cache.GetOrAdd(url, u =>
        {
            var task = ResolveAsync(u);
            // Giữ trong cache RAM chỉ kết quả đã giải được. Kết quả hỏng-mạng bị gỡ ra để lượt sau
            // (mở lại panel / dựng lại card) tự thử lại thay vì "đóng băng" card trống cả phiên.
            _ = task.ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion || t.Result is not { Resolved: true })
                    Cache.TryRemove(u, out _);
            }, TaskScheduler.Default);
            return task;
        });
    }

    /// <summary>Đĩa trước, mạng sau. Metadata đã giải được ghi xuống đĩa nên mở lại app là card có
    /// tiêu đề NGAY, không phụ thuộc mạng — trước đây cache chỉ nằm trong RAM nên mỗi phiên đều fetch lại
    /// toàn bộ và chỉ cần một lần lỗi là card trống.</summary>
    private static async Task<LinkPreview?> ResolveAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != "http" && baseUri.Scheme != "https"))
            return null;

        if (LoadMeta(url) is { } disk) return disk;

        await Gate.WaitAsync().ConfigureAwait(false);
        LinkPreview? result;
        try { result = await FetchAsync(url, baseUri).ConfigureAwait(false); }
        finally { Gate.Release(); }

        if (result is { Resolved: true }) SaveMeta(url, result);
        return result;
    }

    private static async Task<LinkPreview?> FetchAsync(string url, Uri baseUri)
    {
        try { Directory.CreateDirectory(CacheDir); } catch { }

        // Trần thời gian cho CẢ lượt (trang + ảnh + favicon). Một host chết ngốn nguyên timeout của
        // HttpClient ở từng request con, cộng lại giữ chỗ trong Gate hàng chục giây và chặn các link
        // lành phía sau — đúng kiểu "vài link không get được" khi mở panel có nhiều card.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var tok = budget.Token;

        HttpResponseMessage? resp = null;
        try
        {
            // Một lần thử lại: lỗi mạng thoáng qua (DNS/handshake) rất hay gặp ngay lúc panel bung ra
            // và hàng loạt card cùng khởi động fetch.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, tok).ConfigureAwait(false);
                    break;
                }
                catch when (attempt == 0 && !tok.IsCancellationRequested)
                {
                    await Task.Delay(400, tok).ConfigureAwait(false);
                }
            }

            var ct = resp.Content.Headers.ContentType?.MediaType ?? "";

            // URL trỏ THẲNG tới ảnh → tải chính nó, vẽ như card ảnh (phủ đầy).
            if (resp.IsSuccessStatusCode && ct.StartsWith("image", StringComparison.OrdinalIgnoreCase))
            {
                var direct = await DownloadImageAsync(OgPath(url), baseUri, tok).ConfigureAwait(false);
                if (direct != null)
                {
                    var (dw, dh) = ReadDims(direct);
                    return new LinkPreview
                    {
                        Title = FileNameTitle(baseUri) ?? PrettyHost(baseUri),
                        HasPageTitle = FileNameTitle(baseUri) != null,
                        Domain = baseUri.Host,
                        ImagePath = direct,
                        IsLogo = IsLogoSized(dw, dh),
                        ImgW = dw, ImgH = dh,
                        Resolved = true,
                    };
                }
                return await DomainOnlyAsync(baseUri, null, tok).ConfigureAwait(false);
            }

            // Không phải HTML (endpoint API trả JSON/text) hoặc trang trả lỗi (401/403/404, chặn bot):
            // KHÔNG bỏ trắng card — vẫn hiện tên miền + favicon của miền đó.
            if (!resp.IsSuccessStatusCode || !ct.Contains("html"))
                return await DomainOnlyAsync(baseUri, null, tok).ConfigureAwait(false);

            string html;
            try { html = await resp.Content.ReadAsStringAsync(tok).ConfigureAwait(false); }
            catch { return await DomainOnlyAsync(baseUri, null, tok).ConfigureAwait(false); }

            string? title = Clean(MetaContent(html, "og:title"))
                            ?? Clean(MetaContent(html, "twitter:title"))
                            ?? Clean(TitleTag(html));

            // Ưu tiên og:image (ảnh lớn). Không có/không tải được → favicon (thử nhiều nguồn).
            string? ogImage = MetaContent(html, "og:image") ?? MetaContent(html, "twitter:image");
            string? imagePath = null;
            if (ogImage != null && Uri.TryCreate(baseUri, System.Net.WebUtility.HtmlDecode(ogImage), out var ogUri))
                imagePath = await DownloadImageAsync(OgPath(url), ogUri, tok).ConfigureAwait(false);

            bool isFavicon = imagePath == null;
            if (isFavicon) imagePath = await FetchFaviconAsync(baseUri, html, tok).ConfigureAwait(false);

            var (w, h) = imagePath != null && !isFavicon ? ReadDims(imagePath) : (null, null);
            return new LinkPreview
            {
                Title = title ?? PrettyHost(baseUri),
                HasPageTitle = title != null,
                Domain = baseUri.Host,
                ImagePath = imagePath,
                IsFavicon = isFavicon && imagePath != null,
                IsLogo = !isFavicon && imagePath != null && IsLogoSized(w, h),
                ImgW = w, ImgH = h,
                Resolved = true,
            };
        }
        catch
        {
            // Đứt mạng thật: vẫn trả tên miền để card có cái gì đó đọc được thay vì "147 characters",
            // nhưng KHÔNG đánh dấu Resolved → không ghi đĩa, lượt sau thử lại.
            return new LinkPreview { Title = PrettyHost(baseUri), Domain = baseUri.Host };
        }
        finally { resp?.Dispose(); }
    }

    /// <summary>Preview "chỉ có tên miền": dùng khi trang không trả HTML hoặc trả lỗi. Vẫn cố lấy favicon
    /// theo miền để card có hình nhận diện.</summary>
    private static async Task<LinkPreview> DomainOnlyAsync(Uri baseUri, string? html, CancellationToken tok)
    {
        var fav = await FetchFaviconAsync(baseUri, html, tok).ConfigureAwait(false);
        return new LinkPreview
        {
            Title = PrettyHost(baseUri),
            Domain = baseUri.Host,
            ImagePath = fav,
            IsFavicon = fav != null,
            Resolved = true,
        };
    }

    /// <summary>
    /// Lấy favicon thử nhiều nguồn theo thứ tự: 1) &lt;link rel="icon"&gt; trong HTML (nếu có HTML);
    /// 2) /favicon.ico ở gốc miền; 3) Google favicon service. Dùng nguồn đầu tiên trả ảnh hợp lệ.
    ///
    /// Cache theo MIỀN chứ không theo URL: 12 link Google Drive khác nhau dùng chung một favicon, cache
    /// theo URL thì tải lại 12 lần và link mới copy luôn phải chờ mạng.
    /// </summary>
    private static async Task<string?> FetchFaviconAsync(Uri baseUri, string? html, CancellationToken tok)
    {
        var host = baseUri.Host;
        var path = Path.Combine(CacheDir, Hash("favicon:" + host) + ".ico");
        try { if (File.Exists(path) && new FileInfo(path).Length > 0) return path; } catch { }

        var candidates = new System.Collections.Generic.List<string>();
        if (html != null && FaviconUrl(html) is { } htmlFav &&
            Uri.TryCreate(baseUri, System.Net.WebUtility.HtmlDecode(htmlFav), out var hu))
            candidates.Add(hu.AbsoluteUri);
        candidates.Add($"{baseUri.Scheme}://{baseUri.Authority}/favicon.ico");
        candidates.Add($"https://www.google.com/s2/favicons?domain={host}&sz=64");
        // Subdomain thường không tự có favicon (api.github.com, cdn.… ) → mượn của miền cha.
        if (ParentDomain(host) is { } parent)
        {
            candidates.Add($"https://{parent}/favicon.ico");
            candidates.Add($"https://www.google.com/s2/favicons?domain={parent}&sz=64");
        }

        foreach (var c in candidates)
        {
            if (tok.IsCancellationRequested) break;
            try
            {
                using var resp = await Http.GetAsync(c, tok).ConfigureAwait(false);
                var bytes = await resp.Content.ReadAsByteArrayAsync(tok).ConfigureAwait(false);
                // Xét NỘI DUNG, không xét status: dịch vụ favicon của Google trả icon quả cầu mặc định
                // kèm status 404, lọc theo IsSuccessStatusCode là vứt mất cái icon dùng được duy nhất của
                // các miền không tự có favicon. Chiều ngược lại cũng có: /favicon.ico thiếu hay trả trang
                // HTML 404 kèm status 200 — nhận bừa là lưu rác vào cache rồi card hiện ô vỡ.
                if (!LooksLikeImage(bytes)) continue;
                await File.WriteAllBytesAsync(path, bytes, tok).ConfigureAwait(false);
                return path;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Nhận diện ảnh theo magic bytes (PNG/JPEG/GIF/BMP/ICO/WebP). Chặn trang lỗi HTML bị trả về
    /// thay cho ảnh. Ngưỡng 70 byte loại nốt ảnh rỗng / 1px tracking.</summary>
    private static bool LooksLikeImage(byte[] b)
    {
        if (b.Length < 70) return false;
        if (b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return true;   // PNG
        if (b[0] == 0xFF && b[1] == 0xD8) return true;                                 // JPEG
        if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return true;                    // GIF
        if (b[0] == 'B' && b[1] == 'M') return true;                                   // BMP
        if (b[0] == 0x00 && b[1] == 0x00 && (b[2] == 0x01 || b[2] == 0x02) && b[3] == 0x00) return true; // ICO/CUR
        if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' &&
            b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return true;    // WebP
        if (b[0] == '<' && b.Length > 300) return false;                               // SVG/HTML → WIC không vẽ được
        return false;
    }

    /// <summary>"api.github.com" → "github.com"; null nếu host đã là miền hai cấp / là địa chỉ IP.</summary>
    private static string? ParentDomain(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out _)) return null;
        var parts = host.Split('.');
        return parts.Length > 2 ? string.Join('.', parts[1..]) : null;
    }

    private static string OgPath(string sourceUrl) => Path.Combine(CacheDir, Hash(sourceUrl) + ".og");

    private static async Task<string?> DownloadImageAsync(string path, Uri imgUri, CancellationToken tok)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            var bytes = await Http.GetByteArrayAsync(imgUri, tok).ConfigureAwait(false);
            if (!LooksLikeImage(bytes)) return null;
            await File.WriteAllBytesAsync(path, bytes, tok).ConfigureAwait(false);
            return path;
        }
        catch { return null; }
    }

    // ---------- Cache metadata trên đĩa ----------

    private static string MetaPath(string url) => Path.Combine(CacheDir, Hash(url) + ".json");

    private static LinkPreview? LoadMeta(string url)
    {
        try
        {
            var p = MetaPath(url);
            if (!File.Exists(p)) return null;
            var v = JsonSerializer.Deserialize<LinkPreview>(File.ReadAllText(p), JsonOpts);
            if (v is not { Resolved: true }) return null;
            // Ảnh có thể đã bị dọn — bỏ đường dẫn chết để card rơi về dạng chỉ-chữ thay vì ô trống.
            if (v.ImagePath != null && !File.Exists(v.ImagePath))
                v = new LinkPreview
                {
                    Title = v.Title, Domain = v.Domain, HasPageTitle = v.HasPageTitle, Resolved = true,
                };
            return v;
        }
        catch { return null; }
    }

    private static void SaveMeta(string url, LinkPreview p)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(MetaPath(url), JsonSerializer.Serialize(p, JsonOpts));
        }
        catch { }
    }

    // ---------- Parse HTML ----------

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

    /// <summary>Tên miền rút gọn dùng làm tiêu đề thay thế khi trang không cho đọc tiêu đề thật.</summary>
    private static string PrettyHost(Uri u)
    {
        var h = u.Host;
        return h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
    }

    /// <summary>Tên file từ đường dẫn URL (dùng cho link trỏ thẳng tới file); null nếu URL không có tên file.</summary>
    private static string? FileNameTitle(Uri u)
    {
        try
        {
            var n = System.Net.WebUtility.UrlDecode(Path.GetFileName(u.LocalPath));
            return string.IsNullOrWhiteSpace(n) ? null : n;
        }
        catch { return null; }
    }

    /// <summary>og:image nhỏ (&lt;300px) hoặc gần vuông vừa (&lt;600px) → coi là LOGO (không phủ đầy).</summary>
    private static bool IsLogoSized(int? w, int? h)
    {
        if (w is not { } ww || h is not { } hh || ww <= 0 || hh <= 0) return false;
        int max = Math.Max(ww, hh);
        if (max < 300) return true;
        double ratio = (double)Math.Min(ww, hh) / max;
        return max < 600 && ratio > 0.82; // gần vuông
    }

    private static (int?, int?) ReadDims(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return (null, null);
        try
        {
            using var s = File.OpenRead(path);
            using var img = System.Drawing.Image.FromStream(s, false, false);
            return (img.Width, img.Height);
        }
        catch { return (null, null); }
    }

    private static string? Clean(string? s)
    {
        if (s == null) return null;
        s = Regex.Replace(System.Net.WebUtility.HtmlDecode(s), @"\s+", " ").Trim();
        return s.Length == 0 ? null : s;
    }

    private static string Hash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }
}
