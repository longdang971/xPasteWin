using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace xPasteWin.ViewModels;

/// <summary>
/// <see cref="ObservableCollection{T}"/> có thêm phép thay TOÀN BỘ nội dung bằng MỘT thông báo Reset.
///
/// Vì sao cần: đổi tab All ⇄ Pinned làm hầu hết item rời khỏi danh sách. Xoá từng cái một nghĩa là
/// mỗi item bắn một sự kiện CollectionChanged riêng, và ListView phải xử lý từng cú một — 450 lần cho
/// một lịch sử 500 item. Reset một phát để ListView dựng lại đúng nắm container đang thấy (~8 cái).
///
/// Vẫn giữ đường cập nhật từng-item cho thay đổi NHỎ (thêm một item vừa copy, dời item vừa dán lên
/// đầu): ở đó Reset sẽ bắt ListView dựng lại container của những thẻ vốn không đổi, gây nháy chữ.
/// </summary>
public sealed class CardCollection : ObservableCollection<CardViewModel>
{
    public void ReplaceAll(IReadOnlyList<CardViewModel> items)
    {
        Items.Clear();
        foreach (var it in items) Items.Add(it);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
