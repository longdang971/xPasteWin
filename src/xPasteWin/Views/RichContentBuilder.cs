using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using xPasteWin.Services;

namespace xPasteWin.Views;

/// <summary>
/// Dựng rich preview (RTF/HTML) dùng chung cho card và cửa sổ preview (Space): nạp RTF vào RichEditBox,
/// và dựng Inline (màu/cỡ chữ/đậm-nghiêng/gạch chân/monospace/link) cho RichTextBlock từ HtmlSpan.
/// </summary>
internal static class RichContentBuilder
{
    /// <summary>Nạp RTF vào RichEditBox chỉ-đọc (rtf null/rỗng → xoá trắng).</summary>
    public static void ApplyRtf(RichEditBox box, string? rtf)
    {
        try
        {
            if (!string.IsNullOrEmpty(rtf))
                box.Document.SetText(Microsoft.UI.Text.TextSetOptions.FormatRtf, rtf);
            else
                box.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, "");
        }
        catch { try { box.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, ""); } catch { } }
    }

    /// <summary>Dựng Inline HTML vào RichTextBlock. defaultFg dùng cho run không khai màu. Nền TỪNG ĐOẠN
    /// (Run không có Background trong WinUI) được vẽ qua TextHighlighter theo range (best-effort).</summary>
    public static void PopulateHtml(RichTextBlock target, IReadOnlyList<HtmlSpan> spans, Brush defaultFg)
    {
        target.Blocks.Clear();
        target.TextHighlighters.Clear();
        var para = new Paragraph();
        int offset = 0;
        var bgRanges = new Dictionary<uint, List<(int start, int len)>>();
        foreach (var s in spans)
        {
            if (s.LineBreak) { para.Inlines.Add(new LineBreak()); offset += 1; continue; }
            var run = new Run { Text = s.Text };
            run.Foreground = s.Color is { } col ? new SolidColorBrush(ToColor(col)) : defaultFg;
            if (s.FontSize is { } fs) run.FontSize = fs;
            if (s.Bold) run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            if (s.Italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (s.Underline) run.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
            if (s.Mono) run.FontFamily = new FontFamily("Consolas");

            if (s.Link)
            {
                var h = new Hyperlink();
                if (s.Color is { } lc) h.Foreground = new SolidColorBrush(ToColor(lc));
                if (!string.IsNullOrEmpty(s.Href) && Uri.TryCreate(s.Href, UriKind.Absolute, out var uri))
                { try { h.NavigateUri = uri; } catch { } }
                h.Inlines.Add(run);
                para.Inlines.Add(h);
            }
            else para.Inlines.Add(run);

            if (s.Background is { } bg && s.Text.Length > 0)
            {
                if (!bgRanges.TryGetValue(bg, out var l)) bgRanges[bg] = l = new();
                l.Add((offset, s.Text.Length));
            }
            offset += s.Text.Length;
        }
        target.Blocks.Add(para);

        // Nền từng đoạn (transcript/highlight) — mỗi màu một TextHighlighter gộp các range cùng màu.
        foreach (var kv in bgRanges)
            AddBackground(target.TextHighlighters, ToColor(kv.Key), kv.Value);
    }

    // Struct TextRange của WinRT không project ra C# nên phải thao tác qua reflection.
    internal static readonly Type? RangeType =
        typeof(TextHighlighter).GetProperty("Ranges")?.PropertyType.GetGenericArguments()
            .FirstOrDefault();

    /// <summary>Thêm 1 range vào TextHighlighter.Ranges. Struct TextRange không project ra C# nên dựng
    /// qua reflection rồi thêm bằng dynamic (binder tự chọn Add(TextRange) đúng kiểu). false nếu lỗi.</summary>
    public static bool AddRange(TextHighlighter hl, int start, int len)
    {
        if (RangeType == null) return false;
        try
        {
            dynamic r = Activator.CreateInstance(RangeType)!;
            r.StartIndex = start;
            r.Length = len;
            hl.Ranges.Add(r);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Thêm 1 TextHighlighter tô nền cho danh sách range.</summary>
    private static void AddBackground(IList<TextHighlighter> highlighters, Color color, List<(int start, int len)> ranges)
    {
        if (RangeType == null || ranges.Count == 0) return;
        var hl = new TextHighlighter { Background = new SolidColorBrush(color) };
        int added = 0;
        foreach (var (start, len) in ranges)
            if (AddRange(hl, start, len)) added++;
        if (added > 0) highlighters.Add(hl);
    }

    /// <summary>Tô lại toàn bộ highlight cho RichTextBlock HTML: nền TỪNG ĐOẠN (từ spans) + đoạn khớp
    /// TỪ KHOÁ tìm kiếm (vàng). Toạ độ range tính theo cùng mô hình offset khi dựng inline (LineBreak = 1).</summary>
    public static void HighlightHtml(RichTextBlock target, IReadOnlyList<HtmlSpan> spans, string? term, Color searchBg, bool darkText)
    {
        try { target.TextHighlighters.Clear(); } catch { return; }

        var bgRanges = new Dictionary<uint, List<(int, int)>>();
        var sb = new System.Text.StringBuilder();
        int offset = 0;
        foreach (var s in spans)
        {
            if (s.LineBreak) { sb.Append('\n'); offset += 1; continue; }
            if (s.Background is { } bg && s.Text.Length > 0)
            {
                if (!bgRanges.TryGetValue(bg, out var l)) bgRanges[bg] = l = new();
                l.Add((offset, s.Text.Length));
            }
            sb.Append(s.Text);
            offset += s.Text.Length;
        }
        foreach (var kv in bgRanges)
            AddBackground(target.TextHighlighters, ToColor(kv.Key), kv.Value);

        if (!string.IsNullOrEmpty(term))
        {
            var hl = new TextHighlighter { Background = new SolidColorBrush(searchBg) };
            if (darkText) hl.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0, 0, 0));
            var text = sb.ToString();
            int idx = 0, count = 0;
            while ((idx = text.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (AddRange(hl, idx, term.Length)) count++;
                idx += term.Length;
            }
            if (count > 0) { try { target.TextHighlighters.Add(hl); } catch { } }
        }
    }

    public static Color ToColor(uint a) =>
        Color.FromArgb((byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a);
}
