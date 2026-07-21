using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using xPasteWin.Models;
using xPasteWin.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace xPasteWin.ViewModels;

public sealed partial class PanelViewModel : ObservableObject
{
    private readonly ClipboardStore _store;
    private readonly PasteService _paste;
    private readonly ClipboardMonitor _monitor;
    private readonly ISettings _settings;

    /// <summary>Danh sách card đang hiển thị (đã lọc theo tab + search).</summary>
    public ObservableCollection<CardViewModel> Cards { get; } = new();

    public SelectionModel Selection { get; } = new();

    [ObservableProperty] private ClipboardTab activeTab = ClipboardTab.All;
    [ObservableProperty] private bool isSearchOpen;
    [ObservableProperty] private Guid? previewItemId;

    /// <summary>App lắng nghe để: HidePanel → PasteService.PasteAsync → store.MoveToTop.</summary>
    public event Action? PasteFinished;

    public ClipboardItem? PendingReorder { get; private set; }

    /// <summary>Tên app đích để dán (cho nhãn "Paste to &lt;App&gt;" trong context menu).</summary>
    public string? TargetAppName => _paste.TargetAppName;

    public PanelViewModel(ClipboardStore store, PasteService paste, ClipboardMonitor monitor, ISettings settings)
    {
        _store = store; _paste = paste; _monitor = monitor; _settings = settings;
    }

