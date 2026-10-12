using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Models;
using StickyNotes.Services;
using StickyNotes.Services.Localization;
using StickyNotes.ViewModels;
// 刻意只按类型别名引入 Wpf.Ui 的这两个类型，而不写 `using Wpf.Ui.Controls;`：
// 两个命名空间都定义了 MenuItem，整条 using 会让本文件里既有的 MenuItem 判定
//（卡片右键菜单等）变成二义引用而编译失败。
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace StickyNotes.Views;

/// <summary>
/// 便签管理中心主窗口交互逻辑
/// </summary>
public partial class NotesListWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SettingsService? _settingsService;
    private readonly PinService? _pinService;
    private readonly SessionLockCoordinator? _sessionLock;
    private readonly SyncHost? _syncHost;
    private bool _isManualSyncing;

    /// <summary>同步结果轻提示条的自动收起计时器（仅成功/信息态启用，见 <see cref="ShowInfoBar"/>）</summary>
    private readonly DispatcherTimer _infoBarCloseTimer = new();

    public NotesListViewModel ViewModel => (NotesListViewModel)DataContext;

    public NotesListWindow(
        NotesListViewModel viewModel,
        SettingsService? settingsService = null,
        PinService? pinService = null,
        SyncHost? syncHost = null,
        SessionLockCoordinator? sessionLock = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settingsService = settingsService;
        _pinService = pinService;
        _syncHost = syncHost;
        _sessionLock = sessionLock;
        PinOverlay.PinService = pinService;

        // 会话锁是唯一的锁定真相源：窗口只做「同步显示」，不再自行判断该不该锁。
        // 两个方向都要订阅 —— 缺 Unlocked 会出现「在归档窗口输了 PIN，主列表窗口遮罩却还在」的不一致。
        // 本窗口解锁成功后回调闲置计时归零，避免解锁瞬间就再次超时。
        if (_sessionLock != null)
        {
            _sessionLock.LockRequested += (_, _) => SyncLockState();
            _sessionLock.Unlocked += (_, _) => SyncLockState();
            PinOverlay.Unlocked += (_, _) => _sessionLock.Unlock();
        }

        Loaded += async (_, _) =>
        {
            RestoreWindowPlacement();
            SyncLockState();
            try
            {
                await ViewModel.LoadNotesAsync();
            }
            catch (Exception ex)
            {
                // 数据库/IO 异常不允许在 async void 中崩溃进程（P2-5）
                AppLog.Error($"[NotesListWindow] 加载便签列表失败: {ex.Message}", ex);
            }
        };

        // 窗口重新可见时（托盘唤醒、双击 exe 唤醒）只同步会话锁状态。
        // 旧实现在此无条件上锁，导致「X 关到托盘 → 再打开必输 PIN」且与设定的分钟数无关。
        IsVisibleChanged += (s, e) =>
        {
            if (e.NewValue is true)
            {
                SyncLockState();
            }
            else
            {
                ViewModel.SearchText = string.Empty;
            }
        };

        // 同步结果提示条自动收起：到点收起，等待期间用户可随时手动关掉
        _infoBarCloseTimer.Interval = InfoBarAutoCloseDelay;
        _infoBarCloseTimer.Tick += (_, _) =>
        {
            _infoBarCloseTimer.Stop();
            if (SyncInfoBar != null)
            {
                SyncInfoBar.IsOpen = false;
            }
        };

        Activated += (_, _) =>
        {
            if (PinOverlay.IsLocked) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (PinOverlay.IsLocked) return;
                RestoreOptimalFocus();
            });
        };

        Closing += (s, e) =>
        {
            SaveWindowPlacement();
            ViewModel.SearchText = string.Empty;
            if (!App.IsShuttingDown && (_settingsService?.MinimizeToTrayOnClose ?? true))
            {
                e.Cancel = true;
                Hide();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    NativeMethods.TrimWorkingSet();
                });
            }
        };

        StateChanged += (s, e) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    NativeMethods.TrimWorkingSet();
                });
            }
        };

        WeakReferenceMessenger.Default.Register<ShowNotesListRequestedMessage>(this, (_, _) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
                Show();
                Activate();
                Focus();

                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(hwnd);
                }
            });
        });

        PreviewKeyDown += NotesListWindow_PreviewKeyDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_ACTIVATE_INSTANCE && NativeMethods.WM_ACTIVATE_INSTANCE != 0)
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Show();
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void FilterAllRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is NotesListViewModel vm)
        {
            vm.SelectedFilterIndex = 0;
        }
    }

    private void FilterPinnedRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is NotesListViewModel vm)
        {
            vm.SelectedFilterIndex = 1;
        }
    }

    private void NoteCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 排除内部按钮触发的冒泡
        if (e.OriginalSource is DependencyObject dep)
        {
            var btn = FindVisualAncestor<System.Windows.Controls.Button>(dep);
            if (btn != null) return;
        }

        if (sender is FrameworkElement { DataContext: Note note })
        {
            NotesListBox.SelectedItem = note;
        }

        // 双击打开新窗口
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: Note note2 })
        {
            ViewModel.OpenNoteCommand.Execute(note2);
            e.Handled = true;
        }
    }


    private void CardMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement btn)
        {
            var card = FindVisualAncestor<Border>(btn);
            if (card?.ContextMenu != null)
            {
                card.ContextMenu.PlacementTarget = btn;
                card.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                card.ContextMenu.IsOpen = true;
                e.Handled = true;
            }
        }
    }

    private void ContextOpenNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Note note })
        {
            ViewModel.OpenNoteCommand.Execute(note);
        }
    }

    private async void ContextTogglePin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Note note })
        {
            try
            {
                await ViewModel.TogglePinNoteCommand.ExecuteAsync(note);
            }
            catch (Exception ex)
            {
                AppLog.Error($"[NotesListWindow] 置顶便签 {note.Id} 失败: {ex.Message}", ex);
                ShowInfoBar(Loc.T("List.Error.PinFailed", "置顶失败：{0}", ex.Message), InfoBarSeverity.Error);
            }
        }
    }

    private void OpenArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenArchiveCommand.Execute(null);
    }

    /// <summary>
    /// 手动「立即同步」（协议设计 §6 触发时机 5）：走<see cref="SyncHost.SyncNowAsync"/>统一入口，
    /// 单飞由引擎兜底；同步期间按钮置忙，完成后刷新列表（下行应用由 SyncHost 广播重载消息）。
    /// </summary>
    /// <remarks>
    /// 结果一律走<see cref="ShowInfoBar"/>非打断提示，不再弹 MessageBox：
    /// 同步是可随时重复触发的后台动作，弹窗打断输入且必须逐个点掉，属设计缺陷。
    /// 前置校验失败（未初始化/未启用/未配置）同样只提示不弹窗，理由同上。
    /// </remarks>
    private async void SyncNowButton_Click(object sender, RoutedEventArgs e)
    {
        // 重入保护：本窗口内同步未结束前不重复发起（引擎单飞仅拦引擎侧，这里先给出可见反馈）
        if (_isManualSyncing) return;

        if (_syncHost == null)
        {
            ShowInfoBar(Loc.T("Sync.ErrorNotInitialized", "同步功能尚未初始化"), InfoBarSeverity.Warning);
            return;
        }

        var sync = _settingsService?.Settings.Sync;
        if (sync == null || !sync.Enabled)
        {
            ShowInfoBar(Loc.T("Sync.ErrorNotEnabled", "请先在「设置 → 网络同步」中启用同步"),
                InfoBarSeverity.Informational);
            return;
        }

        if (!sync.IsConfigured)
        {
            ShowInfoBar(Loc.T("Sync.ErrorServerIncomplete",
                    "同步服务器信息尚未填写完整，请先在「设置 → 网络同步」中配置并保存"),
                InfoBarSeverity.Informational);
            return;
        }

        _isManualSyncing = true;
        SetSyncNowBusy(true);
        try
        {
            var summary = await _syncHost.SyncNowAsync("manual");
            if (summary == null)
            {
                // null 有三种来源：引擎单飞放弃、后端构建失败、或本轮已被异常吞掉并记入 LastError
                var error = _syncHost.CurrentState.LastError;
                ShowInfoBar(
                    string.IsNullOrEmpty(error)
                        ? Loc.T("Sync.ResultSkipped", "本次同步未执行（上一轮同步尚未结束）")
                        : Loc.T("Sync.ErrorFailed", "同步失败：{0}", error),
                    InfoBarSeverity.Warning);
                return;
            }

            // 上传逐条容错（P2-4）：部分失败也返回摘要，明确告知失败条数而非谎报全部成功
            var uploadFailed = summary.UploadFailedIds.Count;
            ShowInfoBar(
                uploadFailed > 0
                    ? Loc.T("Sync.ResultDoneWithUploadFailure",
                        "同步完成 · 下行 {0} · 上传 {1} · 上传失败 {2}（下轮自动重试）",
                        summary.Downloaded, summary.Uploaded, uploadFailed)
                    : Loc.T("Sync.ResultDone",
                        "同步完成 · 下行 {0} · 上传 {1} · 守卫跳过 {2}",
                        summary.Downloaded, summary.Uploaded, summary.GuardedSkipped),
                uploadFailed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[NotesListWindow] 手动同步异常: {ex.Message}", ex);
            ShowInfoBar(Loc.T("Sync.ErrorFailed", "同步失败：{0}", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _isManualSyncing = false;
            SetSyncNowBusy(false);
        }
    }

    /// <summary>
    /// 自动收起轻提示条的延时。仅成功态用短延时自动消失；
    /// 警告与错误保留在界面上直到用户手动关闭或被下一次提示覆盖，避免漏看失败原因。
    /// </summary>
    private static readonly TimeSpan InfoBarAutoCloseDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 统一展示非打断式轻提示条（成功/信息/警告/错误四态）。
    /// </summary>
    private void ShowInfoBar(string message, InfoBarSeverity severity)
    {
        if (SyncInfoBar == null) return;

        SyncInfoBar.Message = message;
        SyncInfoBar.Severity = severity;
        SyncInfoBar.IsOpen = true;

        // 重入：连点同步时，上一次的自动收起计时必须作废，否则会把新提示提前关掉
        _infoBarCloseTimer.Stop();
        if (severity == InfoBarSeverity.Success || severity == InfoBarSeverity.Informational)
        {
            _infoBarCloseTimer.Start();
        }
    }

    /// <summary>同步进行中把按钮置为不可用并把文案换成「同步中…」，避免用户误以为点击无效</summary>
    private void SetSyncNowBusy(bool busy)
    {
        SyncNowButton.IsEnabled = !busy;
        SyncNowButton.Content = busy
            ? Loc.T("Sync.Syncing", "同步中…")
            : Loc.T("Sync.Now", "立即同步");
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenSettingsCommand.Execute(null);
    }

    private async void ContextDeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Note note })
        {
            try
            {
                await ViewModel.DeleteNoteCommand.ExecuteAsync(note);
            }
            catch (Exception ex)
            {
                AppLog.Error($"[NotesListWindow] 归档便签 {note.Id} 失败: {ex.Message}", ex);
                ShowInfoBar(Loc.T("List.Error.ArchiveFailed", "归档失败：{0}", ex.Message), InfoBarSeverity.Error);
            }
        }
    }


    internal ListBox NotesListBoxControl => NotesListBox;
    internal ListBox SearchHitsListBoxControl => SearchHitsListBox;
    internal Wpf.Ui.Controls.TextBox SearchBoxControl => SearchBox;

    /// <summary>测试钩子：PIN 遮罩锁定状态（供锁定态快捷键拦截用例设置/读取）</summary>
    internal bool IsPinLockedForTest
    {
        get => PinOverlay.IsLocked;
        set => PinOverlay.IsLocked = value;
    }

    /// <summary>
    /// 测试钩子：模拟用户在本窗口输对 PIN。
    /// 必须走遮罩自身的 <c>Unlocked</c> 事件而非直接改 <c>IsLocked</c> ——
    /// 否则不会触发宿主对 <see cref="SessionLockCoordinator"/> 的解锁回调，测的就不是真实链路了。
    /// </summary>
    internal void UnlockForTest() => PinOverlay.UnlockForTest();

    /// <summary>测试钩子：以指定修饰键驱动窗口级快捷键处理逻辑（Ctrl 组合键无法在单测中伪造 Keyboard.Modifiers）</summary>
    internal void RaiseWindowPreviewKeyDown(KeyEventArgs e, ModifierKeys modifiers) => HandlePreviewKeyDown(e, modifiers);

    internal void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            if (ViewModel.IsSearching)
            {
                if (ViewModel.SearchResults.Count > 0)
                {
                    int targetIndex = (SearchHitsListBox.SelectedIndex >= 0 && SearchHitsListBox.SelectedIndex < ViewModel.SearchResults.Count)
                        ? SearchHitsListBox.SelectedIndex
                        : 0;
                    FocusListBoxItem(SearchHitsListBox, targetIndex);
                    e.Handled = true;
                }
            }
            else
            {
                if (ViewModel.FilteredNotes.Any())
                {
                    int targetIndex = (NotesListBox.SelectedIndex >= 0 && NotesListBox.SelectedIndex < NotesListBox.Items.Count)
                        ? NotesListBox.SelectedIndex
                        : 0;
                    FocusListBoxItem(NotesListBox, targetIndex);
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.Enter)
        {
            if (ViewModel.IsSearching && ViewModel.SearchResults.Count > 0)
            {
                var hit = (SearchHitsListBox.SelectedItem as SearchHit) ?? ViewModel.SearchResults[0];
                SearchHitsListBox.SelectedItem = hit;
                ViewModel.JumpToSearchHitCommand.Execute(hit);
                e.Handled = true;
            }
            else if (!ViewModel.IsSearching && ViewModel.FilteredNotes.Any())
            {
                var note = (NotesListBox.SelectedItem as Note) ?? ViewModel.FilteredNotes.FirstOrDefault();
                if (note != null)
                {
                    NotesListBox.SelectedItem = note;
                    ViewModel.OpenNoteCommand.Execute(note);
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (ViewModel.IsSearching)
            {
                ViewModel.SearchText = string.Empty;
                e.Handled = true;
            }
        }
    }

    // 兼容历史调用
    internal void SearchBox_KeyDown(object sender, KeyEventArgs e) => SearchBox_PreviewKeyDown(sender, e);

    /// <summary>
    /// 把会话锁状态同步到遮罩。窗口**只反映**状态，不自行判定该不该锁 ——
    /// 锁定时机统一由 <see cref="SessionLockCoordinator"/> 依据「闲置时长」与冷启动规则决定。
    /// 与 <see cref="PinService.IsPinEnabled"/> 取交集：用户在设置里清除 PIN 后立刻解除锁定，无需重启。
    /// </summary>
    private void SyncLockState()
    {
        if (_pinService is null) return;

        PinOverlay.IsLocked = _pinService.IsPinEnabled && (_sessionLock?.IsLocked ?? false);
        if (PinOverlay.IsLocked && _sessionLock != null)
        {
            PinOverlay.LockReason = _sessionLock.LockReason;
        }
    }

    /// <summary>
    /// 当窗口激活或从外部便签页返回时，智能恢复最佳键盘交互焦点
    /// </summary>
    public void RestoreOptimalFocus()
    {
        if (PinOverlay.IsLocked) return;

        // 如果用户已经在可编辑文本框内，不夺取输入焦点
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox ||
            Keyboard.FocusedElement is System.Windows.Controls.PasswordBox)
        {
            return;
        }

        if (ViewModel.IsSearching)
        {
            if (SearchHitsListBox.SelectedIndex >= 0 && SearchHitsListBox.SelectedIndex < ViewModel.SearchResults.Count)
            {
                FocusListBoxItem(SearchHitsListBox, SearchHitsListBox.SelectedIndex);
            }
            else if (ViewModel.SearchResults.Count > 0)
            {
                FocusListBoxItem(SearchHitsListBox, 0);
            }
            else
            {
                SearchBox.Focus();
                SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
            }
        }
        else
        {
            if (NotesListBox.SelectedIndex >= 0 && NotesListBox.SelectedIndex < NotesListBox.Items.Count)
            {
                FocusListBoxItem(NotesListBox, NotesListBox.SelectedIndex);
            }
            else if (NotesListBox.Items.Count > 0)
            {
                FocusListBoxItem(NotesListBox, 0);
            }
            else
            {
                SearchBox.Focus();
                SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
            }
        }
    }

    private void NotesListWindow_PreviewKeyDown(object sender, KeyEventArgs e) => HandlePreviewKeyDown(e, Keyboard.Modifiers);

    /// <summary>
    /// 窗口级按键分发。<b>PIN 锁定优先于一切快捷键</b>：锁定态下 Ctrl+N/Ctrl+F 等组合键若先被处理，
    /// 会绕过遮罩打开搜索框或直接新建一张不含遮罩的便签窗口，使锁定形同虚设。
    /// 修饰键由参数传入，以便单测在无法改写只读的 <see cref="Keyboard.Modifiers"/> 时仍能驱动 Ctrl 组合键路径。
    /// </summary>
    private void HandlePreviewKeyDown(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (PinOverlay.IsLocked) return;

        if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Key == Key.N)
            {
                ViewModel.NewNoteCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F)
            {
                SearchBox.Focus();
                SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
                e.Handled = true;
                return;
            }
        }

        // 若当前焦点在可编辑文本框（如搜索框、PIN密码框），由控件各自处理，不在此全局拦截
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox ||
            Keyboard.FocusedElement is System.Windows.Controls.PasswordBox)
        {
            return;
        }

        if (e.Key == Key.Down)
        {
            if (ViewModel.IsSearching)
            {
                if (SearchHitsListBox.Items.Count > 0)
                {
                    int next = SearchHitsListBox.SelectedIndex < 0
                        ? 0
                        : Math.Min(SearchHitsListBox.SelectedIndex + 1, SearchHitsListBox.Items.Count - 1);
                    FocusListBoxItem(SearchHitsListBox, next);
                    e.Handled = true;
                }
            }
            else
            {
                if (NotesListBox.Items.Count > 0)
                {
                    int next = NotesListBox.SelectedIndex < 0
                        ? 0
                        : Math.Min(NotesListBox.SelectedIndex + 1, NotesListBox.Items.Count - 1);
                    FocusListBoxItem(NotesListBox, next);
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.Up)
        {
            if (ViewModel.IsSearching)
            {
                if (SearchHitsListBox.SelectedIndex > 0)
                {
                    FocusListBoxItem(SearchHitsListBox, SearchHitsListBox.SelectedIndex - 1);
                    e.Handled = true;
                }
                else
                {
                    SearchBox.Focus();
                    SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
                    e.Handled = true;
                }
            }
            else
            {
                if (NotesListBox.SelectedIndex > 0)
                {
                    FocusListBoxItem(NotesListBox, NotesListBox.SelectedIndex - 1);
                    e.Handled = true;
                }
                else
                {
                    SearchBox.Focus();
                    SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.Enter)
        {
            if (ViewModel.IsSearching)
            {
                if (SearchHitsListBox.SelectedItem is SearchHit hit)
                {
                    ViewModel.JumpToSearchHitCommand.Execute(hit);
                    e.Handled = true;
                }
                else if (ViewModel.SearchResults.Count > 0)
                {
                    var hit0 = ViewModel.SearchResults[0];
                    SearchHitsListBox.SelectedItem = hit0;
                    ViewModel.JumpToSearchHitCommand.Execute(hit0);
                    e.Handled = true;
                }
            }
            else
            {
                if (NotesListBox.SelectedItem is Note note)
                {
                    ViewModel.OpenNoteCommand.Execute(note);
                    e.Handled = true;
                }
                else if (ViewModel.FilteredNotes.FirstOrDefault() is Note note0)
                {
                    NotesListBox.SelectedItem = note0;
                    ViewModel.OpenNoteCommand.Execute(note0);
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (ViewModel.IsSearching)
            {
                ViewModel.SearchText = string.Empty;
                SearchBox.Focus();
                e.Handled = true;
            }
        }
    }

    internal void FocusListBoxItem(ListBox listBox, int index)
    {
        if (index < 0 || listBox.Items.Count == 0) return;
        index = Math.Clamp(index, 0, listBox.Items.Count - 1);
        listBox.SelectedIndex = index;

        if (listBox.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
        {
            item.Focus();
            item.BringIntoView();
        }
        else
        {
            listBox.ScrollIntoView(listBox.Items[index]);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var container = listBox.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
                container?.Focus();
                container?.BringIntoView();
            });
        }
    }

    private void ListBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ListBox listBox && e.NewFocus == listBox)
        {
            int target = listBox.SelectedIndex >= 0 ? listBox.SelectedIndex : 0;
            if (listBox.Items.Count > target)
            {
                FocusListBoxItem(listBox, target);
                e.Handled = true;
            }
        }
    }

    internal void SearchHitsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up && SearchHitsListBox.SelectedIndex <= 0)
        {
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SearchHitsListBox.SelectedItem is SearchHit hit)
        {
            ViewModel.JumpToSearchHitCommand.Execute(hit);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel.SearchText = string.Empty;
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    internal void NotesListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up && NotesListBox.SelectedIndex <= 0)
        {
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && NotesListBox.SelectedItem is Note note)
        {
            ViewModel.OpenNoteCommand.Execute(note);
            e.Handled = true;
        }
    }

    /// <summary>搜索卡片按下位置：用于区分「点击」与「拖拽选择摘要文字」</summary>
    private Point _searchHitPressPoint;
    private bool _searchHitPressed;

    /// <summary>
    /// 记录按下位置。原实现在 MouseLeftButtonDown 直接跳转（按下即走），
    /// 导致用户无法在摘要里选中/复制文字，拖拽误触也会跳走（原 F-P3-15）。
    /// </summary>
    private void SearchHitCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _searchHitPressPoint = e.GetPosition(this);
        _searchHitPressed = true;
    }

    private void SearchHitCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_searchHitPressed)
        {
            return;
        }

        _searchHitPressed = false;

        // 移动超过阈值视为拖拽选择文字，不触发跳转
        var releasePoint = e.GetPosition(this);
        if (Math.Abs(releasePoint.X - _searchHitPressPoint.X) > 4 ||
            Math.Abs(releasePoint.Y - _searchHitPressPoint.Y) > 4)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: SearchHit hit })
        {
            SearchHitsListBox.SelectedItem = hit;
            ViewModel.JumpToSearchHitCommand.Execute(hit);
        }
    }

    /// <summary>
    /// 右键先选中该项：否则右键菜单的操作对象与视觉高亮不一致，易误归档错便签（原 F-P3-18）。
    /// </summary>
    private void NoteCard_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Note note })
        {
            NotesListBox.SelectedItem = note;
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private record WindowPlacementData(double Left, double Top, double Width, double Height);

    private void RestoreWindowPlacement()
    {
        try
        {
            // 位置策略：主窗口每次打开都固定在主屏工作区左侧 1/3 区带的水平居中位置，
            // 右侧 2/3 留给便签贴纸窗口；不再恢复上次的 Left/Top（用户会话内仍可自由拖动）。
            // 配置文件只用于恢复用户调整过的窗口尺寸。
            double width = Width;
            double height = Height;

            if (System.IO.File.Exists(StickyNotes.Infrastructure.AppPaths.WindowConfigPath))
            {
                var json = System.IO.File.ReadAllText(StickyNotes.Infrastructure.AppPaths.WindowConfigPath);
                var config = System.Text.Json.JsonSerializer.Deserialize<WindowPlacementData>(json);
                if (config != null && config.Width >= MinWidth && config.Height >= MinHeight)
                {
                    width = config.Width;
                    height = config.Height;
                }
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            ApplyLeftThirdPlacement(width, height);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[NotesListWindow] 恢复主窗口位置失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 将主窗口放到主屏工作区左侧 1/3 区带的水平居中、垂直居中位置，并钳制在工作区内
    /// </summary>
    private void ApplyLeftThirdPlacement(double width, double height)
    {
        var work = SystemParameters.WorkArea;
        double left = work.Left + Math.Max(0, work.Width / 3.0 - width / 2.0);
        double top = work.Top + Math.Max(10, (work.Height - height) / 2.0);

        if (left + width > work.Right)
            left = Math.Max(work.Left, work.Right - width - 10);
        if (top + height > work.Bottom)
            top = Math.Max(work.Top, work.Bottom - height - 10);

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    private void SaveWindowPlacement()
    {
        try
        {
            if (WindowState == WindowState.Normal)
            {
                var data = new WindowPlacementData(
                    Left,
                    Top,
                    ActualWidth > 0 ? ActualWidth : Width,
                    ActualHeight > 0 ? ActualHeight : Height
                );
                var json = System.Text.Json.JsonSerializer.Serialize(data);
                System.IO.File.WriteAllText(StickyNotes.Infrastructure.AppPaths.WindowConfigPath, json);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[NotesListWindow] 保存主窗口位置失败: {ex.Message}", ex);
        }
    }
}
