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
/// 已归档便签管理窗口 ViewModel
/// </summary>
public partial class ArchivedNotesViewModel : ObservableObject
{
    private readonly INoteRepository _repository;
    private readonly ISearchService _searchService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredNotes))]
    [NotifyPropertyChangedFor(nameof(ArchivedCount))]
    [NotifyPropertyChangedFor(nameof(HasNoNotes))]
    [NotifyPropertyChangedFor(nameof(CanClearArchived))]
    private ObservableCollection<Note> _archivedNotes = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredNotes))]
    private string _searchText = string.Empty;

    public IEnumerable<Note> FilteredNotes
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchText))
                return ArchivedNotes;

            // 复用 SearchService 的统一匹配规则（内容 + 标题），消除手写副本的语义漂移（原 F-P2-8）
            return ArchivedNotes.Where(n => _searchService.IsMatch(n, SearchText));
        }
    }

    public int ArchivedCount => ArchivedNotes.Count;

    /// <summary>
    /// 归档数量展示文案（形如「12 条」）。原先用 XAML StringFormat 硬编码中文，
    /// 无法本地化，故改为在 VM 侧组装。
    /// </summary>
    public string ArchivedCountText => Loc.T("Archive.Count", "{0} 条", ArchivedCount);

    public bool HasNoNotes => ArchivedNotes.Count == 0;

    /// <summary>
    /// 「清空归档」按钮是否可用。
    /// </summary>
    /// <remarks>
    /// 该值由 <see cref="ArchivedNotes"/> **集合内容**派生，而所有增删都是原地
    /// <c>Clear/Add/Remove</c>（集合实例从不被替换），因此
    /// <c>[NotifyPropertyChangedFor]</c> 挂在集合**属性 setter** 上的自动通知永远不会触发。
    /// 任何改动集合的路径都必须在最后显式调用 <see cref="NotifyDerivedCollectionsChanged"/>，
    /// 否则按钮会停留在窗口首次绑定时的取值（首次打开时集合为空 → 按钮永远是灰色）。
    /// </remarks>
    public bool CanClearArchived => ArchivedNotes.Count > 0;

    /// <summary>
    /// 集合变更后统一发出所有派生属性的变更通知（含 <see cref="CanClearArchived"/>）。
    /// 原地修改 <see cref="ArchivedNotes"/> 的每一处都必须调用本方法。
    /// </summary>
    private void NotifyDerivedCollectionsChanged()
    {
        OnPropertyChanged(nameof(FilteredNotes));
        OnPropertyChanged(nameof(ArchivedCount));
        OnPropertyChanged(nameof(ArchivedCountText));
        OnPropertyChanged(nameof(HasNoNotes));
        OnPropertyChanged(nameof(CanClearArchived));
    }

    public ArchivedNotesViewModel(INoteRepository repository, ISearchService searchService)
    {
        _repository = repository;
        _searchService = searchService;

        // 监听便签归档消息，若归档窗口开启中，自动拉取新归档
        WeakReferenceMessenger.Default.Register<NoteArchivedMessage>(this, async (_, _) =>
        {
            try
            {
                await LoadArchivedNotesAsync();
            }
            catch (Exception ex)
            {
                // MessageHandler 委托等价 async void：数据库/IO 异常不允许直接终止进程（P2-5）
                AppLog.Error($"[ArchivedNotesViewModel] 归档列表自动刷新失败: {ex.Message}", ex);
            }
        });

        // 语言切换：计数文案与卡片预览均派生自语言包，需显式重发通知
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (_, _) =>
        {
            foreach (var note in ArchivedNotes)
            {
                note.NotifyLocalizedTextChanged();
            }
            NotifyDerivedCollectionsChanged();
        });
    }

    /// <summary>
    /// 加载所有已归档的便签
    /// </summary>
    public async Task LoadArchivedNotesAsync()
    {
        var list = await _repository.GetAllArchivedAsync();
        ArchivedNotes.Clear();
        foreach (var note in list)
        {
            ArchivedNotes.Add(note);
        }

        NotifyDerivedCollectionsChanged();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredNotes));
    }

    /// <summary>
    /// 恢复便签回主管理列表
    /// </summary>
    [RelayCommand]
    public async Task RestoreNoteAsync(Note note)
    {
        await _repository.RestoreNoteAsync(note.Id);
        note.IsDeleted = false;
        ArchivedNotes.Remove(note);

        NotifyDerivedCollectionsChanged();

        // 广播恢复消息，主列表自动重新收纳
        WeakReferenceMessenger.Default.Send(new NoteRestoredMessage(note));
    }

    /// <summary>
    /// 彻底永久删除指定便签
    /// </summary>
    [RelayCommand]
    public async Task HardDeleteNoteAsync(Note note)
    {
        await _repository.HardDeleteAsync(note.Id);
        ArchivedNotes.Remove(note);

        NotifyDerivedCollectionsChanged();
    }

    /// <summary>
    /// 清空所有已归档便签
    /// </summary>
    [RelayCommand]
    public async Task ClearAllArchivedAsync()
    {
        await _repository.ClearAllArchivedAsync();
        ArchivedNotes.Clear();

        NotifyDerivedCollectionsChanged();
    }
}
