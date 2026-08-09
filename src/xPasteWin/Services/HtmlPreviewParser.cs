using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace xPasteWin.Services;

/// <summary>Một đoạn văn bản đã gộp định dạng để dựng Inline cho RichTextBlock.</summary>
public sealed class HtmlSpan
{
    public string Text = "";
    public bool LineBreak;         // ngắt dòng (br / hết block)
    public uint? Color;            // màu chữ ARGB (null = mặc định theo nền)
    public uint? Background;       // màu nền ARGB (để suy ra fill; Run không vẽ được nền)
    public double? FontSize;       // px
    public bool Bold, Italic, Underline, Mono, Link;
    public string? Href;
}

/// <summary>
/// Parser HTML "đủ dùng" để dựng preview trên card giống macOS: giữ màu chữ, cỡ chữ, đậm/nghiêng/
/// gạch chân, monospace, link và ngắt dòng theo block. KHÔNG phải trình duyệt đầy đủ — bỏ qua CSS
/// ngoài, layout phức tạp; đủ cho các đoạn copy từ web/editor.
/// </summary>
public static class HtmlPreviewParser
{
    private sealed class Style
    {
        public uint? Color, Background;
        public double? FontSize;
        public bool Bold, Italic, Underline, Mono, Link;
        public string? Href;
        public Style Clone() => (Style)MemberwiseClone();
    }

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    { "p","div","li","ul","ol","tr","table","h1","h2","h3","h4","h5","h6","blockquote","pre","section","article","header","footer","hr" };

    private static readonly HashSet<string> MonoTags = new(StringComparer.OrdinalIgnoreCase)
    { "code","pre","tt","kbd","samp" };

