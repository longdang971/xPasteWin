using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace xPasteWin.Services;

/// <summary>
/// Đọc phần đầu của file văn bản để hiển thị preview trên card / cửa sổ preview (port TextFileReader
/// của macOS). Nhận diện file text theo phần mở rộng hoặc "sniff" (không có byte NUL ở đầu file).
/// </summary>
public static class TextFileReader
{
    public const int CardHeadBytes = 8 * 1024;      // đủ ~12 dòng trên card
    public const int PreviewHeadBytes = 256 * 1024; // bản đầy đủ hơn cho cửa sổ preview

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml",
        ".ini", ".cfg", ".conf", ".toml", ".env", ".properties",
        ".cs", ".js", ".ts", ".jsx", ".tsx", ".py", ".java", ".kt", ".kts", ".c", ".cc", ".cpp",
        ".h", ".hpp", ".m", ".mm", ".swift", ".go", ".rs", ".rb", ".php", ".pl", ".lua", ".r",
        ".sh", ".bash", ".zsh", ".bat", ".cmd", ".ps1", ".psm1", ".sql", ".gradle", ".groovy",
        ".html", ".htm", ".css", ".scss", ".less", ".vue", ".svelte", ".dart", ".scala", ".clj",
        ".ex", ".exs", ".erl", ".hs", ".fs", ".vb", ".asm", ".s", ".make", ".mk", ".cmake",
        ".dockerfile", ".gitignore", ".editorconfig", ".diff", ".patch", ".srt", ".vtt", ".tex",
    };

    /// <summary>File tồn tại VÀ được coi là văn bản (theo ext hoặc sniff nội dung).</summary>
    public static bool IsTextFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            if (!File.Exists(path)) return false;
            var ext = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext) && TextExts.Contains(ext)) return true;
            // Không rõ ext → sniff: đọc ~2KB, không có byte NUL và phần lớn in được → coi là text.
            return SniffText(path);
        }
        catch { return false; }
    }

    private static bool SniffText(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[2048];
            int n = fs.Read(buf, 0, buf.Length);
            if (n == 0) return true; // file rỗng: coi là text
            int printable = 0;
            for (int i = 0; i < n; i++)
            {
                byte b = buf[i];
                if (b == 0) return false;                    // NUL → nhị phân
                if (b == 9 || b == 10 || b == 13 || b >= 32) printable++;
            }
            return printable >= n * 0.85;
        }
        catch { return false; }
    }

    /// <summary>Đọc tối đa <paramref name="maxBytes"/> byte đầu, giải mã UTF-8 (fallback mặc định). Null nếu lỗi.</summary>
    public static string? ReadHead(string? path, int maxBytes)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            int len = (int)Math.Min(maxBytes, fs.Length);
            var buf = new byte[len];
            int read = fs.Read(buf, 0, len);
            try { return new UTF8Encoding(false, true).GetString(buf, 0, read); }
            catch { return Encoding.Default.GetString(buf, 0, read); } // không phải UTF-8 hợp lệ
        }
        catch { return null; }
    }
}
