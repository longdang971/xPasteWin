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

    /// <summary>
    /// Đặt lại selection sau khi HÀNG THẺ đổi bên dưới nó (đổi tab, gõ search, bật filter, mở panel).
    ///
    /// Còn một thẻ đang chọn nằm trong danh sách mới → KHÔNG đụng vào: chọn lại sẽ giật highlight về
    /// đầu hàng chẳng vì lý do gì. Một multi-selection sống sót một phần cũng tính là còn sống — thu
    /// nó về một thẻ là lặng lẽ bỏ mất phần còn lại của lô người dùng đã gom.
    ///
    /// Ngược lại: thẻ đầu hàng, hoặc rỗng khi hàng không có kết quả nào. Port PanelSelection.rebased.
    /// </summary>
    public void Rebase(IReadOnlyList<Guid> ordered)
    {
        foreach (var id in ordered) if (_selected.Contains(id)) return;
        _selected.Clear();
        if (ordered.Count > 0) _selected.Add(ordered[0]);
    }

    /// <summary>
    /// Selection còn lại sau cú click vào vùng trống của panel.
    ///
    /// Cú click đó tồn tại để thu multi-selection về một thẻ, nên nó GIỮ thẻ đang chọn ở trên cùng —
    /// không phải thẻ đầu hàng, vốn sẽ dời một highlight người dùng đã đặt có chủ đích. Nó không bao
    /// giờ để lại con số không: hàng thẻ sẽ nằm im không highlight và ⏎ / Backspace vô tác dụng cho
    /// tới khi click vào một thẻ. Port PanelSelection.collapsed.
    /// </summary>
    public void Collapse(IReadOnlyList<Guid> ordered)
    {
        Guid? keep = null;
        foreach (var id in ordered) if (_selected.Contains(id)) { keep = id; break; }
        keep ??= ordered.Count > 0 ? ordered[0] : null;
        _selected.Clear();
        if (keep is { } g) _selected.Add(g);
    }

    /// <summary>
    /// Thẻ nào sẽ giữ selection sau khi <paramref name="deleting"/> biến mất khỏi danh sách.
    ///
    /// Thẻ còn sống đầu tiên TẠI hoặc SAU khối bị xoá — thẻ trượt vào chỗ trống — nếu không có thì
    /// thẻ còn sống cuối cùng trước đó, cho khối xoá chạy tới hết hàng. Trả null chỉ khi chẳng còn gì.
    ///
    /// Gọi TRƯỚC khi xoá, trên danh sách như nó đang có: sau khi xoá thì vị trí của các thẻ đã mất và
    /// không còn khoảng trống nào để lần ra. Port PanelSelection.survivor.
    /// </summary>
    public static Guid? Survivor(IReadOnlyList<Guid> ordered, ICollection<Guid> deleting)
    {
        int firstGap = -1;
        for (int i = 0; i < ordered.Count; i++)
            if (deleting.Contains(ordered[i])) { firstGap = i; break; }
        if (firstGap < 0) return null;

        for (int i = firstGap; i < ordered.Count; i++)
            if (!deleting.Contains(ordered[i])) return ordered[i];
        for (int i = firstGap - 1; i >= 0; i--)
            if (!deleting.Contains(ordered[i])) return ordered[i];
        return null;
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
