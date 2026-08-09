using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using xPasteWin.Models;

namespace xPasteWin.Services;

/// <summary>
/// Loại item ĐÚNG NHƯ popover filter trình bày — khác <see cref="ClipboardContentType"/>: mã màu được
/// lưu dưới dạng text nhưng đọc (và được đặt tên trên card) là màu. Port FilterType của macOS.
/// </summary>
public enum FilterType { Text, Link, Image, Color, File, Folder }

/// <summary>Khoảng thời gian popover đưa ra. CHỈ chọn một: "Hôm nay" + "Tuần trước" cùng lúc thì vô nghĩa.</summary>
public enum DateFilter { Today, Yesterday, ThisWeek, LastWeek, Last30Days }

public static class FilterTypeInfo
{
    public static readonly FilterType[] All =
        { FilterType.Text, FilterType.Link, FilterType.Image, FilterType.Color, FilterType.File, FilterType.Folder };

    public static string Title(this FilterType t) => t switch
    {
        FilterType.Text => "Text",
        FilterType.Link => "Link",
        FilterType.Image => "Image",
        FilterType.Color => "Color",
        FilterType.File => "File",
        _ => "Folder",
    };

    /// <summary>Glyph Segoe Fluent Icons thay cho SF Symbol của bản macOS. Viết dạng \uXXXX thay vì
    /// dán ký tự thật: ký tự vùng Private Use vô hình trong editor lẫn diff, sửa nhầm không ai thấy.</summary>
    public static string Glyph(this FilterType t) => t switch
    {
        FilterType.Text => "\uE8E4",    // AlignLeft — cùng glyph badge "text thô" trên card
        FilterType.Link => "\uE71B",    // Link
        FilterType.Image => "\uE91B",   // Photo
        FilterType.Color => "\uE790",   // Color
        FilterType.File => "\uE8A5",    // Document
        _ => "\uE8B7",                  // Folder
    };

    /// <summary>Glyph mục Date (token + chip) và nút filter trên ô search.</summary>
    public const string CalendarGlyph = "\uE787";
    public const string FilterGlyph = "\uE71C";
    /// <summary>Dấu X hiện khi hover lên token, và dấu + của chip đếm overflow.</summary>
    public const string CloseGlyph = "\uE711";
    public const string PlusGlyph = "\uE710";

    public static bool Matches(this FilterType t, ClipboardItem i) => t switch
    {
        FilterType.Text => i.Type == ClipboardContentType.Text && ColorParser.Parse(i.Text) == null,
        FilterType.Color => i.Type == ClipboardContentType.Text && ColorParser.Parse(i.Text) != null,
        FilterType.Link => i.Type == ClipboardContentType.Url,
        FilterType.Image => i.Type == ClipboardContentType.Image,
        FilterType.File => i.Type == ClipboardContentType.File,
        _ => i.Type == ClipboardContentType.Folder,
    };
}

public static class DateFilterInfo
{
    public static readonly DateFilter[] All =
        { DateFilter.Today, DateFilter.Yesterday, DateFilter.ThisWeek, DateFilter.LastWeek, DateFilter.Last30Days };

    public static string Title(this DateFilter d) => d switch
    {
        DateFilter.Today => "Today",
        DateFilter.Yesterday => "Yesterday",
        DateFilter.ThisWeek => "This week",
        DateFilter.LastWeek => "Last week",
        _ => "Last 30 days",
    };

