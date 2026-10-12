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
/// 便签正文的持久化状态（用于底部状态栏展示真实保存结果，替代此前的硬编码「已同步」）
/// </summary>
public enum NoteSaveState
{
    /// <summary>正文已成功写入数据库</summary>
    Saved,

    /// <summary>存在尚未落盘的改动（防抖计时中或正在写入）</summary>
    Pending,

    /// <summary>写入失败，需要用户重试</summary>
    Failed
}

/// <summary>
/// 独立彩色便签贴纸的 ViewModel
/// </summary>
public partial class NoteViewModel : ObservableObject
{
    private readonly INoteRepository _repository;
    private readonly AutoSaveCoordinator _autoSaveCoordinator;
    private readonly SettingsService _settingsService;

    [ObservableProperty]
    private Note _note = new();

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private NoteColor _color = NoteColor.Yellow;

    [ObservableProperty]
    private bool _alwaysOnTop;

    /// <summary>
    /// 兼容旧属性绑定
    /// </summary>
    public bool IsPinned
    {
        get => AlwaysOnTop;
        set => AlwaysOnTop = value;
    }

    [ObservableProperty]
    private double _fontSize = 14.0;

    private string _windowTitle = string.Empty;
    /// <summary>
    /// 便签窗口标题（供 <c>NoteWindow.Title</c> 绑定，任务栏/Alt+Tab 据此区分多张便签）。
    /// </summary>
    /// <remarks>
    /// 生产契约（有意为之，勿改）：<b>不做实时绑定</b>。正文是
    /// <c>UpdateSourceTrigger=PropertyChanged</c>，若直接绑定 <c>Note.DisplayTitle</c>，
    /// 每敲一个字都会触发一次 <c>WM_SETTEXT</c> 与任务栏按钮重绘，长文本输入会明显卡顿。
    /// 因此标题只由 <see cref="RefreshWindowTitle"/> 按需刷新：窗口打开时立即计算一次，
    /// 之后由 <c>NoteWindow</c> 的低频计时器驱动，且内部做了「有变化才通知」短路，
    /// 未变动时零 PropertyChanged 事件。
    /// </remarks>
    public string WindowTitle
    {
        get => _windowTitle;
        private set => SetProperty(ref _windowTitle, value);
    }

    private NoteSaveState _saveState = NoteSaveState.Saved;
    /// <summary>
    /// 当前便签的真实持久化状态
    /// </summary>
    public NoteSaveState SaveState
    {
        get => _saveState;
        private set
        {
            if (SetProperty(ref _saveState, value))
            {
                OnPropertyChanged(nameof(SaveStatusText));
            }
        }
    }

    private DateTime? _lastSavedAt;
    /// <summary>
    /// 最近一次成功写入数据库的时刻（本地时间）
    /// </summary>
    public DateTime? LastSavedAt
    {
        get => _lastSavedAt;
        private set
        {
            if (SetProperty(ref _lastSavedAt, value))
            {
                OnPropertyChanged(nameof(SaveStatusText));
            }
        }
    }

    /// <summary>
    /// 底部状态栏展示文案：与真实保存状态严格对应，不再是无条件写死的「已同步」
    /// </summary>
    public string SaveStatusText => SaveState switch
    {
        NoteSaveState.Pending => Loc.T("Note.SaveState.Saving", "保存中…"),
        NoteSaveState.Failed => Loc.T("Note.SaveState.Failed", "保存失败，点击重试"),
        _ => LastSavedAt.HasValue
            ? Loc.T("Note.SaveState.Synced", "已同步 {0}", LastSavedAt.Value.ToString("HH:mm:ss"))
            : Loc.T("Note.SaveState.SyncedNoTime", "已同步")
    };

