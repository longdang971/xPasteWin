using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using xPasteWin.Models;
using xPasteWin.Services;
using Xunit;

namespace xPasteWin.Tests;

public class DisplayedTabTests
{
    private sealed class FakeSettings : ISettings
    {
        private readonly Dictionary<string, object?> _m = new();
        public T Get<T>(string k, T f) => _m.TryGetValue(k, out var v) ? (T)v! : f;
        public void Set<T>(string k, T v) => _m[k] = v;
    }

    private static ClipboardStore NewStore() =>
        new(new FakeSettings(), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

    private static ClipboardItem Text(string t) =>
        new() { Type = ClipboardContentType.Text, Text = t };

    [Fact]
    public void All_tab_returns_all_items()
    {
        var s = NewStore();
        s.Add(Text("a")); s.Add(Text("b"));
        Assert.Equal(2, s.Displayed(ClipboardTab.All).Count);
    }

    [Fact]
    public void Pinned_tab_returns_only_pinned()
    {
        var s = NewStore();
        s.Add(Text("a")); s.Add(Text("b"));
        s.TogglePin(s.Items.First(i => i.Text == "a"));
        var pinned = s.Displayed(ClipboardTab.Pinned);
        Assert.Single(pinned);
        Assert.Equal("a", pinned[0].Text);
    }

    [Fact]
    public void Pinned_tab_empty_when_none_pinned()
    {
        var s = NewStore();
        s.Add(Text("a"));
        Assert.Empty(s.Displayed(ClipboardTab.Pinned));
    }

    [Fact]
    public void Pinned_tab_applies_search_query()
    {
        var s = NewStore();
        s.Add(Text("hello")); s.Add(Text("world"));
        s.TogglePin(s.Items.First(i => i.Text == "hello"));
        s.TogglePin(s.Items.First(i => i.Text == "world"));
        s.SearchQuery = "hell";
        var pinned = s.Displayed(ClipboardTab.Pinned);
        Assert.Single(pinned);
        Assert.Equal("hello", pinned[0].Text);
    }
}
