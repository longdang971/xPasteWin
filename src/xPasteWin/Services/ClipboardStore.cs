using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using xPasteWin.Models;

namespace xPasteWin.Services;

public sealed class ClipboardStore
{
    public static readonly int[] KeepHistoryDaysByIndex = { 1, 7, 30, 365, 0 };

    private readonly ISettings _settings;
    private readonly int _maxItems;
    private readonly string _itemsDir;
    private readonly string _imagesDir;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private string _searchQuery = "";

    public ObservableCollection<ClipboardItem> Items { get; } = new();

    public string SearchQuery
    {
        get => _searchQuery;
        set => _searchQuery = value ?? "";
    }

    public ClipboardStore(ISettings settings, string? storageDir = null, int maxItems = 500)
    {
        _settings = settings;
        _maxItems = maxItems;
        storageDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xPaste");
        _itemsDir = Path.Combine(storageDir, "items");
        _imagesDir = Path.Combine(storageDir, "images");
        Directory.CreateDirectory(_itemsDir);
        Directory.CreateDirectory(_imagesDir);
        Load();
        Trim();         // cắt về <=maxItems nếu đĩa còn tồn dư item cũ (Load không tự trim)
        PruneExpired();
    }

    public string ImagePath(Guid id) => Path.Combine(_imagesDir, id + ".jpg");

    public IReadOnlyList<ClipboardItem> FilteredItems
    {
        get
        {
            var sorted = Items.OrderByDescending(i => i.IsPinned)
                              .ThenByDescending(i => i.Timestamp).ToList();
            if (string.IsNullOrEmpty(_searchQuery)) return sorted;
            return sorted.Where(Matches).ToList();
        }
    }

    /// <summary>Danh sách hiển thị theo tab (giống <c>displayedItems</c> của macOS).</summary>
    public IReadOnlyList<ClipboardItem> Displayed(ClipboardTab tab)
    {
        if (tab == ClipboardTab.All) return FilteredItems;
        var pinned = Items.Where(i => i.IsPinned)
                          .OrderByDescending(i => i.Timestamp).ToList();
        if (string.IsNullOrEmpty(_searchQuery)) return pinned;
        return pinned.Where(Matches).ToList();
    }

    private bool Matches(ClipboardItem i) => i.Type switch
    {
        ClipboardContentType.Text or ClipboardContentType.Url =>
            i.Text?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ?? false,
        ClipboardContentType.Image =>
            "image".StartsWith(_searchQuery, StringComparison.OrdinalIgnoreCase),
        _ => i.DisplayText.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase),
    };

    public void Add(ClipboardItem item)
    {
        // Dedup: bỏ item unpinned trùng cùng loại.
        var dupes = Items.Where(e => !e.IsPinned && e.Type == item.Type && IsSameContent(e, item))
                         .Select(e => e.Id).ToList();
        foreach (var id in dupes) RemoveItem(id);

        // Ghi ảnh đồng bộ (GĐ1 ưu tiên đúng đắn; chuyển async ở GĐ sau).
        if (item.ImageData is { Length: > 0 } data)
        {
            try { File.WriteAllBytes(ImagePath(item.Id), data); } catch { }
        }

        Items.Insert(0, item);
        Trim();
        PruneExpired();
        WriteMetadata(item);
    }

    private static bool IsSameContent(ClipboardItem a, ClipboardItem b) => a.Type switch
    {
        ClipboardContentType.Text or ClipboardContentType.Url => a.Text == b.Text,
        ClipboardContentType.Image => a.ImageHash != null && a.ImageHash == b.ImageHash,
        _ => a.FilePaths != null && b.FilePaths != null && a.FilePaths.SequenceEqual(b.FilePaths),
    };

    public void Delete(ClipboardItem item) => RemoveItem(item.Id);

    public void TogglePin(ClipboardItem item)
    {
        var it = Items.FirstOrDefault(i => i.Id == item.Id);
        if (it == null) return;
        it.IsPinned = !it.IsPinned;
        WriteMetadata(it);
    }

    public void MoveToTop(ClipboardItem item)
    {
        var idx = IndexOf(item.Id);
        if (idx < 0) return;
        var it = Items[idx];
        Items.RemoveAt(idx);
        it.Timestamp = DateTimeOffset.Now;
        Items.Insert(0, it);
        WriteMetadata(it);
    }

    public void DeleteItems(IEnumerable<Guid> ids)
    {
        foreach (var id in ids.ToList()) RemoveItem(id);
    }

    public void ClearUnpinned()
    {
        foreach (var id in Items.Where(i => !i.IsPinned).Select(i => i.Id).ToList())
            RemoveItem(id);
    }

    public void ClearAll()
    {
        foreach (var id in Items.Select(i => i.Id).ToList()) RemoveItem(id);
    }

    /// <summary>Xóa toàn bộ file lịch sử trên đĩa NGAY (đồng bộ, gọi được từ thread bất kỳ) — cho
    /// clear-on-sleep/logout để dữ liệu không còn khi máy treo/thoát. KHÔNG đụng ObservableCollection
    /// (phần dọn collection phải chạy trên UI thread riêng).</summary>
    public void WipeDiskFiles()
    {
        void DeleteAll(string dir, string pattern)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, pattern))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }
        DeleteAll(_itemsDir, "*.json");
        DeleteAll(_imagesDir, "*.jpg");
    }

    public void PruneExpired()
    {
        var idx = _settings.Get("keepHistoryIndex", 4);
        if (idx < 0 || idx >= KeepHistoryDaysByIndex.Length) return;
        var days = KeepHistoryDaysByIndex[idx];
        if (days <= 0) return;
        var cutoff = DateTimeOffset.Now.AddDays(-days);
        foreach (var id in Items.Where(i => !i.IsPinned && i.Timestamp < cutoff)
                                .Select(i => i.Id).ToList())
            RemoveItem(id);
    }

    private void Trim()
    {
        var unpinned = Items.Where(i => !i.IsPinned).OrderBy(i => i.Timestamp).ToList();
        if (unpinned.Count <= _maxItems) return;
        foreach (var it in unpinned.Take(unpinned.Count - _maxItems).ToList())
            RemoveItem(it.Id);
    }

    private int IndexOf(Guid id)
    {
        for (int i = 0; i < Items.Count; i++) if (Items[i].Id == id) return i;
        return -1;
    }

    private void RemoveItem(Guid id)
    {
        var idx = IndexOf(id);
        if (idx >= 0) Items.RemoveAt(idx);
        DeleteFiles(id);
    }

    private void WriteMetadata(ClipboardItem item)
    {
        var path = Path.Combine(_itemsDir, item.Id + ".json");
        try { File.WriteAllText(path, JsonSerializer.Serialize(item, JsonOpts)); } catch { }
    }

    private void DeleteFiles(Guid id)
    {
        var meta = Path.Combine(_itemsDir, id + ".json");
        var img = ImagePath(id);
        try { if (File.Exists(meta)) File.Delete(meta); } catch { }
        try { if (File.Exists(img)) File.Delete(img); } catch { }
    }

    private void Load()
    {
        if (!Directory.Exists(_itemsDir)) return;
        var loaded = new List<ClipboardItem>();
        foreach (var file in Directory.EnumerateFiles(_itemsDir, "*.json"))
        {
            try
            {
                var it = JsonSerializer.Deserialize<ClipboardItem>(File.ReadAllText(file));
                if (it != null) loaded.Add(it);
            }
            catch { }
        }
        foreach (var it in loaded.OrderByDescending(i => i.IsPinned)
                                 .ThenByDescending(i => i.Timestamp))
            Items.Add(it);
    }
}