    /// <summary>Khoảng mà filter phủ, tính tương đối với <paramref name="now"/>. Tuần bắt đầu theo
    /// ngày đầu tuần của locale hệ thống (giống Calendar.current của macOS).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Interval(this DateFilter d, DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.Date, now.Offset);
        switch (d)
        {
            case DateFilter.Today:
                return (today, today.AddDays(1));
            case DateFilter.Yesterday:
                return (today.AddDays(-1), today);
            case DateFilter.ThisWeek:
            {
                var start = WeekStart(today);
                return (start, start.AddDays(7));
            }
            case DateFilter.LastWeek:
            {
                var start = WeekStart(today).AddDays(-7);
                return (start, start.AddDays(7));
            }
            default:
                return (now.AddDays(-30), now);
        }
    }

    private static DateTimeOffset WeekStart(DateTimeOffset day)
    {
        var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        int delta = ((int)day.DayOfWeek - (int)first + 7) % 7;
        return day.AddDays(-delta);
    }

    public static bool Matches(this DateFilter d, ClipboardItem i, DateTimeOffset now)
    {
        var (start, end) = d.Interval(now);
        return i.Timestamp >= start && i.Timestamp < end;
    }
}

/// <summary>Một app có mặt trong lịch sử, sẵn sàng vẽ thành chip: khoá là đường dẫn exe (vai trò như
/// bundle ID của macOS), kèm tên hiển thị và PNG icon đã trích.</summary>
/// <param name="ExePath">Đường dẫn exe — trùng với <see cref="ClipboardItem.SourceApp"/>.</param>
public sealed record FilterApp(string ExePath, string Name, string? IconPath);

/// <summary>
/// Những gì popover filter đã bật. Trong CÙNG một mục các lựa chọn là OR (Image *hoặc* Link); giữa
/// các mục là AND (một Image **từ Chrome** **hôm nay**). Port SearchFilters của macOS.
/// </summary>
public sealed class SearchFilters
{
    /// <summary>Đường dẫn exe của app nguồn — vai trò như bundle ID trên macOS.</summary>
    public HashSet<string> Apps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<FilterType> Types { get; } = new();
    public DateFilter? Date { get; private set; }

    /// <summary>Phát mỗi khi tập filter đổi — store xoá cache, panel dựng lại card.</summary>
    public event Action? Changed;

    public bool IsEmpty => Types.Count == 0 && Apps.Count == 0 && Date == null;
    public int ActiveCount => Types.Count + Apps.Count + (Date == null ? 0 : 1);

    public void Toggle(FilterType type)
    {
        if (!Types.Remove(type)) Types.Add(type);
        Changed?.Invoke();
    }

    public void ToggleApp(string exePath)
    {
        if (!Apps.Remove(exePath)) Apps.Add(exePath);
        Changed?.Invoke();
    }

    public void ToggleDate(DateFilter value)
    {
        Date = Date == value ? null : value;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (IsEmpty) return;
        Types.Clear();
        Apps.Clear();
        Date = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Gỡ token mà Backspace trong ô search phải xoá: token CUỐI trên màn hình — date trước, rồi app
    /// cuối, rồi type cuối, đúng thứ tự hàng token vẽ ra. <paramref name="appName"/> đổi đường dẫn exe
    /// thành tên hiển thị để thứ tự app khớp với những gì đang thấy.
    /// </summary>
    public void RemoveLastToken(Func<string, string?> appName)
    {
        if (Date != null) { Date = null; Changed?.Invoke(); return; }

        var lastApp = Apps.OrderBy(a => appName(a) ?? a, StringComparer.CurrentCultureIgnoreCase).LastOrDefault();
        if (lastApp != null) { Apps.Remove(lastApp); Changed?.Invoke(); return; }

        // FilterTypeInfo.All theo đúng thứ tự vẽ; lấy cái cuối đang bật.
        for (int k = FilterTypeInfo.All.Length - 1; k >= 0; k--)
            if (Types.Remove(FilterTypeInfo.All[k])) { Changed?.Invoke(); return; }
    }

    public bool Matches(ClipboardItem i, DateTimeOffset now)
    {
        if (Types.Count > 0 && !Types.Any(t => t.Matches(i))) return false;
        if (Apps.Count > 0 && (i.SourceApp == null || !Apps.Contains(i.SourceApp))) return false;
        if (Date is { } d && !d.Matches(i, now)) return false;
        return true;
    }
}
