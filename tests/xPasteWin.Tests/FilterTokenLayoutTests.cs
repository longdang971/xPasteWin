using System;
using System.Linq;
using xPasteWin.Services;
using Xunit;

namespace xPasteWin.Tests;

/// <summary>
/// Bao nhiêu token filter giữ được nhãn trước khi phần còn lại gom sau một chip đếm. Port
/// FilterTokenLayoutTests.swift.
///
/// Bảy filter đang bật lấp kín cả ô search, chừa cho chỗ gõ đúng một khe.
/// </summary>
public class FilterTokenLayoutTests
{
    /// <summary>Đo xác định thay cho engine chữ: 7px mỗi ký tự. Test đo TỶ LỆ của phép tính, không đo
    /// font — buộc kết quả phụ thuộc font hệ thống sẽ khiến test đổi màu theo máy chạy.</summary>
    private static readonly Func<string, double> Measure = s => s.Length * 7.0;

    /// <summary>Bảy cái trong báo cáo, theo đúng thứ tự chúng hiện ra.</summary>
    private static readonly string[] Crowded =
        { "Text", "Link", "Image", "File", "Folder", "Terminal", "Transmit" };

    [Fact]
    public void A_few_tokens_all_keep_their_labels()
    {
        Assert.Equal(2, FilterTokenLayout.VisibleCount(new[] { "Text", "Link" }, 400, Measure));
    }

    [Fact]
    public void A_crowded_row_collapses_some_of_them()
    {
        int visible = FilterTokenLayout.VisibleCount(Crowded, FilterTokenLayout.RowBudget, Measure);
        Assert.True(visible < Crowded.Length, "bảy token không được ở lại hết");
        Assert.True(visible > 0);
    }

    [Fact]
    public void What_stays_plus_the_counter_actually_fits()
    {
        // Toàn bộ ý nghĩa của phép tính: hàng nó chọn phải vừa ngân sách, KỂ CẢ chip đếm. Không chừa
        // chỗ cho chip đếm thì tràn đúng một chip.
        double budget = FilterTokenLayout.RowBudget;
        int visible = FilterTokenLayout.VisibleCount(Crowded, budget, Measure);
        double width = FilterTokenLayout.RowWidth(Crowded.Take(visible).ToList(),
                                                  visible < Crowded.Length, Measure);
        Assert.True(width <= budget, $"hàng rộng {width} so với ngân sách {budget}");
    }

    [Fact]
    public void A_row_that_fits_exactly_keeps_every_token()
    {
        var titles = new[] { "Text", "Link", "File" };
        double exact = FilterTokenLayout.RowWidth(titles, false, Measure);
        Assert.Equal(titles.Length, FilterTokenLayout.VisibleCount(titles, exact, Measure));
    }

    [Fact]
    public void One_token_survives_a_budget_too_small_even_for_it()
    {
        // Một hàng chỉ có "+1" chẳng gọi tên filter nào, tệ hơn cả việc để tràn.
        Assert.Equal(1, FilterTokenLayout.VisibleCount(new[] { "Một cái tên rất là dài" }, 10, Measure));
    }

    [Fact]
    public void Longer_labels_push_more_behind_the_counter()
    {
        int shortNames = FilterTokenLayout.VisibleCount(new[] { "A", "B", "C", "D", "E" }, 220, Measure);
        int longNames = FilterTokenLayout.VisibleCount(
            new[] { "Sequel Pro", "Transmit 5", "Visual Studio", "Adobe Photoshop", "IntelliJ" }, 220, Measure);
        Assert.True(shortNames > longNames, "ngân sách là BỀ RỘNG, không phải số token — tên app rất dài");
    }

    [Fact]
    public void No_filters_means_no_tokens()
    {
        Assert.Equal(0, FilterTokenLayout.VisibleCount(Array.Empty<string>(), 220, Measure));
    }

    [Fact]
    public void Measuring_the_same_title_twice_agrees()
    {
        // Bề rộng được nhớ qua các lượt vẽ toolbar; cache trả số khác ở lần thứ hai sẽ làm hàng token
        // nhấp nháy giữa hai bố cục.
        Assert.Equal(FilterTokenLayout.TokenWidth("Transmit"), FilterTokenLayout.TokenWidth("Transmit"));
    }

    [Fact]
    public void The_budget_leaves_the_text_field_room_to_type()
    {
        // Ô search rộng 460 và chia sẻ nó với hai tab compact cùng nút filter.
        Assert.True(FilterTokenLayout.RowBudget < 300,
                    "ngân sách lớn cỡ này sẽ tái hiện đúng cái chật chội đang được sửa");
    }

    [Fact]
    public void Fallback_measure_still_bounds_the_row()
    {
        // Chưa ai tiêm hàm đo (vd panel chưa dựng xong) thì ước lượng theo ký tự vẫn phải giữ hàng
        // token trong ngân sách, thay vì cho mọi token bề rộng 0 rồi vẽ tràn hết ra.
        int visible = FilterTokenLayout.VisibleCount(Crowded, FilterTokenLayout.RowBudget);
        double width = FilterTokenLayout.RowWidth(Crowded.Take(visible).ToList(), visible < Crowded.Length);
        Assert.True(width <= FilterTokenLayout.RowBudget);
    }
}
