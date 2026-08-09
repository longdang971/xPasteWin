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
    public CardCollection Cards { get; } = new();

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
        // Bật/tắt filter ở popover là đổi danh sách hiển thị ngay, không cần ai gọi Refresh hộ.
        _store.Filters.Changed += Refresh;
    }

    /// <summary>Công tắc type/app/date của popover filter (dùng chung instance với store).</summary>
    public SearchFilters Filters => _store.Filters;

    /// <summary>
    /// Các app THỰC SỰ có trong lịch sử, đã phân giải tên + icon và sắp theo tên — chỉ dựng khi
    /// popover mở chứ không giữ trong store: nó đụng tới FileVersionInfo và trích icon, mà không ai
    /// cần tới cho đến khi người dùng thật sự muốn lọc.
    /// </summary>
    public IReadOnlyList<FilterApp> AppsInHistory()
    {
        return _store.Items
            .Select(i => i.SourceApp)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new FilterApp(p!, SourceAppService.DisplayName(p!),
                                       SourceAppService.GetVisual(p)?.IconPath))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Tên hiển thị của app theo đường dẫn exe — cho token filter và Backspace gỡ token.</summary>
    public static string AppName(string exePath) => SourceAppService.DisplayName(exePath);

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

    /// <summary>
    /// Đồng bộ danh sách card từ store, giữ nguyên selection còn hợp lệ.
    ///
    /// Đổi tab / gõ search / bật filter đều thay cả hàng thẻ bên dưới selection, nên sau khi lọc bỏ
    /// những id không còn, selection được đặt lại về thẻ đầu hàng nếu chẳng còn gì được chọn — panel
    /// luôn có đúng một thẻ sáng để ⏎ (dán) và Backspace (xoá) có chỗ mà tác động.
    /// KHÔNG đụng vào khi panel đang ẩn đi: đường đó cố ý xoá selection.
    /// </summary>
    public void Refresh()
    {
        SyncCards(_store.Displayed(ActiveTab).ToList());
        Selection.Retain(Cards.Select(c => c.Id).ToHashSet());
        if (!IsHidingPanel) Selection.Rebase(OrderedIds());
        ApplySelectionVisual();
        // Tô nền vàng đoạn khớp: cập nhật từ khoá free-text cho mọi card.
        var term = _store.HighlightTerm;
        foreach (var c in Cards) c.HighlightTerm = term;
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
        // Nút hover pin/xoá đi qua store + refresh danh sách (giống context menu).
        vm.OnTogglePin = () => { TogglePin(vm.Item); vm.RaisePinState(); };
        vm.OnDelete = () => Delete(vm.Item);
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

        // 1) Không đổi gì → về sớm, khỏi đụng vào collection (trường hợp phổ biến nhất: mở panel mà
        //    chưa copy gì mới, vì App đã đồng bộ ngay lúc store đổi).
        if (Cards.Count == desired.Count)
        {
            bool same = true;
            for (int i = 0; i < desired.Count; i++)
                if (Cards[i].Id != desired[i].Id) { same = false; break; }
            if (same) { for (int i = 0; i < Cards.Count; i++) Cards[i].Index = i; return; }
        }

        // 2) Thay đổi LỚN (đổi tab: gần như cả danh sách ra/vào) → thay một phát bằng Reset. Cập nhật
        //    từng item ở đây nghĩa là hàng trăm sự kiện CollectionChanged cho ListView xử lý lần lượt.
        //    Ngưỡng 32: dưới mức đó đường từng-item rẻ hơn và giữ nguyên container nên không nháy chữ.
        var desiredIds = desired.Select(i => i.Id).ToHashSet();
        var currentIds = Cards.Select(c => c.Id).ToHashSet();
        int churn = Cards.Count(c => !desiredIds.Contains(c.Id)) + desired.Count(i => !currentIds.Contains(i.Id));
        if (churn > 32)
        {
            Cards.ReplaceAll(desired.Select(GetOrCreateCard).ToList());
            for (int i = 0; i < Cards.Count; i++) Cards[i].Index = i;
            return;
        }

        // 3) Thay đổi nhỏ: bỏ card không còn trong danh sách mong muốn.
        for (int i = Cards.Count - 1; i >= 0; i--)
            if (!desiredIds.Contains(Cards[i].Id)) Cards.RemoveAt(i);

        // 4) Duyệt theo đúng thứ tự mong muốn: khớp thì giữ, có sẵn ở chỗ khác thì Move, chưa có thì Insert
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

        // Gán lại vị trí (cho badge Ctrl+1..9) — thứ tự có thể đổi sau move/insert.
        for (int i = 0; i < Cards.Count; i++) Cards[i].Index = i;
    }

    /// <summary>Card ở vị trí thứ <paramref name="index"/> (0-based) — cho dán nhanh Ctrl+1..9.</summary>
    public CardViewModel? CardAt(int index) =>
        index >= 0 && index < Cards.Count ? Cards[index] : null;

    /// <summary>Bật/tắt badge số trên toàn bộ card (khi giữ/nhả Ctrl).</summary>
    public void SetBadges(bool visible, bool plain)
    {
        foreach (var c in Cards) { c.ShowBadge = visible; c.BadgePlain = plain; }
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
    /// <summary>Đúng giữa lúc panel đang đóng: chặn việc đặt lại selection cho hàng thẻ vừa đổi, vì
    /// đường đóng panel cố ý dọn sạch selection.</summary>
    public bool IsHidingPanel { get; set; }

    public void SelectSingle(Guid id) { Selection.SelectSingle(id); ApplySelectionVisual(); }
    public void ToggleSelect(Guid id) { Selection.Toggle(id); ApplySelectionVisual(); }
    public void SelectAll() { Selection.SelectAll(OrderedIds()); ApplySelectionVisual(); }
    public void ClearSelection() { Selection.Clear(); ApplySelectionVisual(); }

    /// <summary>Click vào vùng trống của panel: thu multi-selection về một thẻ thay vì bỏ trắng — hàng
    /// thẻ không bao giờ nằm im không highlight. Port collapseSelection của macOS.</summary>
    public void CollapseSelection()
    {
        if (IsHidingPanel) return;
        Selection.Collapse(OrderedIds());
        ApplySelectionVisual();
    }

    /// <summary>Di chuyển selection bằng phím mũi tên. Trả id để View cuộn tới.</summary>
    public Guid? MoveSelection(int delta)
    {
        var id = Selection.MoveSelection(OrderedIds(), delta);
        ApplySelectionVisual();
        return id;
    }

    // --- Thao tác item ---
    public bool NeedsDeleteConfirm => Selection.Count > 1;

    /// <summary>
    /// Xoá, rồi để selection lại trên thứ còn sống sót.
    ///
    /// Trước đây Backspace dọn trắng selection, nên xoá một loạt thẻ là phải với chuột sau mỗi lần:
    /// cú nhấn thứ hai không còn gì để tác động. Người thừa kế được tính TRƯỚC khi xoá, trên hàng thẻ
    /// như nó đang có — sau khi xoá thì khoảng trống các thẻ để lại không còn để mà lần ra.
    /// </summary>
    public void DeleteSelected()
    {
        var ids = Selection.SelectedIds.ToHashSet();
        if (ids.Count == 0) return;
        var heir = SelectionModel.Survivor(OrderedIds(), ids);
        _store.DeleteItems(ids);
        Selection.Clear();
        if (heir is { } g) Selection.SelectSingle(g);
        Refresh();
    }

    public void Delete(ClipboardItem item)
    {
        var heir = SelectionModel.Survivor(OrderedIds(), new HashSet<Guid> { item.Id });
        _store.Delete(item);
        if (heir is { } g) Selection.SelectSingle(g);
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

    /// <summary>Đặt/xoá tên do người dùng đặt cho item (rename) rồi lưu + làm mới card.</summary>
    public void SetLabel(CardViewModel card, string? label)
    {
        card.Item.Label = label;
        _store.UpdateItem(card.Item);
        card.NotifyChanged();
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

    /// <summary>Dán NHIỀU item đang chọn, nối bằng dấu phân tách (New line/Space/Comma) — giống macOS.</summary>
    public void PasteSelectedJoined(bool plain)
    {
        var items = OrderedIds().Where(Selection.IsSelected)
                                .Select(id => Card(id)?.Item)
                                .Where(i => i != null).Cast<ClipboardItem>().ToList();
        if (items.Count == 0) return;
        if (items.Count == 1) { PasteInternal(items[0], plain); return; }

        var sep = _settings.Get("multiPasteSeparator", "newline") switch
        {
            "space" => " ",
            "comma" => ", ",
            _ => "\n",
        };
        var text = string.Join(sep, items.Select(i => i.Text ?? i.DisplayText));
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
        try { Clipboard.Flush(); } catch { }
        _monitor.MarkNextChangeAsOwn();
        PendingReorder = items[0];
        PasteFinished?.Invoke();
    }

    /// <summary>Dán bản văn bản đã BIẾN ĐỔI (Trim, JSON, URL encode…) — ghi text kết quả rồi dán.</summary>
    public void PasteTransformed(ClipboardItem item, TextTransform transform)
    {
        if (item.Text == null) return;
        string outText;
        try { outText = transform.Apply(item.Text); } catch { return; }
        var dp = new DataPackage();
        dp.SetText(outText);
        Clipboard.SetContent(dp);
        try { Clipboard.Flush(); } catch { }
        _monitor.MarkNextChangeAsOwn();
        PendingReorder = item;
        PasteFinished?.Invoke();
    }

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
