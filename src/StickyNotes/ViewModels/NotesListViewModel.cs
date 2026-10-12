using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Models;
using StickyNotes.Services;
using StickyNotes.Services.Localization;

namespace StickyNotes.ViewModels;

/// <summary>
/// 便签管理中心主窗口 ViewModel
/// </summary>
public partial class NotesListViewModel : ObservableObject, IDisposable
{
    private readonly INoteRepository _repository;
    private readonly ISearchService _searchService;
    private readonly WindowManager _windowManager;
    private CancellationTokenSource? _searchCts;
    private IReadOnlyList<Note> _searchSnapshot = Array.Empty<Note>();

    [ObservableProperty]
    private ObservableCollection<Note> _notes = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredNotes))]
    private int _selectedFilterIndex = 0; // 0: 全部, 1: 已置顶

    public IEnumerable<Note> FilteredNotes =>
        SelectedFilterIndex == 1
            ? Notes.Where(n => n.IsPinnedInList)
            : Notes;

    public int PinnedNotesCount => Notes.Count(n => n.IsPinnedInList);

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SearchHit> _searchResults = new();

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private int _searchHitCount;

    [ObservableProperty]
    private int _searchNoteCount;

    /// <summary>
    /// 搜索状态栏文案：卡片数可能大于便签数（多命中扁平展开），
    /// 两者相等时维持原「N 条匹配便签」口径，否则明确「来自 M 张便签」
    /// </summary>
    public string SearchStatusText => SearchHitCount == 0
        ? string.Empty
        : SearchHitCount == SearchNoteCount
            ? Loc.T("Search.StatusSimple", "找到 {0} 条匹配便签 (回车或点击直达)", SearchHitCount)
            : Loc.T("Search.StatusWithNotes",
                "找到 {0} 条结果 (来自 {1} 张便签，回车或点击直达)", SearchHitCount, SearchNoteCount);

    partial void OnSearchHitCountChanged(int value) => OnPropertyChanged(nameof(SearchStatusText));

    partial void OnSearchNoteCountChanged(int value) => OnPropertyChanged(nameof(SearchStatusText));

    /// <summary>语言切换后重发所有派生自语言包的属性通知。</summary>
    private void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(SearchStatusText));

        // Note.PreviewText / DisplayTitle 是按需计算的只读属性，需逐条显式通知
        foreach (var note in _repository is null ? Array.Empty<Note>() : AllNotesSnapshot())
        {
            note.NotifyLocalizedTextChanged();
        }

        // SearchHit 是不可变 record，其 BadgeText 依赖当前语言；ListBox 不会因集合未变而重算，
        // 故强制清空后重新加入，让容器重建并重新读取绑定。
        if (SearchResults.Count > 0)
        {
            var snapshot = SearchResults.ToArray();
            SearchResults.Clear();
            foreach (var hit in snapshot)
            {
                SearchResults.Add(hit);
            }
        }

        OnPropertyChanged(nameof(FilteredNotes));
    }

    private IEnumerable<Note> AllNotesSnapshot() => FilteredNotes as Note[] ?? FilteredNotes.ToArray();

    [ObservableProperty]
    private bool _hasNoNotes;

    public NotesListViewModel(
        INoteRepository repository,
        ISearchService searchService,
        WindowManager windowManager)
    {
        _repository = repository;
        _searchService = searchService;
        _windowManager = windowManager;

        // 注册消息总线监听
        WeakReferenceMessenger.Default.Register<NoteContentChangedMessage>(this, (_, msg) => HandleNoteContentChanged(msg.Value));
        WeakReferenceMessenger.Default.Register<NoteMetaChangedMessage>(this, (_, msg) => HandleNoteMetaChanged(msg.Value));
        WeakReferenceMessenger.Default.Register<NoteUpdatedMessage>(this, (_, msg) => HandleNoteMetaChanged(msg.Value));
        WeakReferenceMessenger.Default.Register<NoteDeletedMessage>(this, (_, msg) => HandleNoteDeleted(msg.Value));
        WeakReferenceMessenger.Default.Register<NoteCreatedMessage>(this, (_, msg) => HandleNoteCreated(msg.Value));
        WeakReferenceMessenger.Default.Register<NotesReloadedRequestedMessage>(this, (_, _) =>
        {
            var app = System.Windows.Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.InvokeAsync(async () => await LoadNotesAsync());
            }
            else
            {
                _ = LoadNotesAsync();
            }
        });
        WeakReferenceMessenger.Default.Register<NoteArchivedMessage>(this, (_, msg) => HandleNoteDeleted(msg.Value));
        WeakReferenceMessenger.Default.Register<NoteRestoredMessage>(this, (_, msg) => HandleNoteRestored(msg.Value));

        // 界面语言切换：搜索状态文案与便签卡片预览（Note.PreviewText / SearchHit.BadgeText）
        // 均为普通 CLR 属性，不会随 XAML 绑定自动刷新，必须显式重发通知。
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (_, _) =>
        {
            var app = System.Windows.Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(RefreshLocalizedText);
            }
            else
            {
                RefreshLocalizedText();
            }
        });
        WeakReferenceMessenger.Default.Register<NewNoteRequestedMessage>(this, (_, _) =>
        {
            var app = System.Windows.Application.Current;
            if (app != null)
            {
                if (app.Dispatcher.CheckAccess())
                {
                    _ = NewNoteAsync();
                }
                else
                {
                    app.Dispatcher.InvokeAsync(async () => await NewNoteAsync());
                }
            }
            else
            {
                _ = NewNoteAsync();
            }
        });
        WeakReferenceMessenger.Default.Register<ShowNotesListRequestedMessage>(this, (_, _) =>
        {
            var app = System.Windows.Application.Current;
            if (app != null)
            {
                if (app.Dispatcher.CheckAccess())
                {
                    _windowManager.OpenOrActivateNotesListWindow();
                }
                else
                {
                    app.Dispatcher.InvokeAsync(() => _windowManager.OpenOrActivateNotesListWindow());
                }
            }
        });
    }

    /// <summary>
    /// 更新线程安全的搜索只读快照
    /// </summary>
    private void UpdateSearchSnapshot()
    {
        _searchSnapshot = Notes.ToArray();
    }

    /// <summary>
    /// 加载所有未删除的活动便签
    /// </summary>
    public async Task LoadNotesAsync()
    {
        var list = await _repository.GetAllActiveAsync();
        Notes.Clear();
        foreach (var note in list)
        {
            Notes.Add(note);
        }
        HasNoNotes = Notes.Count == 0;
        RefreshFilterNotification();
        UpdateSearchSnapshot();
    }

    /// <summary>
    /// 安全取消并清理当前正在进行的搜索任务（杜绝对已释放 CTS 重复 Cancel 导致的 ObjectDisposedException）
    /// </summary>
    private void CancelCurrentSearch()
    {
        var cts = Interlocked.Exchange(ref _searchCts, null);
        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                AppLog.Error($"[NotesListViewModel] 取消搜索任务异常: {ex.Message}", ex);
            }
            finally
            {
                try
                {
                    cts.Dispose();
                }
                catch (ObjectDisposedException) { }
            }
        }
    }

    partial void OnSearchTextChanged(string value) => QueueSearch(value);

    /// <summary>
    /// 搜索触发的唯一入口：空词清空结果并退出搜索态，非空词取消在途搜索后经 80ms 防抖后台执行。
    /// 搜索框输入（属性变更）与数据变更回调（内容/元数据/删除/恢复）统一走这里，
    /// 后者不再手动重入 <c>OnSearchTextChanged</c> 属性回调，消除「属性回调身兼两职」的耦合（原 F-P2-19）。
    /// </summary>
    private void QueueSearch(string? value)
    {
        CancelCurrentSearch();

        if (string.IsNullOrWhiteSpace(value))
        {
            IsSearching = false;
            SearchResults.Clear();
            SearchHitCount = 0;
            SearchNoteCount = 0;
            return;
        }

        IsSearching = true;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;

        // 80ms 极速防抖即输即显，使用不可变快照消除后台线程与 UI 绑定的并发枚举竞态
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(80, token);
                if (token.IsCancellationRequested) return;

                var snapshot = _searchSnapshot;
                var hits = _searchService.Search(snapshot, value);

                if (token.IsCancellationRequested) return;

                void UpdateUi()
                {
                    if (token.IsCancellationRequested) return;

                    // 结果序列与当前完全一致时跳过重建：多命中卡片下用户常在结果间导航，
                    // 后台内容/元数据变更引发的重复触发不应冲掉选中项与滚动位置
                    if (IsSameResultSequence(SearchResults, hits))
                    {
                        return;
                    }

                    SearchResults.Clear();
                    foreach (var hit in hits)
                    {
                        SearchResults.Add(hit);
                    }
                    SearchHitCount = hits.Count;
                    SearchNoteCount = hits.Select(h => h.NoteId).Distinct().Count();
                }

                var app = System.Windows.Application.Current;
                if (app != null && app.Dispatcher != null && !app.Dispatcher.HasShutdownStarted)
                {
                    if (app.Dispatcher.CheckAccess())
                    {
                        UpdateUi();
                    }
                    else
                    {
                        // InvokeAsync 而非同步 Invoke（原 F-P2-20）：不阻塞后台线程等 UI，
                        // 退出竞态下的异常经 Task 观察并记录，不静默丢失也不阻塞搜索任务
                        _ = app.Dispatcher.InvokeAsync(UpdateUi).Task.ContinueWith(
                            t =>
                            {
                                if (t.IsFaulted && t.Exception != null)
                                {
                                    var inner = t.Exception.GetBaseException();
                                    AppLog.Error($"[NotesListViewModel] 搜索结果更新失败: {inner.Message}", inner);
                                }
                            },
                            TaskContinuationOptions.OnlyOnFaulted);
                    }
                }
                else
                {
                    UpdateUi();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Error($"[NotesListViewModel] 搜索执行异常: {ex.Message}", ex);
            }
        }, token);
    }

    /// <summary>
    /// 判定新命中序列与当前列表是否完全一致（NoteId + 行号 + 选区 + 分级逐项比对）。
    /// 一致即视为「数据虽有变化但未影响搜索结果」，跳过 UI 重建以保住选中与滚动。
    /// </summary>
    private static bool IsSameResultSequence(
        System.Collections.ObjectModel.ObservableCollection<SearchHit> current,
        IReadOnlyList<SearchHit> next)
    {
        if (current.Count != next.Count) return false;

        for (int i = 0; i < next.Count; i++)
        {
            var a = current[i];
            var b = next[i];
            if (a.NoteId != b.NoteId
                || a.LineNumber != b.LineNumber
                || a.CharIndex != b.CharIndex
                || a.Length != b.Length
                || a.Tier != b.Tier)
            {
                return false;
            }
        }

        return true;
    }

    [RelayCommand]
    public async Task NewNoteAsync()
    {
        var newNote = new Note
        {
            Id = Guid.NewGuid(),
            Content = string.Empty,
            Color = NoteColor.Yellow,
            IsPinnedInList = false,
            AlwaysOnTop = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var (left, top) = _windowManager.CalculateSmartRightPlacement(newNote.WindowWidth, newNote.WindowHeight);
        newNote.WindowX = left;
        newNote.WindowY = top;

        await _repository.SaveAsync(newNote);
        Notes.Insert(0, newNote);
        HasNoNotes = false;
        RefreshFilterNotification();
        UpdateSearchSnapshot();

        AppLog.Info($"[NotesListViewModel] 已新建便签 {newNote.Id}，位置=({left:F0},{top:F0})");

        _windowManager.OpenOrActivateNote(newNote, window =>
        {
            window.Focus();
            window.Editor.Focus();
        });
    }

    [RelayCommand]
    public void OpenNote(Note note)
    {
        _windowManager.OpenOrActivateNote(note);
    }

    [RelayCommand]
    public async Task TogglePinNoteAsync(Note note)
    {
        note.IsPinnedInList = !note.IsPinnedInList;
        note.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveAsync(note);
        WeakReferenceMessenger.Default.Send(new NoteMetaChangedMessage(note));
    }

    [RelayCommand]
    public async Task DeleteNoteAsync(Note note)
    {
        note.IsDeleted = true;
        note.IsOpen = false;
        note.UpdatedAt = DateTime.UtcNow;
        _windowManager.CloseNoteWindow(note.Id);
        await _repository.ArchiveNoteAsync(note.Id);
        Notes.Remove(note);
        HasNoNotes = Notes.Count == 0;
        RefreshFilterNotification();
        UpdateSearchSnapshot();

        AppLog.Info($"[NotesListViewModel] 已归档便签 {note.Id}");

        WeakReferenceMessenger.Default.Send(new NoteArchivedMessage(note.Id));

        if (IsSearching)
        {
            QueueSearch(SearchText);
        }
    }

    [RelayCommand]
    public void OpenArchive()
    {
        _windowManager.OpenOrActivateArchivedNotesWindow();
    }

    [RelayCommand]
    public void OpenSettings()
    {
        _windowManager.OpenOrActivateSettingsWindow();
    }

    [RelayCommand]
    public void JumpToSearchHit(SearchHit hit)
    {
        _windowManager.NavigateToHit(hit);
    }

    private void RefreshFilterNotification()
    {
        OnPropertyChanged(nameof(FilteredNotes));
        OnPropertyChanged(nameof(PinnedNotesCount));
    }

    /// <summary>
    /// 处理正文内容轻量变更（仅更新卡片内部文本属性和搜索快照，不重新排列列表次序）
    /// </summary>
    private void HandleNoteContentChanged((Guid NoteId, string Content, DateTime UpdatedAt) payload)
    {
        Action action = () =>
        {
            var note = Notes.FirstOrDefault(n => n.Id == payload.NoteId);
            if (note != null)
            {
                note.Content = payload.Content;
                note.UpdatedAt = payload.UpdatedAt;
            }
            UpdateSearchSnapshot();
            if (IsSearching)
            {
                QueueSearch(SearchText);
            }
        };

        var app = System.Windows.Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    /// <summary>
    /// 处理元数据变更（置顶、颜色等，必要时仅使用 Move 微调位置，不重建视觉树）
    /// </summary>
    private void HandleNoteMetaChanged(Note updated)
    {
        Action action = () =>
        {
            var idx = Notes.ToList().FindIndex(n => n.Id == updated.Id);
            if (idx >= 0)
            {
                var cur = Notes[idx];
                cur.Color = updated.Color;
                cur.IsPinnedInList = updated.IsPinnedInList;
                cur.AlwaysOnTop = updated.AlwaysOnTop;
                cur.UpdatedAt = updated.UpdatedAt;

                // 重新排定次序：仅当位置发生变动时使用 Move，杜绝 RemoveAt+Insert 导致的视觉闪烁
                int targetPos = 0;
                while (targetPos < Notes.Count)
                {
                    var target = Notes[targetPos];
                    if (target.Id == cur.Id) { targetPos++; continue; }
                    if (cur.IsPinnedInList && !target.IsPinnedInList) break;
                    if (cur.IsPinnedInList == target.IsPinnedInList && cur.UpdatedAt >= target.UpdatedAt) break;
                    targetPos++;
                }
                if (targetPos > idx) targetPos--;
                if (targetPos != idx && targetPos >= 0 && targetPos < Notes.Count)
                {
                    Notes.Move(idx, targetPos);
                }
            }

            RefreshFilterNotification();
            UpdateSearchSnapshot();

            if (IsSearching)
            {
                QueueSearch(SearchText);
            }
        };

        var app = System.Windows.Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    private void HandleNoteDeleted(Guid id)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _windowManager.CloseNoteWindow(id);
            var note = Notes.FirstOrDefault(n => n.Id == id);
            if (note != null)
            {
                Notes.Remove(note);
            }
            HasNoNotes = Notes.Count == 0;
            RefreshFilterNotification();
            UpdateSearchSnapshot();
            if (IsSearching)
            {
                QueueSearch(SearchText);
            }
        });
    }

    private void HandleNoteCreated(Note note)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (note.Id != Guid.Empty && !Notes.Any(n => n.Id == note.Id))
            {
                Notes.Insert(0, note);
                HasNoNotes = false;
                RefreshFilterNotification();
                UpdateSearchSnapshot();
            }
            else
            {
                _ = LoadNotesAsync();
            }
        });
    }

    private void HandleNoteRestored(Note note)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!Notes.Any(n => n.Id == note.Id))
            {
                Notes.Insert(0, note);
                HasNoNotes = false;
                RefreshFilterNotification();
                UpdateSearchSnapshot();
                if (IsSearching)
                {
                    QueueSearch(SearchText);
                }
            }
        });
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        CancelCurrentSearch();
    }
}