    public NoteViewModel(
        INoteRepository repository,
        AutoSaveCoordinator autoSaveCoordinator,
        SettingsService settingsService)
    {
        _repository = repository;
        _autoSaveCoordinator = autoSaveCoordinator;
        _settingsService = settingsService;

        _fontSize = _settingsService.EditorFontSize;

        // 监听字号全局调整
        WeakReferenceMessenger.Default.Register<FontSizeChangedMessage>(this, (_, msg) =>
        {
            FontSize = msg.Value;
        });

        // 底部保存状态文案随界面语言变化，需显式重发通知
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (_, _) =>
            OnPropertyChanged(nameof(SaveStatusText)));
    }

    /// <summary>
    /// 交互式调整便签正文字号（Ctrl+滚轮，限制在 10 ~ 36 pt）。
    /// <para>
    /// <b>产品契约（有意为之，勿改）</b>：字号是<b>全局</b>设置 —— 设置页提供同样的
    /// 字号下拉项并承诺“即时生效”，任意一张便签上缩放会同步影响全部便签与设置页。
    /// 每一步缩放会立即落盘一次（含顺带触发的备份/清理），这是该设计的已知代价；
    /// 若改为“按便签记忆 + 防抖落盘”，将改变用户在设置页看到的全局语义。
    /// </para>
    /// </summary>
    public void ChangeFontSize(double delta)
    {
        double newSize = Math.Clamp(FontSize + delta, 10.0, 36.0);
        if (Math.Abs(FontSize - newSize) > 0.1)
        {
            _settingsService.SetEditorFontSize(newSize);
        }
    }

    public void Initialize(Note note)
    {
        Note = note;

        // 初始化期间的回填不是用户编辑，必须抑制「保存中/防抖保存/脏时间戳」等副作用，
        // 否则刚打开的便签会被误判为有未落盘改动。
        _isInitializing = true;
        try
        {
            Content = note.Content;
        }
        finally
        {
            _isInitializing = false;
        }

        Color = note.Color;
        AlwaysOnTop = note.AlwaysOnTop;

        // 初始即为「已同步」：打开时的内存内容与数据库一致
        SaveState = NoteSaveState.Saved;
        LastSavedAt = null;

        // 窗口打开时立即算一次标题，之后交给低频计时器增量刷新
        RefreshWindowTitle();
    }

    /// <summary>
    /// 重新计算窗口标题，仅在结果真正变化时才发出 <c>PropertyChanged</c>。
    /// 由 <c>NoteWindow</c> 的低频计时器调用，避免正文每次按键都重绘任务栏按钮。
    /// </summary>
    public void RefreshWindowTitle()
    {
        var newTitle = Note.DisplayTitle;
        if (string.Equals(newTitle, _windowTitle, StringComparison.Ordinal))
        {
            return;
        }

        WindowTitle = newTitle;
    }

    private bool _isInitializing;

    partial void OnContentChanged(string value)
    {
        if (_isInitializing) return;

        Note.Content = value;
        Note.UpdatedAt = DateTime.UtcNow;

        // 有未落盘改动，状态立即转为「保存中…」
        SaveState = NoteSaveState.Pending;

        // 触发 500ms 防抖静默保存
        _autoSaveCoordinator.ScheduleSave(Note, async n =>
        {
            try
            {
                await _repository.SaveAsync(n);
                LastSavedAt = DateTime.Now;
                SaveState = NoteSaveState.Saved;
            }
            catch (Exception ex)
            {
                SaveState = NoteSaveState.Failed;
                AppLog.Error($"[NoteViewModel] 便签 {n.Id} 自动保存失败: {ex.Message}", ex);
                throw;
            }

            // 发送正文变更轻量消息（不触发列表重排抖动）
            WeakReferenceMessenger.Default.Send(new NoteContentChangedMessage(n.Id, n.Content, n.UpdatedAt));
        });
    }

    /// <summary>
    /// 保存失败后的手动重试入口（供底部状态栏「保存失败，点击重试」调用）
    /// </summary>
    [RelayCommand]
    public async Task RetrySaveAsync()
    {
        if (SaveState != NoteSaveState.Failed) return;
        await FlushSaveAsync();
    }

    [RelayCommand]
    public async Task TogglePinAsync()
    {
        AlwaysOnTop = !AlwaysOnTop;
        Note.AlwaysOnTop = AlwaysOnTop;
        Note.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveAsync(Note);
        WeakReferenceMessenger.Default.Send(new NoteMetaChangedMessage(Note));
    }

    [RelayCommand]
    public async Task SetColorAsync(NoteColor newColor)
    {
        Color = newColor;
        Note.Color = newColor;
        Note.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveAsync(Note);
        WeakReferenceMessenger.Default.Send(new NoteMetaChangedMessage(Note));
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        // 1. 内存实体标记为已删除与已关闭
        Note.IsDeleted = true;
        Note.IsOpen = false;
        Note.UpdatedAt = DateTime.UtcNow;

        // 2. 取消防抖待写入队列中的旧数据，防止异步覆写
        _autoSaveCoordinator.CancelPendingSave(Note.Id);

        // 3. 执行数据库归档持久化与广播
        await _repository.ArchiveNoteAsync(Note.Id);
        WeakReferenceMessenger.Default.Send(new NoteArchivedMessage(Note.Id));
    }

    /// <summary>
    /// 同步阻塞式刷盘（专供窗口 Closing 使用）。
    /// WPF 的窗口关闭是同步流程，async void 事件处理器中 await 之后的续体在
    /// 「全部窗口 Closed → App.OnExit → 进程结束」的时序下不会执行，因此此处必须同步等待落库完成。
    /// 复用 AutoSaveCoordinator 中已验证不会死锁的 ConfigureAwait(false).GetAwaiter().GetResult() 模式。
    /// </summary>
    public void FlushSaveBlocking()
    {
        if (Note.IsDeleted)
        {
            // 便签已归档/删除：必须先摘除防抖队列中残留的待保存调度再返回。
            // 若直接返回，迟到的防抖任务会在行被彻底删除后经 Upsert 重新 INSERT（幽灵数据），
            // 或在归档窗口恢复后用陈旧状态把刚恢复的便签再次抹掉（P1-2）
            _autoSaveCoordinator.CancelPendingSave(Note.Id);
            return;
        }

        try
        {
            var executed = _autoSaveCoordinator.FlushAsync(Note.Id).ConfigureAwait(false).GetAwaiter().GetResult();

            if (executed)
            {
                // 待保存调度已执行完整保存：状态、时间戳与列表广播均由 SaveAction 内部完成。
                // 原实现在此之后又无条件 SaveAsync(Note) 一次，造成关窗时的重复全量写（F-P2-11）。
                return;
            }

            if (SaveState == NoteSaveState.Saved)
            {
                // 无待保存且状态已是已同步：内容早已落库，零写入
                return;
            }

            // 兜底直写：覆盖「防抖任务在途已被摘除」与「上次保存失败后重试」两种情形，
            // 保住 F-P1-4「关窗必同步落库」的契约
            _repository.SaveAsync(Note).ConfigureAwait(false).GetAwaiter().GetResult();
            LastSavedAt = DateTime.Now;
            SaveState = NoteSaveState.Saved;

            // 与异步版本保持一致：广播持久化正文（禁止用 PreviewText，见下方说明）
            WeakReferenceMessenger.Default.Send(new NoteContentChangedMessage(Note.Id, Note.Content, Note.UpdatedAt));
        }
        catch (Exception ex)
        {
            SaveState = NoteSaveState.Failed;
            AppLog.Error($"[NoteViewModel] 便签 {Note.Id} 关闭时同步刷盘失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 是否存在尚未落盘的改动（供窗口失焦时短路，避免每次失焦都全量写库）
    /// </summary>
    public bool HasPendingChanges => SaveState != NoteSaveState.Saved;

    /// <summary>
    /// 立即强制刷盘当前便签
    /// </summary>
    public async Task FlushSaveAsync()
    {
        // 已归档/已删除便签禁止执行回写保存
        if (Note.IsDeleted) return;

        try
        {
            var executed = await _autoSaveCoordinator.FlushAsync(Note.Id);

            if (executed)
            {
                // 待保存调度已执行完整保存：状态、时间戳与列表广播均由 SaveAction 内部完成。
                // 原实现在此之后又无条件 SaveAsync(Note) 一次，造成每次失焦都双写一条
                // 全量 UPDATE（F-P2-11 的写放大），现只写一次。
                return;
            }

            if (SaveState == NoteSaveState.Saved)
            {
                // 无待保存且状态已是已同步：内容早已落库，零写入
                return;
            }

            // 兜底直写：覆盖「防抖任务在途已被摘除」与「上次保存失败后重试」两种情形
            await _repository.SaveAsync(Note);
            LastSavedAt = DateTime.Now;
            SaveState = NoteSaveState.Saved;
        }
        catch (Exception ex)
        {
            SaveState = NoteSaveState.Failed;
            AppLog.Error($"[NoteViewModel] 便签 {Note.Id} 强制刷盘失败: {ex.Message}", ex);
            return;
        }

        // 必须广播持久化正文 Note.Content，禁止使用展示用的派生属性 Note.PreviewText：
        // PreviewText 在空白便签时会返回「（空白便签）」占位文案，且会 Trim 掉正文首尾空格与缩进，
        // 一旦广播出去会污染主列表内存实体（NotesListViewModel.HandleNoteContentChanged），
        // 再经「切换置顶」等全字段 SaveAsync 覆盖写回数据库，造成正文被永久污染。
        WeakReferenceMessenger.Default.Send(new NoteContentChangedMessage(Note.Id, Note.Content, Note.UpdatedAt));
    }
}
