using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using xPasteWin.Models;

namespace xPasteWin.Services;

/// <summary>
/// Phân tích RTF của item để dựng "rich preview" trên card (giống RichTextRenderer của macOS):
/// trích MÀU NỀN (document background <c>\viewbkcol</c>, hoặc nền run phổ biến nhất như nền đen của
/// Terminal) để tô nền card, còn chữ/màu/font/link do RichEditBox tự render từ chính RTF.
/// HTML tạm chưa hỗ trợ (RichEditBox chỉ nạp RTF) → rơi về text thường.
/// </summary>
public static class RichPreviewService
{
    // Giới hạn kích thước để không làm chậm panel (RichEditBox nạp RTF lớn / parse HTML lớn đều nặng).
    private const int RtfByteCap = 256 * 1024;
    private const int HtmlByteCap = 256 * 1024;

    public enum RichKind { Rtf, Html }

    public sealed class RichInfo
    {
        public RichKind Kind { get; init; }
        public string Rtf { get; init; } = "";                                   // khi Kind==Rtf
        public IReadOnlyList<HtmlSpan> Html { get; init; } = Array.Empty<HtmlSpan>(); // khi Kind==Html
        public uint? FillArgb { get; init; } // màu nền card (null = giữ nền mặc định)
    }

    /// <summary>Trả RichInfo nếu item có RTF/HTML hợp lệ (đủ nhỏ); null nếu không / quá lớn.</summary>
    public static RichInfo? Analyze(ClipboardItem item)
    {
        if (item.RichType == "html") return AnalyzeHtml(item);
        if (item.RichType != "rtf" || item.RichData is not { Length: > 0 }) return null;
        if (item.RichData.Length > RtfByteCap) return null;
        string rtf;
        try { rtf = Encoding.UTF8.GetString(item.RichData); } catch { return null; }
        if (string.IsNullOrEmpty(rtf)) return null;

        var colors = ParseColorTable(rtf);
        uint? fill = null;

        // 1) Nền tài liệu: \viewbkcolN → chỉ số màu trong bảng.
        var mv = Regex.Match(rtf, @"\\viewbkcol(\d+)");
        if (mv.Success) fill = ColorAt(colors, int.Parse(mv.Groups[1].Value));

        // 2) Không có → nền run phổ biến nhất (\chcbpat / \cb / \highlight): Terminal tô nền mọi ô
        //    cùng một màu (đen) nên màu đó thắng → card ra nền đen như mong đợi.
        if (fill == null)
        {
            var counts = new Dictionary<int, int>();
            foreach (Match m in Regex.Matches(rtf, @"\\(?:chcbpat|highlight|cb)(\d+)"))
            {
                int idx = int.Parse(m.Groups[1].Value);
                if (idx <= 0) continue;
                counts[idx] = counts.GetValueOrDefault(idx) + 1;
            }
            if (counts.Count > 0)
                fill = ColorAt(colors, counts.OrderByDescending(kv => kv.Value).First().Key);
        }

        return new RichInfo { Kind = RichKind.Rtf, Rtf = rtf, FillArgb = fill };
    }

    private static RichInfo? AnalyzeHtml(ClipboardItem item)
    {
        if (item.RichData is not { Length: > 0 } || item.RichData.Length > HtmlByteCap) return null;
        string html;
        try { html = Encoding.UTF8.GetString(item.RichData); } catch { return null; }
        if (string.IsNullOrWhiteSpace(html)) return null;

        var (spans, fill) = HtmlPreviewParser.Parse(html);
        // Không có gì để hiện (chỉ toàn khoảng trắng/thẻ) → để rơi về text thường.
        if (spans.Count == 0 || spans.All(s => s.LineBreak || string.IsNullOrWhiteSpace(s.Text)))
            return null;
        return new RichInfo { Kind = RichKind.Html, Html = spans, FillArgb = fill };
    }

    /// <summary>Bảng màu RTF: index → ARGB (null = màu "auto"/rỗng). Index 0 thường là auto.</summary>
    private static List<uint?> ParseColorTable(string rtf)
    {
        var list = new List<uint?>();
        int i = rtf.IndexOf("\\colortbl", StringComparison.Ordinal);
        if (i < 0) return list;
        int end = rtf.IndexOf('}', i);
        if (end < 0) return list;
        string body = rtf.Substring(i + "\\colortbl".Length, end - (i + "\\colortbl".Length));

        foreach (var seg in body.Split(';'))
        {
            var r = Regex.Match(seg, @"\\red(\d+)");
            var g = Regex.Match(seg, @"\\green(\d+)");
            var b = Regex.Match(seg, @"\\blue(\d+)");
            if (r.Success && g.Success && b.Success)
                list.Add(0xFF000000u
                    | ((uint)int.Parse(r.Groups[1].Value) << 16)
                    | ((uint)int.Parse(g.Groups[1].Value) << 8)
                    | (uint)int.Parse(b.Groups[1].Value));
            else
                list.Add(null); // ô rỗng = auto
        }
        return list;
    }

    private static uint? ColorAt(List<uint?> colors, int idx) =>
        idx >= 0 && idx < colors.Count ? colors[idx] : null;
}
