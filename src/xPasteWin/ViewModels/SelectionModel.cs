using System;
using System.Collections.Generic;
using System.Linq;

namespace xPasteWin.ViewModels;

/// <summary>
/// Quản lý tập item đang chọn của panel — logic thuần (không phụ thuộc WinUI), tái tạo hành vi
/// selection của macOS ContentView (selectedIDs + moveSelection + primarySelected).
/// </summary>
public sealed class SelectionModel
{
    private readonly HashSet<Guid> _selected = new();

    public IReadOnlyCollection<Guid> SelectedIds => _selected;
    public int Count => _selected.Count;
    public bool IsSelected(Guid id) => _selected.Contains(id);

    /// <summary>Chọn đơn — thay thế toàn bộ selection (click thường).</summary>
    public void SelectSingle(Guid id) { _selected.Clear(); _selected.Add(id); }

    /// <summary>Toggle một id (Ctrl+click).</summary>
    public void Toggle(Guid id) { if (!_selected.Add(id)) _selected.Remove(id); }

    /// <summary>Chọn tất cả (Ctrl+A).</summary>
    public void SelectAll(IEnumerable<Guid> ids)
    {
        _selected.Clear();
        foreach (var id in ids) _selected.Add(id);
    }

    /// <summary>Bỏ chọn hết (click nền).</summary>
    public void Clear() => _selected.Clear();

    /// <summary>Giữ lại chỉ những id còn tồn tại trong danh sách hiện tại (sau khi xoá/lọc).</summary>
    public void Retain(IReadOnlyCollection<Guid> existing)
    {
        _selected.RemoveWhere(id => !existing.Contains(id));
    }

    /// <summary>Item "chính" để dán/preview: id đầu tiên theo thứ tự hiển thị mà đang được chọn.</summary>
    public Guid? Primary(IReadOnlyList<Guid> ordered)
    {
        foreach (var id in ordered)
            if (_selected.Contains(id)) return id;
        return null;
    }

    /// <summary>
    /// Di chuyển selection bằng phím mũi tên. Kẹp ở hai đầu (không cuộn vòng).
    /// Chưa có selection: delta&gt;0 → item đầu, delta&lt;0 → item cuối. Trả id mới để cuộn tới.
    /// </summary>
    public Guid? MoveSelection(IReadOnlyList<Guid> ordered, int delta)
    {
        if (ordered.Count == 0) return null;
        var cur = Primary(ordered);
        int target;
        if (cur == null)
            target = delta > 0 ? 0 : ordered.Count - 1;
        else
        {
            int idx = 0;
            for (int i = 0; i < ordered.Count; i++)
                if (ordered[i] == cur.Value) { idx = i; break; }
            target = Math.Clamp(idx + delta, 0, ordered.Count - 1);
        }
        var tid = ordered[target];
        SelectSingle(tid);
        return tid;
    }
}
