using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using xPasteWin.Models;
using xPasteWin.Services;
using Xunit;

public class ClipboardStoreTests
{
    private sealed class FakeSettings : ISettings
    {
        private readonly Dictionary<string, object?> _m = new();
        public T Get<T>(string k, T f) => _m.TryGetValue(k, out var v) ? (T)v! : f;
        public void Set<T>(string k, T v) => _m[k] = v;
    }

    private static ClipboardStore NewStore(out string dir, FakeSettings? s = null)
    {
        dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        return new ClipboardStore(s ?? new FakeSettings(), dir);
    }

    private static ClipboardItem Text(string t) =>
        new() { Type = ClipboardContentType.Text, Text = t };

    [Fact]
    public void Add_inserts_newest_first()
    {
        var store = NewStore(out _);
        store.Add(Text("a"));
        store.Add(Text("b"));
        Assert.Equal("b", store.Items[0].Text);
        Assert.Equal(2, store.Items.Count);
    }

    [Fact]
    public void Add_dedups_identical_unpinned_text()
    {
        var store = NewStore(out _);
        store.Add(Text("same"));
        store.Add(Text("same"));
        Assert.Single(store.Items);
    }

    [Fact]
    public void FilteredItems_pinned_first_then_by_time()
    {
        var store = NewStore(out _);
        store.Add(Text("old"));
        store.Add(Text("new"));
        store.TogglePin(store.Items.First(i => i.Text == "old"));
        Assert.Equal("old", store.FilteredItems[0].Text); // pinned lên đầu
    }

    [Fact]
    public void FilteredItems_filters_by_search_query()
    {
        var store = NewStore(out _);
        store.Add(Text("hello world"));
        store.Add(Text("goodbye"));
        store.SearchQuery = "hello";
        Assert.Single(store.FilteredItems);
        Assert.Equal("hello world", store.FilteredItems[0].Text);
    }

    [Fact]
    public void Trim_keeps_pinned_and_caps_unpinned()
    {
        var s = new FakeSettings(); s.Set("keepHistoryIndex", 4); // Forever
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var store = new ClipboardStore(s, dir, maxItems: 3);
        for (int i = 0; i < 5; i++) store.Add(Text($"t{i}"));
        Assert.Equal(3, store.Items.Count);
        Assert.Equal("t4", store.Items[0].Text); // mới nhất còn
    }

    [Fact]
    public void ClearUnpinned_removes_only_unpinned()
    {
        var store = NewStore(out _);
        store.Add(Text("keep"));
        store.Add(Text("drop"));
        store.TogglePin(store.Items.First(i => i.Text == "keep"));
        store.ClearUnpinned();
        Assert.Single(store.Items);
        Assert.Equal("keep", store.Items[0].Text);
    }

    [Fact]
    public void Reload_from_disk_restores_items()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var s = new FakeSettings();
        new ClipboardStore(s, dir).Add(Text("persisted"));
        var reopened = new ClipboardStore(s, dir);
        Assert.Contains(reopened.Items, i => i.Text == "persisted");
    }

    [Fact]
    public void PruneExpired_removes_old_unpinned_only()
    {
        var s = new FakeSettings(); s.Set("keepHistoryIndex", 0); // 1 ngày
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var store = new ClipboardStore(s, dir);
        var old = new ClipboardItem
        {
            Type = ClipboardContentType.Text,
            Text = "old",
            Timestamp = DateTimeOffset.Now.AddDays(-2)
        };
        store.Add(old);
        store.PruneExpired();
        Assert.DoesNotContain(store.Items, i => i.Text == "old");
    }
}