    /// <summary>Parse fragment HTML → (spans, màu nền chủ đạo).</summary>
    public static (List<HtmlSpan> spans, uint? fill) Parse(string html)
    {
        // Bỏ comment / style / script / head để không lẫn text rác.
        html = Regex.Replace(html, @"<!--.*?-->", " ", RegexOptions.Singleline);
        html = Regex.Replace(html, @"<(style|script|head)\b.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Nền tài liệu: <body bgcolor> hoặc <body style="background(-color)">.
        uint? docBg = null;
        var body = Regex.Match(html, @"<body\b([^>]*)>", RegexOptions.IgnoreCase);
        if (body.Success) docBg = BgFromAttrs(body.Groups[1].Value);

        var spans = new List<HtmlSpan>();
        var stack = new Stack<Style>();
        stack.Push(new Style());

        int pos = 0;
        var tag = new Regex(@"<(/?)([a-zA-Z0-9]+)([^>]*?)/?>", RegexOptions.Singleline);
        foreach (Match m in tag.Matches(html))
        {
            if (m.Index > pos)
                EmitText(spans, html.Substring(pos, m.Index - pos), stack.Peek());
            pos = m.Index + m.Length;

            bool close = m.Groups[1].Value == "/";
            string name = m.Groups[2].Value.ToLowerInvariant();
            string attrs = m.Groups[3].Value;

            if (name is "br")
            {
                spans.Add(new HtmlSpan { LineBreak = true });
                continue;
            }
            if (name is "hr")
            {
                spans.Add(new HtmlSpan { LineBreak = true });
                continue;
            }

            if (close)
            {
                if (stack.Count > 1) stack.Pop();
                if (BlockTags.Contains(name)) spans.Add(new HtmlSpan { LineBreak = true });
            }
            else
            {
                if (BlockTags.Contains(name) && spans.Count > 0 && !spans[^1].LineBreak)
                    spans.Add(new HtmlSpan { LineBreak = true });
                stack.Push(ApplyTag(stack.Peek().Clone(), name, attrs));
            }
        }
        if (pos < html.Length) EmitText(spans, html.Substring(pos), stack.Peek());

        // Fill: nền tài liệu > nền span phổ biến nhất (theo tổng độ dài text).
        uint? fill = docBg;
        if (fill == null)
        {
            var byBg = new Dictionary<uint, int>();
            foreach (var s in spans)
                if (s.Background is { } bg && !s.LineBreak && s.Text.Length > 0)
                    byBg[bg] = byBg.GetValueOrDefault(bg) + s.Text.Length;
            if (byBg.Count > 0) fill = byBg.OrderByDescending(kv => kv.Value).First().Key;
        }

        CollapseBreaks(spans);
        return (spans, fill);
    }

    private static void EmitText(List<HtmlSpan> spans, string raw, Style st)
    {
        var text = DecodeEntities(raw);
        // Gộp khoảng trắng liên tiếp (HTML coi nhiều space là một), nhưng giữ 1 space.
        text = Regex.Replace(text, @"[ \t\r\n]+", " ");
        if (text.Length == 0) return;
        spans.Add(new HtmlSpan
        {
            Text = text,
            Color = st.Link && st.Color == null ? 0xFF3B82F6u : st.Color, // link mặc định xanh
            Background = st.Background,
            FontSize = st.FontSize,
            Bold = st.Bold, Italic = st.Italic,
            Underline = st.Underline || st.Link,
            Mono = st.Mono, Link = st.Link, Href = st.Href,
        });
    }

    private static Style ApplyTag(Style s, string name, string attrs)
    {
        switch (name)
        {
            case "b": case "strong": s.Bold = true; break;
            case "i": case "em": s.Italic = true; break;
            case "u": case "ins": s.Underline = true; break;
            case "a":
                s.Link = true;
                var href = Attr(attrs, "href");
                if (!string.IsNullOrEmpty(href)) s.Href = DecodeEntities(href);
                break;
        }
        if (MonoTags.Contains(name)) s.Mono = true;
        if (name is "h1" or "h2" or "h3") { s.Bold = true; s.FontSize ??= name == "h1" ? 20 : name == "h2" ? 17 : 15; }

        // <font color size>
        if (name == "font")
        {
            var col = Attr(attrs, "color");
            if (ParseColor(col) is { } c) s.Color = c;
        }

        // style="" inline
        var style = Attr(attrs, "style");
        if (!string.IsNullOrEmpty(style))
        {
            foreach (var decl in style.Split(';'))
            {
                var kv = decl.Split(':', 2);
                if (kv.Length != 2) continue;
                var prop = kv[0].Trim().ToLowerInvariant();
                var val = kv[1].Trim();
                switch (prop)
                {
                    case "color": if (ParseColor(val) is { } fc) s.Color = fc; break;
                    case "background-color":
                    case "background": if (ParseColor(val) is { } bc) s.Background = bc; break;
                    case "font-size": if (ParseFontSize(val) is { } fs) s.FontSize = fs; break;
                    case "font-weight":
                        if (val.Contains("bold") || (int.TryParse(val, out var w) && w >= 600)) s.Bold = true;
                        break;
                    case "font-style": if (val.Contains("italic")) s.Italic = true; break;
                    case "text-decoration": if (val.Contains("underline")) s.Underline = true; break;
                    case "font-family": if (val.Contains("mono")) s.Mono = true; break;
                }
            }
        }
        return s;
    }

    // Gộp các ngắt dòng liên tiếp + bỏ ngắt ở đầu/cuối.
    private static void CollapseBreaks(List<HtmlSpan> spans)
    {
        for (int i = spans.Count - 1; i > 0; i--)
            if (spans[i].LineBreak && spans[i - 1].LineBreak) spans.RemoveAt(i);
        while (spans.Count > 0 && spans[0].LineBreak) spans.RemoveAt(0);
        while (spans.Count > 0 && spans[^1].LineBreak) spans.RemoveAt(spans.Count - 1);
    }

    private static uint? BgFromAttrs(string attrs)
    {
        if (ParseColor(Attr(attrs, "bgcolor")) is { } c) return c;
        var style = Attr(attrs, "style");
        if (!string.IsNullOrEmpty(style))
        {
            var m = Regex.Match(style, @"background(?:-color)?\s*:\s*([^;]+)", RegexOptions.IgnoreCase);
            if (m.Success && ParseColor(m.Groups[1].Value.Trim()) is { } bc) return bc;
        }
        return null;
    }

    private static string Attr(string attrs, string name)
    {
        var m = Regex.Match(attrs, name + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        if (!m.Success) return "";
        return m.Groups[1].Success ? m.Groups[1].Value
             : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
    }

    private static double? ParseFontSize(string v)
    {
        v = v.Trim().ToLowerInvariant();
        var m = Regex.Match(v, @"([\d.]+)\s*(px|pt|em|rem|%)?");
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return v switch { "small" => 11, "medium" => 13, "large" => 16, "x-large" => 20, _ => (double?)null };
        double px = m.Groups[2].Value switch
        {
            "pt" => n * 96.0 / 72.0,
            "em" or "rem" => n * 13.0,
            "%" => 13.0 * n / 100.0,
            _ => n,
        };
        return Math.Clamp(px, 8, 40);
    }

    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = 0xFF000000, ["white"] = 0xFFFFFFFF, ["red"] = 0xFFFF0000, ["green"] = 0xFF008000,
        ["lime"] = 0xFF00FF00, ["blue"] = 0xFF0000FF, ["yellow"] = 0xFFFFFF00, ["cyan"] = 0xFF00FFFF,
        ["aqua"] = 0xFF00FFFF, ["magenta"] = 0xFFFF00FF, ["fuchsia"] = 0xFFFF00FF, ["gray"] = 0xFF808080,
        ["grey"] = 0xFF808080, ["silver"] = 0xFFC0C0C0, ["maroon"] = 0xFF800000, ["olive"] = 0xFF808000,
        ["navy"] = 0xFF000080, ["teal"] = 0xFF008080, ["purple"] = 0xFF800080, ["orange"] = 0xFFFFA500,
    };

    private static uint? ParseColor(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        if (v.StartsWith('#'))
        {
            var h = v[1..];
            if (h.Length == 3) h = "" + h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
            if (h.Length == 6 && uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return 0xFF000000u | rgb;
            return null;
        }
        var m = Regex.Match(v, @"rgba?\(\s*(\d+)\D+(\d+)\D+(\d+)", RegexOptions.IgnoreCase);
        if (m.Success)
            return 0xFF000000u | ((uint)int.Parse(m.Groups[1].Value) << 16)
                 | ((uint)int.Parse(m.Groups[2].Value) << 8) | (uint)int.Parse(m.Groups[3].Value);
        return Named.TryGetValue(v, out var c) ? c : null;
    }

    private static string DecodeEntities(string s)
    {
        if (s.IndexOf('&') < 0) return s;
        s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
             .Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'");
        s = Regex.Replace(s, @"&#(\d+);", mm => SafeChar(int.Parse(mm.Groups[1].Value)));
        s = Regex.Replace(s, @"&#x([0-9a-fA-F]+);", mm => SafeChar(Convert.ToInt32(mm.Groups[1].Value, 16)));
        return s;
    }

    private static string SafeChar(int code)
    {
        try { return char.ConvertFromUtf32(code); } catch { return ""; }
    }
}
