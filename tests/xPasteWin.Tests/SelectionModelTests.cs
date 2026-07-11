using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using xPasteWin.ViewModels;

namespace xPasteWin.Tests;

public class SelectionModelTests
{
    private static List<Guid> Ids(int n) => Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToList();

    [Fact]
    public void SelectSingle_replaces_previous()
    {
        var ids = Ids(3);
        var s = new SelectionModel();
        s.Toggle(ids[0]); s.Toggle(ids[1]);
        Assert.Equal(2, s.Count);
        s.SelectSingle(ids[2]);
        Assert.Equal(1, s.Count);
        Assert.True(s.IsSelected(ids[2]));
        Assert.False(s.IsSelected(ids[0]));
    }

    [Fact]
    public void Toggle_adds_then_removes()
    {
        var ids = Ids(2);
        var s = new SelectionModel();
        s.Toggle(ids[0]);
        Assert.True(s.IsSelected(ids[0]));
        s.Toggle(ids[0]);
        Assert.False(s.IsSelected(ids[0]));
    }

    [Fact]
    public void SelectAll_and_Clear()
    {
        var ids = Ids(4);
        var s = new SelectionModel();
        s.SelectAll(ids);
        Assert.Equal(4, s.Count);
        s.Clear();
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void Primary_is_first_selected_in_display_order()
    {
        var ids = Ids(4);
        var s = new SelectionModel();
        s.Toggle(ids[2]); s.Toggle(ids[1]);
        // thứ tự hiển thị = ids; item chính là ids[1] (xuất hiện trước ids[2])
        Assert.Equal(ids[1], s.Primary(ids));
    }

    [Fact]
    public void Primary_null_when_nothing_selected()
    {
        var ids = Ids(3);
        var s = new SelectionModel();
        Assert.Null(s.Primary(ids));
    }

    [Fact]
    public void MoveSelection_from_none_forward_picks_first()
    {
        var ids = Ids(3);
        var s = new SelectionModel();
        var t = s.MoveSelection(ids, +1);
        Assert.Equal(ids[0], t);
        Assert.True(s.IsSelected(ids[0]));
    }

    [Fact]
    public void MoveSelection_from_none_backward_picks_last()
    {
        var ids = Ids(3);
        var s = new SelectionModel();
        var t = s.MoveSelection(ids, -1);
        Assert.Equal(ids[2], t);
    }

    [Fact]
    public void MoveSelection_clamps_at_both_ends()
    {
        var ids = Ids(3);
        var s = new SelectionModel();
        s.SelectSingle(ids[0]);
        Assert.Equal(ids[0], s.MoveSelection(ids, -1)); // đã ở đầu, kẹp
        s.SelectSingle(ids[2]);
        Assert.Equal(ids[2], s.MoveSelection(ids, +1)); // đã ở cuối, kẹp
    }

    [Fact]
    public void MoveSelection_steps_one_and_replaces_selection()
    {
        var ids = Ids(4);
        var s = new SelectionModel();
        s.SelectSingle(ids[1]);
        var t = s.MoveSelection(ids, +1);
        Assert.Equal(ids[2], t);
        Assert.Equal(1, s.Count); // thay thế, không cộng dồn
    }

    [Fact]
    public void MoveSelection_empty_list_returns_null()
    {
        var s = new SelectionModel();
        Assert.Null(s.MoveSelection(new List<Guid>(), +1));
    }

    [Fact]
    public void Retain_drops_ids_no_longer_present()
    {
        var ids = Ids(3);
        var s = new SelectionModel();
        s.SelectAll(ids);
        s.Retain(new HashSet<Guid> { ids[0], ids[2] });
        Assert.Equal(2, s.Count);
        Assert.False(s.IsSelected(ids[1]));
    }
}
