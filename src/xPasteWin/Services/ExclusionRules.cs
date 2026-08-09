using System;
using System.Text.RegularExpressions;

namespace xPasteWin.Services;

/// <summary>
/// "Never Save Matching Text" (port ExclusionRules của macOS): pattern là chuỗi con (không phân biệt
/// hoa thường) hoặc biểu thức chính quy nếu bọc trong <c>/…/</c>. Dùng để KHÔNG lưu nội dung nhạy cảm.
/// </summary>
public static class ExclusionRules
{
    private const int RegexScanCap = 100_000; // chặn quét regex trên chuỗi khổng lồ

    public static bool ShouldExclude(string? text, string[]? patterns)
    {
        if (string.IsNullOrEmpty(text) || patterns is not { Length: > 0 }) return false;
        foreach (var raw in patterns)
        {
            var p = raw?.Trim();
            if (string.IsNullOrEmpty(p)) continue;
            if (p.Length >= 2 && p[0] == '/' && p[^1] == '/')
            {
                var body = p[1..^1];
                if (body.Length == 0) continue;
                try
                {
                    var hay = text.Length > RegexScanCap ? text[..RegexScanCap] : text;
                    if (Regex.IsMatch(hay, body, RegexOptions.IgnoreCase)) return true;
                }
                catch { /* regex sai → bỏ qua pattern này */ }
            }
            else if (text.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Kiểm tra pattern hợp lệ (regex trong /…/ phải biên dịch được) — cho Settings báo lỗi.</summary>
    public static bool IsValid(string? pattern)
    {
        var p = pattern?.Trim();
        if (string.IsNullOrEmpty(p)) return false;
        if (p.Length >= 2 && p[0] == '/' && p[^1] == '/')
        {
            var body = p[1..^1];
            if (body.Length == 0) return false;
            try { _ = Regex.Match("", body, RegexOptions.IgnoreCase); return true; }
            catch { return false; }
        }
        return true;
    }
}
