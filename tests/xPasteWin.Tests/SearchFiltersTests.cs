using System;
using xPasteWin.Models;
using xPasteWin.Services;
using Xunit;

namespace xPasteWin.Tests;

/// <summary>Port SearchFiltersTests.swift: ngữ nghĩa OR trong mục / AND giữa các mục, khoảng ngày,
/// và thứ tự gỡ token của Backspace.</summary>
public class SearchFiltersTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static ClipboardItem Item(ClipboardContentType type, string? text = null,
                                      string? app = null, DateTimeOffset? at = null) =>
        new() { Type = type, Text = text, SourceApp = app, Timestamp = at ?? Now };

    [Fact]
    public void Empty_filters_match_everything()
    {
        var f = new SearchFilters();
        Assert.True(f.IsEmpty);
        Assert.True(f.Matches(Item(ClipboardContentType.Text, "hi"), Now));
    }

    [Fact]
    public void Type_filter_matches_only_that_type()
    {
        var f = new SearchFilters();
        f.Toggle(FilterType.Image);
        Assert.True(f.Matches(Item(ClipboardContentType.Image), Now));
        Assert.False(f.Matches(Item(ClipboardContentType.Text, "hi"), Now));
    }

    [Fact]
    public void Types_are_or_ed_together()
    {
        var f = new SearchFilters();
        f.Toggle(FilterType.Image);
        f.Toggle(FilterType.Link);
        Assert.True(f.Matches(Item(ClipboardContentType.Image), Now));
        Assert.True(f.Matches(Item(ClipboardContentType.Url, "https://x.com"), Now));
        Assert.False(f.Matches(Item(ClipboardContentType.Text, "hi"), Now));
    }

    [Fact]
    public void Color_and_text_are_distinct_types()
    {
        var colorOnly = new SearchFilters();
        colorOnly.Toggle(FilterType.Color);
        var textOnly = new SearchFilters();
        textOnly.Toggle(FilterType.Text);

        var color = Item(ClipboardContentType.Text, "#1e90ff");
        var prose = Item(ClipboardContentType.Text, "hello");

        Assert.True(colorOnly.Matches(color, Now));
        Assert.False(colorOnly.Matches(prose, Now));
        Assert.True(textOnly.Matches(prose, Now));
        Assert.False(textOnly.Matches(color, Now));
    }

    [Fact]
    public void App_filter()
    {
        var f = new SearchFilters();
        f.ToggleApp(@"C:\chrome.exe");
        Assert.True(f.Matches(Item(ClipboardContentType.Text, "a", @"C:\chrome.exe"), Now));
        Assert.False(f.Matches(Item(ClipboardContentType.Text, "a", @"C:\notepad.exe"), Now));
        Assert.False(f.Matches(Item(ClipboardContentType.Text, "a"), Now));
    }

    [Fact]
    public void Sections_are_and_ed_together()
    {
        var f = new SearchFilters();
        f.Toggle(FilterType.Image);
        f.ToggleApp(@"C:\chrome.exe");
        Assert.True(f.Matches(Item(ClipboardContentType.Image, app: @"C:\chrome.exe"), Now));
        Assert.False(f.Matches(Item(ClipboardContentType.Image, app: @"C:\notepad.exe"), Now));
    }

    [Fact]
    public void Today_filter()
    {
        var f = new SearchFilters();
        f.ToggleDate(DateFilter.Today);
        Assert.True(f.Matches(Item(ClipboardContentType.Text, "now"), Now));
        Assert.False(f.Matches(Item(ClipboardContentType.Text, "old", at: Now.AddDays(-2)), Now));
    }

    [Fact]
    public void Yesterday_filter_excludes_today()
    {
        var f = new SearchFilters();
        f.ToggleDate(DateFilter.Yesterday);
        var yesterdayNoon = new DateTimeOffset(Now.Date, Now.Offset).AddDays(-1).AddHours(12);
        Assert.True(f.Matches(Item(ClipboardContentType.Text, "y", at: yesterdayNoon), Now));
        Assert.False(f.Matches(Item(ClipboardContentType.Text, "t"), Now));
    }

    [Fact]
    public void Last30Days_filter()
    {
        var f = new SearchFilters();
        f.ToggleDate(DateFilter.Last30Days);
        Assert.True(f.Matches(Item(ClipboardContentType.Text, "recent", at: Now.AddDays(-10)), Now));
        Assert.False(f.Matches(Item(ClipboardContentType.Text, "ancient", at: Now.AddDays(-365)), Now));
    }

    [Fact]
    public void Date_is_single_choice()
    {
        var f = new SearchFilters();
        f.ToggleDate(DateFilter.Today);
        f.ToggleDate(DateFilter.Yesterday);
        Assert.Equal(DateFilter.Yesterday, f.Date);

        f.ToggleDate(DateFilter.Yesterday);
        Assert.Null(f.Date);
    }

    [Fact]
    public void Toggle_off_removes_selection()
    {
        var f = new SearchFilters();
        f.Toggle(FilterType.Image);
        f.Toggle(FilterType.Image);
        Assert.Empty(f.Types);
        Assert.True(f.IsEmpty);
    }

    [Fact]
    public void RemoveLastToken_drops_the_date_first()
    {
        var f = new SearchFilters();
        f.Toggle(FilterType.Image);
        f.ToggleApp(@"C:\chrome.exe");
        f.ToggleDate(DateFilter.Today);

        f.RemoveLastToken(_ => null);

        Assert.Null(f.Date);
        Assert.Equal(new[] { @"C:\chrome.exe" }, f.Apps);
        Assert.Equal(new[] { FilterType.Image }, f.Types);
    }

    [Fact]
    public void RemoveLastToken_then_drops_the_last_app_by_display_name()
    {
        var f = new SearchFilters();
        f.Toggle(FilterType.Image);
        f.ToggleApp(@"C:\a.exe");   // "Alpha"
        f.ToggleApp(@"C:\z.exe");   // "Zulu" — token cuối trên màn hình

        f.RemoveLastToken(p => p == @"C:\a.exe" ? "Alpha" : "Zulu");

        Assert.Equal(new[] { @"C:\a.exe" }, f.Apps);
        Assert.Equal(new[] { FilterType.Image }, f.Types);
    }

    [Fact]
    public void RemoveLastToken_finally_drops_the_last_type()
    {
        var f = new SearchFilters();
        f.Toggle(FilterType.Text);     // vẽ trước
        f.Toggle(FilterType.Folder);   // vẽ sau cùng

        f.RemoveLastToken(_ => null);

        Assert.Equal(new[] { FilterType.Text }, f.Types);
    }

    [Fact]
    public void RemoveLastToken_on_empty_filters_does_nothing()
    {
        var f = new SearchFilters();
        f.RemoveLastToken(_ => null);
        Assert.True(f.IsEmpty);
    }

    [Fact]
    public void Repeated_removeLastToken_empties_everything()
    {
        var f = new SearchFilters();
        f.Toggle(FilterType.Image);
        f.Toggle(FilterType.Link);
        f.ToggleApp(@"C:\chrome.exe");
        f.ToggleDate(DateFilter.Today);

        for (int k = 0; k < 4; k++) f.RemoveLastToken(_ => null);

        Assert.True(f.IsEmpty);
    }

    [Fact]
    public void Clear_resets_every_section()
    {
        var f = new SearchFilters();
        f.Toggle(FilterType.Image);
        f.ToggleApp(@"C:\chrome.exe");
        f.ToggleDate(DateFilter.Today);
        Assert.Equal(3, f.ActiveCount);

        f.Clear();

        Assert.True(f.IsEmpty);
    }

    [Fact]
    public void Changed_fires_on_every_mutation()
    {
        // Panel dựng lại card + hàng token nhờ đúng sự kiện này; im lặng một nhịp là danh sách lệch.
        var f = new SearchFilters();
        int count = 0;
        f.Changed += () => count++;

        f.Toggle(FilterType.Image);
        f.ToggleApp(@"C:\chrome.exe");
        f.ToggleDate(DateFilter.Today);
        f.RemoveLastToken(_ => null);
        f.Clear();

        Assert.Equal(5, count);
    }

    [Fact]
    public void Clear_on_empty_filters_stays_quiet()
    {
        // Đóng ô search luôn gọi Clear(); phát sự kiện khi chẳng có gì để xoá sẽ bắt panel dựng lại
        // toàn bộ card mỗi lần đóng search.
        var f = new SearchFilters();
        int count = 0;
        f.Changed += () => count++;

        f.Clear();

        Assert.Equal(0, count);
    }
}
