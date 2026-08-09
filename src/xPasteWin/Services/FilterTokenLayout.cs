using System;
using System.Collections.Generic;
using System.Linq;

namespace xPasteWin.Services;

/// <summary>
/// Hàng token filter rộng bao nhiêu, và bao nhiêu token thì vừa. Port FilterTokenLayout của macOS.
///
/// Bảy filter đang bật lấp kín ô search từ mép này sang mép kia, chừa cho chỗ gõ đúng một khe. Số
/// vượt ngân sách được gom sau một chip đếm.
///
/// ĐO chứ không ĐẾM: token filter app mang tên thật của app, "Zalo" và "Visual Studio Code" không hề
/// bằng nhau, nên quy tắc kiểu "hiện 3 cái đầu" sai ở cả hai phía.
///
/// Nằm ở Services (không dùng kiểu WinUI nào) để phép tính này test được mà không cần dựng UI: hàm đo
/// bề rộng nhãn được TIÊM vào — app tiêm hàm đo bằng TextBlock thật, test tiêm hàm xác định.
/// </summary>
public static class FilterTokenLayout
{
    /// <summary>Mọi thứ token bọc quanh nhãn: padding trái 4, icon 14, spacing 4 của StackPanel,
    /// padding phải 7.</summary>
    public const double TokenChrome = 29;
    /// <summary>Khoảng cách giữa hai token — spacing của chính hàng token.</summary>
    public const double Spacing = 5;
    /// <summary>Chip "+3": dấu cộng nhỏ, con số, và padding bằng token.</summary>
    public const double CounterWidth = 34;

    /// <summary>
    /// Bề rộng hàng token được phép chiếm trước khi ô gõ bắt đầu chịu thiệt.
    ///
    /// Ô search rộng 460 (SearchBar), trong đó hai tab compact chiếm 78, padding của ô 20, kính lúp
    /// ~20 và nút filter ~28. Chừa cho ô gõ ~150 thì phần còn lại là ngân sách này. (Bản macOS để 234
    /// vì thanh của nó rộng 540.)
    /// </summary>
    public const double RowBudget = 164;

    /// <summary>Hàm đo bề rộng nhãn 12px. App gán bằng phép đo TextBlock lúc dựng panel; nếu chưa ai
    /// gán thì dùng ước lượng theo số ký tự để hàng token vẫn không tràn.</summary>
    public static Func<string, double>? LabelMeasure { get; set; }

    private static double Fallback(string title) => title.Length * 6.6;

    // Dựng lại ở mỗi lượt vẽ toolbar, nên số đo được GIỮ chứ không đo lại.
    private static readonly Dictionary<string, double> WidthCache = new();

    public static double TokenWidth(string title, Func<string, double>? measure = null)
    {
        var fn = measure ?? LabelMeasure ?? Fallback;
        // Chỉ cache phép đo mặc định: test tiêm hàm riêng, cache chung sẽ rò số đo giữa các ca test.
        if (measure == null && WidthCache.TryGetValue(title, out var cached)) return cached;
        double width = Math.Ceiling(fn(title)) + TokenChrome;
        if (measure == null) WidthCache[title] = width;
        return width;
    }

    public static double RowWidth(IReadOnlyList<string> titles, bool withCounter, Func<string, double>? measure = null)
    {
        var pieces = titles.Select(t => TokenWidth(t, measure)).ToList();
        if (withCounter) pieces.Add(CounterWidth);
        if (pieces.Count == 0) return 0;
        return pieces.Sum() + Spacing * (pieces.Count - 1);
    }

    /// <summary>
    /// Bao nhiêu token trong <paramref name="titles"/> giữ được nhãn. Phần còn lại thuộc về chip đếm.
    ///
    /// KHÔNG BAO GIỜ bằng 0 khi còn filter: một hàng chỉ có "+7" thì chẳng gọi tên thứ gì, nói cho
    /// người dùng ít hơn cả một token bị tràn.
    /// </summary>
    public static int VisibleCount(IReadOnlyList<string> titles, double budget, Func<string, double>? measure = null)
    {
        if (titles.Count == 0) return 0;
        double used = 0;
        for (int index = 0; index < titles.Count; index++)
        {
            double width = TokenWidth(titles[index], measure) + (index == 0 ? 0 : Spacing);
            // Lấy cái này mà vẫn còn bỏ lại cái khác thì chip đếm cũng phải vừa.
            bool leavesRemainder = index < titles.Count - 1;
            double allowance = budget - (leavesRemainder ? CounterWidth + Spacing : 0);
            if (used + width > allowance) return Math.Max(1, index);
            used += width;
        }
        return titles.Count;
    }
}