    public string SearchQuery
    {
        get => _store.SearchQuery;
        set
        {
            if (_store.SearchQuery == value) return;
            _store.SearchQuery = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    partial void OnActiveTabChanged(ClipboardTab value) => Refresh();

    /// <summary>Đồng bộ danh sách card từ store, giữ nguyên selection còn hợp lệ.</summary>
    public void Refresh()
    {
        SyncCards(_store.Displayed(ActiveTab).ToList());
        Selection.Retain(Cards.Select(c => c.Id).ToHashSet());
        ApplySelectionVisual();
    }

    /// <summary>
    /// Cập nhật <see cref="Cards"/> TẠI CHỖ để khớp danh sách mong muốn (khớp theo Id): giữ nguyên
    /// CardViewModel — và do đó container ListView — cho item không đổi, chỉ thêm/bớt/đổi chỗ phần khác.
    ///
    /// Vì sao KHÔNG Clear()+Add() lại: mỗi lần mở panel App gọi Refresh() trước ShowPanel(); nếu dựng
    /// lại toàn bộ card thì ListView phải tạo lại MỌI container → chữ trong card render lại 2 pha
    /// (ClearType "đậm" → grayscale "mảnh", do ScaleTransform của ContentHost) → nháy đậm/mảnh mỗi lần
    /// mở. Giữ container ổn định khi danh sách không đổi (trường hợp phổ biến: mở panel mà chưa copy gì
    /// mới) → hết nháy, đồng thời đỡ tốn CPU dựng lại thẻ.
    /// </summary>
    // Cache CardViewModel theo Id để TÁI DÙNG khi đổi tab / refresh. Các getter của card rất nặng ở
    // lần đầu (nạp icon app, decode thumbnail, SourceAppService.GetVisual, FileIconService…) rồi được
    // cache NGAY TRONG instance. Nếu mỗi lần đổi tab lại `new CardViewModel` thì toàn bộ việc nặng đó
    // chạy lại → item hiện ra chậm, cảm giác lag. Giữ instance sống theo vòng đời item trong store.
    private readonly Dictionary<Guid, CardViewModel> _cardCache = new();

    private CardViewModel GetOrCreateCard(ClipboardItem item)
    {
        if (_cardCache.TryGetValue(item.Id, out var vm)) return vm;
        vm = new CardViewModel(item, _store);
        _cardCache[item.Id] = vm;
        return vm;
    }

    private void SyncCards(IReadOnlyList<ClipboardItem> desired)
    {
        // 0) Dọn cache cho item đã bị XOÁ khỏi store (tránh phình bộ nhớ). KHÔNG dọn theo `desired`:
        //    item bị lọc khỏi tab hiện tại (vd đang ở tab Pin) vẫn còn trong store và cần giữ cache
        //    để lần đổi tab quay lại hiện ra tức thì.
        if (_cardCache.Count > 0)
        {
            var live = _store.Items.Select(i => i.Id).ToHashSet();
            foreach (var id in _cardCache.Keys.Where(k => !live.Contains(k)).ToList())
                _cardCache.Remove(id);
        }

        // 1) Bỏ card không còn trong danh sách mong muốn.
        var desiredIds = desired.Select(i => i.Id).ToHashSet();
        for (int i = Cards.Count - 1; i >= 0; i--)
            if (!desiredIds.Contains(Cards[i].Id)) Cards.RemoveAt(i);

        // 2) Duyệt theo đúng thứ tự mong muốn: khớp thì giữ, có sẵn ở chỗ khác thì Move, chưa có thì Insert
        //    (tái dùng instance từ cache thay vì dựng mới → không chạy lại việc nặng khi đổi tab).
        for (int i = 0; i < desired.Count; i++)
        {
            var id = desired[i].Id;
            if (i < Cards.Count && Cards[i].Id == id) continue;

            int existing = -1;
            for (int j = i + 1; j < Cards.Count; j++)
                if (Cards[j].Id == id) { existing = j; break; }

            if (existing >= 0) Cards.Move(existing, i);
            else Cards.Insert(i, GetOrCreateCard(desired[i]));
        }
    }

    private List<Guid> OrderedIds() => Cards.Select(c => c.Id).ToList();

    private void ApplySelectionVisual()
    {
        foreach (var c in Cards) c.IsSelected = Selection.IsSelected(c.Id);
    }

    public CardViewModel? Card(Guid id) => Cards.FirstOrDefault(c => c.Id == id);

    public CardViewModel? PrimaryCard()
    {
        var id = Selection.Primary(OrderedIds());
        return id is { } g ? Card(g) : null;
    }

    // --- Selection ---
    public void SelectSingle(Guid id) { Selection.SelectSingle(id); ApplySelectionVisual(); }
    public void ToggleSelect(Guid id) { Selection.Toggle(id); ApplySelectionVisual(); }
    public void SelectAll() { Selection.SelectAll(OrderedIds()); ApplySelectionVisual(); }
    public void ClearSelection() { Selection.Clear(); ApplySelectionVisual(); }

    /// <summary>Di chuyển selection bằng phím mũi tên. Trả id để View cuộn tới.</summary>
    public Guid? MoveSelection(int delta)
    {
        var id = Selection.MoveSelection(OrderedIds(), delta);
        ApplySelectionVisual();
        return id;
    }

    // --- Thao tác item ---
    public bool NeedsDeleteConfirm => Selection.Count > 1;

    public void DeleteSelected()
    {
        var ids = Selection.SelectedIds.ToList();
        if (ids.Count == 0) return;
        _store.DeleteItems(ids);
        Selection.Clear();
        Refresh();
    }

    public void Delete(ClipboardItem item)
    {
        _store.Delete(item);
        Refresh();
    }

    /// <summary>Xoá toàn bộ lịch sử chưa ghim (nút … → Clear History).</summary>
    public void ClearHistory()
    {
        _store.ClearUnpinned();
        Selection.Clear();
        Refresh();
    }

    public void TogglePin(ClipboardItem item)
    {
        _store.TogglePin(item);
        Refresh();
    }

    public void Copy(ClipboardItem item)
    {
        WriteToClipboard(item, plain: false);
        _monitor.MarkNextChangeAsOwn();
        _store.MoveToTop(item);
        Refresh();
        var card = Card(item.Id);
        if (card != null) { card.IsCopied = true; PreviewItemId = null; }
    }

    public static bool CanOpenUrl(ClipboardItem item) =>
        item.Type == ClipboardContentType.Url &&
        Uri.TryCreate(item.Text, UriKind.Absolute, out _);

    public void OpenUrl(ClipboardItem item)
    {
        if (Uri.TryCreate(item.Text, UriKind.Absolute, out var uri))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = uri.AbsoluteUri, UseShellExecute = true });
            }
            catch { }
        }
    }

    public void TogglePreview(Guid id) =>
        PreviewItemId = PreviewItemId == id ? null : id;

    // --- Paste ---
    public void Paste(ClipboardItem item) => PasteInternal(item, plain: false);
    public void PastePlain(ClipboardItem item) => PasteInternal(item, plain: true);

    private void PasteInternal(ClipboardItem item, bool plain)
    {
        // Setting "Always paste as Plain Text": ép plain khi item có text.
        if (!plain && item.Text != null && _settings.Get("alwaysPastePlainText", false)) plain = true;
        WriteToClipboard(item, plain);
        _monitor.MarkNextChangeAsOwn();
        PendingReorder = item;
        PasteFinished?.Invoke();
    }

    public void ClearPendingReorder() => PendingReorder = null;

    private void WriteToClipboard(ClipboardItem item, bool plain)
    {
        var dp = new DataPackage();
        switch (item.Type)
        {
            case ClipboardContentType.Text:
            case ClipboardContentType.Url:
                if (item.Text != null) dp.SetText(item.Text);
                // Giữ định dạng khi dán: RTF (app office) hoặc HTML (web). Capture chỉ lưu MỘT trong hai.
                if (!plain && item.RichData is { Length: > 0 })
                {
                    var rich = System.Text.Encoding.UTF8.GetString(item.RichData);
                    if (item.RichType == "rtf") dp.SetRtf(rich);
                    else if (item.RichType == "html")
                        dp.SetHtmlFormat(Interop.ClipboardFormats.BuildCfHtml(rich));
                }
                break;

            case ClipboardContentType.Image:
                var path = _store.ImagePath(item.Id);
                if (File.Exists(path))
                {
                    var f = StorageFile.GetFileFromPathAsync(path).AsTask().Result;
                    dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(f));
                }
                break;

            case ClipboardContentType.File:
            case ClipboardContentType.Folder:
                if (item.FilePaths is { Length: > 0 })
                {
                    var items = new List<IStorageItem>();
                    foreach (var p in item.FilePaths)
                    {
                        try
                        {
                            if (Directory.Exists(p))
                                items.Add(StorageFolder.GetFolderFromPathAsync(p).AsTask().Result);
                            else if (File.Exists(p))
                                items.Add(StorageFile.GetFileFromPathAsync(p).AsTask().Result);
                        }
                        catch { }
                    }
                    if (items.Count > 0) dp.SetStorageItems(items);
                    dp.SetText(string.Join("\n", item.FilePaths));
                }
                break;
        }
        Clipboard.SetContent(dp);
        // Đẩy nội dung ra OS clipboard NGAY (không giữ dạng delayed-render của tiến trình mình),
        // để app đích (Chrome, Word…) đọc được khi nhận Ctrl+V. Thiếu bước này, nhiều app dán hụt.
        try { Clipboard.Flush(); } catch { }
    }
}
