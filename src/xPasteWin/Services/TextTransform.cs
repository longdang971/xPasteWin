using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using xPasteWin.Models;

namespace xPasteWin.Services;

/// <summary>
/// Bộ biến đổi văn bản cho menu "Paste as…" (port TextTransform của macOS). Mỗi mục chỉ hiện khi
/// thực sự làm THAY ĐỔI nội dung (Applicable), tránh menu rối.
/// </summary>
public sealed class TextTransform
{
    public string Name { get; }
    public Func<string, string> Apply { get; }
    public Func<string, ClipboardContentType, bool> Applicable { get; }

    private TextTransform(string name, Func<string, string> apply, Func<string, ClipboardContentType, bool> applicable)
    { Name = name; Apply = apply; Applicable = applicable; }

    public static readonly IReadOnlyList<TextTransform> All = new[]
    {
        new TextTransform("Trimmed", s => s.Trim(), (s, _) => s != s.Trim()),
        new TextTransform("Single Line", SingleLine, (s, _) => s.Contains('\n') || s.Contains('\r')),
        new TextTransform("lowercase", s => s.ToLowerInvariant(), (s, _) => s != s.ToLowerInvariant()),
        new TextTransform("UPPERCASE", s => s.ToUpperInvariant(), (s, _) => s != s.ToUpperInvariant()),
        new TextTransform("Capitalized", Capitalize,
            (s, _) => s != Capitalize(s)),
        new TextTransform("Pretty JSON", PrettyJson, (s, _) => LooksJson(s) && PrettyJson(s) != s),
        new TextTransform("Minified JSON", MinifyJson, (s, _) => LooksJson(s) && MinifyJson(s) != s),
        new TextTransform("URL Decoded", s => Uri.UnescapeDataString(s), (s, _) => s.Contains('%')),
        new TextTransform("URL Encoded", s => Uri.EscapeDataString(s),
            (s, _) => s.Length <= 2000 && Uri.EscapeDataString(s) != s),
        new TextTransform("Domain Only", DomainOnly, (s, t) => DomainOnly(s) is { Length: > 0 } d && d != s),
    };

    /// <summary>Các phép biến đổi áp dụng được cho nội dung này.</summary>
    public static IEnumerable<TextTransform> For(string? text, ClipboardContentType type)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<TextTransform>();
        return All.Where(t => { try { return t.Applicable(text, type); } catch { return false; } });
    }

    private static string SingleLine(string s)
    {
        var parts = s.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(p => p.Trim());
        return string.Join(' ', parts.Where(p => p.Length > 0));
    }

    private static string Capitalize(string s) =>
        CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

    private static bool LooksJson(string s)
    {
        s = s.TrimStart();
        return s.StartsWith('{') || s.StartsWith('[');
    }

    private static string PrettyJson(string s)
    {
        try
        {
            using var doc = JsonDocument.Parse(s);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return s; }
    }

    private static string MinifyJson(string s)
    {
        try
        {
            using var doc = JsonDocument.Parse(s);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = false });
        }
        catch { return s; }
    }

    private static string DomainOnly(string s)
    {
        var t = s.Trim();
        if (Uri.TryCreate(t, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
            return u.Host;
        return "";
    }
}
