using System;
using System.Collections.Generic;
using System.Linq;
using xPasteWin.Models;

namespace xPasteWin.Services;

/// <summary>
/// Phân tích chuỗi tìm kiếm thành bộ lọc loại (img:/url:/text:/file:/folder:/color:, type:X),
/// bộ lọc app (app:chrome, AND nhiều app) và phần free-text còn lại (port SearchQuery của macOS).
/// Free-text khớp trên label / text / đường dẫn / OCR; chỉ free-text được tô vàng highlight.
/// </summary>
public sealed class SearchFilter
{
    public HashSet<ClipboardContentType> Types { get; } = new();
    public bool Color { get; private set; }        // token color: (text là mã màu)
    public List<string> Apps { get; } = new();
    public string FreeText { get; private set; } = "";

    public bool IsEmpty => Types.Count == 0 && !Color && Apps.Count == 0 && FreeText.Length == 0;

    private static readonly Dictionary<string, ClipboardContentType> TypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["img"] = ClipboardContentType.Image, ["image"] = ClipboardContentType.Image, ["photo"] = ClipboardContentType.Image,
        ["url"] = ClipboardContentType.Url, ["link"] = ClipboardContentType.Url,
        ["text"] = ClipboardContentType.Text, ["txt"] = ClipboardContentType.Text,
        ["file"] = ClipboardContentType.File, ["doc"] = ClipboardContentType.File,
        ["folder"] = ClipboardContentType.Folder, ["dir"] = ClipboardContentType.Folder,
    };

    public static SearchFilter Parse(string? raw)
    {
        var q = new SearchFilter();
        var free = new List<string>();
        foreach (var tok in (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = tok.IndexOf(':');
            if (colon > 0)
            {
                var key = tok[..colon];
                var val = tok[(colon + 1)..];
                if (string.Equals(key, "app", StringComparison.OrdinalIgnoreCase))
                {
                    if (val.Length > 0) q.Apps.Add(val);
                    continue;
                }
                if (string.Equals(key, "type", StringComparison.OrdinalIgnoreCase))
                {
                    q.AddType(val);
                    continue;
                }
                if (string.Equals(key, "color", StringComparison.OrdinalIgnoreCase))
                {
                    q.Color = true;
                    if (val.Length > 0) free.Add(val);
                    continue;
                }
                if (TypeMap.ContainsKey(key))
                {
                    q.Types.Add(TypeMap[key]);
                    if (val.Length > 0) free.Add(val);
                    continue;
                }
                free.Add(tok); // key lạ → giữ nguyên làm free text
            }
            else free.Add(tok);
        }
        q.FreeText = string.Join(' ', free);
        return q;
    }

    private void AddType(string val)
    {
        if (string.Equals(val, "color", StringComparison.OrdinalIgnoreCase)) { Color = true; return; }
        if (TypeMap.TryGetValue(val, out var t)) Types.Add(t);
    }

    public bool Matches(ClipboardItem i)
    {
        if (Types.Count > 0 || Color)
        {
            bool ok = Types.Contains(i.Type);
            if (Color && i.Type == ClipboardContentType.Text && ColorParser.Parse(i.Text) != null) ok = true;
            if (!ok) return false;
        }
        foreach (var app in Apps)
            if (!AppMatches(i, app)) return false;
        if (FreeText.Length > 0)
            return Haystack(i).Contains(FreeText, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static bool AppMatches(ClipboardItem i, string app)
    {
        if (string.IsNullOrEmpty(i.SourceApp)) return false;
        string name;
        try { name = System.IO.Path.GetFileNameWithoutExtension(i.SourceApp); }
        catch { name = i.SourceApp; }
        return name.Contains(app, StringComparison.OrdinalIgnoreCase);
    }

    private static string Haystack(ClipboardItem i) => i.Type switch
    {
        ClipboardContentType.Text or ClipboardContentType.Url => (i.Label ?? "") + " " + (i.Text ?? ""),
        ClipboardContentType.Image => (i.Label ?? "") + " " + (i.OcrText ?? ""),
        _ => (i.Label ?? "") + " " + i.DisplayText + " " +
             (i.FilePaths != null ? string.Join(' ', i.FilePaths) : ""),
    };
}
