using System;
using System.Collections.Generic;
using System.Linq;
using xPasteWin.ViewModels;
using Xunit;

namespace xPasteWin.Tests;

/// <summary>
/// Quy tắc giữ selection: cái gì còn sáng sau khi xoá, sau khi hàng thẻ đổi bên dưới (đổi tab / gõ
/// search / bật filter), và sau cú click vào vùng trống. Port PanelSelectionTests.swift.
/// </summary>
public class PanelSelectionTests
{
    private readonly List<Guid> _items = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();

    private Guid? Survivor(params int[] indices) =>
        SelectionModel.Survivor(_items, indices.Select(i => _items[i]).ToHashSet());

    private SelectionModel WithSelected(params int[] indices)
    {
        var s = new SelectionModel();
        foreach (var i in indices) s.Toggle(_items[i]);
        return s;
    }

    // ---------- Xoá: ai thừa kế selection ----------

    [Fact]
    public void The_card_that_slides_into_the_gap_takes_the_selection() =>
        Assert.Equal(_items[3], Survivor(2));

    [Fact]
    public void Deleting_the_first_card_selects_the_new_first() =>
        Assert.Equal(_items[1], Survivor(0));

    [Fact]
    public void Deleting_the_last_card_falls_back_to_the_one_before_it() =>
        // Cuối hàng không có thẻ nào trượt vào chỗ trống, nên selection lùi lại thay vì bị bỏ.
        Assert.Equal(_items[3], Survivor(4));

    [Fact]
    public void A_deleted_block_skips_to_the_first_survivor_past_it() =>
        Assert.Equal(_items[4], Survivor(1, 2, 3));

    [Fact]
    public void A_block_running_to_the_end_falls_back_before_it() =>
        Assert.Equal(_items[1], Survivor(2, 3, 4));

    [Fact]
    public void A_scattered_selection_still_lands_on_a_survivor() =>
        // Thẻ Ctrl+click không cần liền nhau; index 1 là thẻ sống đầu tiên sau index 0.
        Assert.Equal(_items[1], Survivor(0, 2, 4));

    [Fact]
    public void Deleting_everything_leaves_nothing_selected() =>
        Assert.Null(Survivor(0, 1, 2, 3, 4));

    [Fact]
    public void Deleting_nothing_selects_nothing() =>
        Assert.Null(SelectionModel.Survivor(_items, new HashSet<Guid>()));

    [Fact]
    public void An_id_that_is_no_longer_on_screen_selects_nothing() =>
        // Thẻ đã bị lọc ra khỏi màn hình, hoặc đã mất từ trước.
        Assert.Null(SelectionModel.Survivor(_items, new HashSet<Guid> { Guid.NewGuid() }));

    [Fact]
    public void An_empty_list_selects_nothing() =>
        Assert.Null(SelectionModel.Survivor(Array.Empty<Guid>(), new HashSet<Guid> { _items[0] }));

    // ---------- Rebase khi hàng thẻ đổi bên dưới ----------

    [Fact]
    public void A_selection_still_on_screen_is_left_alone()
    {
        // Chọn lại sẽ giật highlight về đầu hàng — thẻ đang chọn còn trên màn hình thì đừng đụng.
        var s = WithSelected(3);
        s.Rebase(_items);
        Assert.Equal(new[] { _items[3] }, s.SelectedIds);
    }

    [Fact]
    public void A_selection_that_scrolled_out_of_the_results_moves_to_the_first()
    {
        var s = WithSelected(0);
        s.Rebase(new[] { _items[2], _items[4] });
        Assert.Equal(new[] { _items[2] }, s.SelectedIds);
    }

    [Fact]
    public void An_empty_selection_takes_the_first_card()
    {
        var s = new SelectionModel();
        s.Rebase(_items);
        Assert.Equal(new[] { _items[0] }, s.SelectedIds);
    }

    [Fact]
    public void A_partly_surviving_multi_selection_is_kept_whole()
    {
        // Hai thẻ Ctrl+click, một cái bị lọc mất: thu về một thẻ ở đây là lặng lẽ bỏ mất cái còn lại
        // của lô người dùng đã gom sẵn để dán.
        var s = WithSelected(1, 4);
        s.Rebase(new[] { _items[1] });
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void A_row_with_no_results_selects_nothing()
    {
        var s = WithSelected(0);
        s.Rebase(Array.Empty<Guid>());
        Assert.Empty(s.SelectedIds);
    }

    // ---------- Collapse khi click vào vùng trống ----------

    [Fact]
    public void A_multi_selection_collapses_onto_its_topmost_card()
    {
        var s = WithSelected(3, 1);
        s.Collapse(_items);
        Assert.Equal(new[] { _items[1] }, s.SelectedIds);   // thẻ gần đầu hàng nhất sống, không phải thẻ bất kỳ
    }

    [Fact]
    public void A_single_selection_is_left_where_it_is()
    {
        var s = WithSelected(3);
        s.Collapse(_items);
        Assert.Equal(new[] { _items[3] }, s.SelectedIds);   // không giật highlight về đầu hàng
    }

    [Fact]
    public void Collapsing_with_nothing_selected_takes_the_first_card()
    {
        var s = new SelectionModel();
        s.Collapse(_items);
        Assert.Equal(new[] { _items[0] }, s.SelectedIds);
    }

    [Fact]
    public void Collapsing_an_empty_row_selects_nothing()
    {
        var s = WithSelected(0);
        s.Collapse(Array.Empty<Guid>());
        Assert.Empty(s.SelectedIds);
    }
}
