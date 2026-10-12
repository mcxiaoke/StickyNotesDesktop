using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Models;
using StickyNotes.Services.Localization;
using StickyNotes.ViewModels;

namespace StickyNotes.Views;

/// <summary>
/// 独立彩色便签贴纸窗口后台逻辑
/// </summary>
public partial class NoteWindow : Window
{
    /// <summary>
    /// 窗口标题低频刷新间隔。
    /// 取3 秒是权衡结果：任务栏/Alt+Tab 的标题滞后 3 秒完全无感，
    /// 而刷新频率越高，触发 <c>WM_SETTEXT</c> 与任务栏按钮重绘的次数越多。
    /// </summary>
    private static readonly TimeSpan TitleRefreshInterval = TimeSpan.FromSeconds(3);

    private readonly DispatcherTimer _titleRefreshTimer;

    public NoteViewModel ViewModel => (NoteViewModel)DataContext;
    public TextBox Editor => EditorTextBox;
    public System.Windows.Controls.Primitives.Popup MoreMenu => MoreMenuPopup;
    public Button CloseButtonControl => CloseNoteButton;

    public NoteWindow(NoteViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // 标题低频刷新：多张便签在任务栏里靠各自的首行摘要区分。
        // 计时器在窗口关闭时必须停掉，否则已关闭窗口的 ViewModel 会被 Dispatcher 一直持有，
        // 形成内存泄漏（每个 DispatcherTimer 都持有对窗口的强引用）。
        _titleRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TitleRefreshInterval
        };
        _titleRefreshTimer.Tick += (_, _) => ViewModel.RefreshWindowTitle();
        _titleRefreshTimer.Start();

        Closed += (_, _) => _titleRefreshTimer.Stop();

        // 实时同步尺寸与坐标至实体，保证持久化绝对准确
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal && ActualWidth > 0 && ActualHeight > 0)
            {
                ViewModel.Note.WindowWidth = ActualWidth;
                ViewModel.Note.WindowHeight = ActualHeight;
            }
        };

        LocationChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
            {
                ViewModel.Note.WindowX = Left;
                ViewModel.Note.WindowY = Top;
            }
        };

        // 监听窗口失焦与关闭事件，立即触发无延迟强制刷盘保存
        Deactivated += async (_, _) =>
        {
            // 无待保存改动时短路，避免「在便签与列表间来回切换 = 每切一次一条全量 UPDATE」（原 F-P2-10）
            if (!ViewModel.HasPendingChanges) return;

            try
            {
                await ViewModel.FlushSaveAsync();
            }
            catch (Exception ex)
            {
                // FlushSaveAsync 内部已兜底，此处防御意外异常终止失焦续体（P2-5）
                AppLog.Error($"[NoteWindow] 失焦刷盘异常: {ex.Message}", ex);
            }

            // 失焦是标题刷新的最佳时机：此刻正文已落库，任务栏标题立即可读，
            // 不必等下一个 3 秒周期。
            ViewModel.RefreshWindowTitle();
        };

        // 关闭必须是同步阻塞刷盘：此前的 async void 写法中，await 之后的续体在
        // 「全部窗口 Closed → App.OnExit → 进程结束」这一真实时序下根本不会执行，
        // 导致最后 500ms 输入不落库、也不广播到主列表（原 F-P1-4）。
        // 复用 AutoSaveCoordinator 中已验证不会死锁的 ConfigureAwait(false).GetAwaiter().GetResult() 模式。
        Closing += (_, e) =>
        {
            // 应用退出流程中不做失败拦截（P2-6）：退出时序（F-P0-1）要求窗口必须能正常关闭，
            // 退出刷盘失败只留日志
            if (App.IsShuttingDown)
            {
                try { ViewModel.FlushSaveBlocking(); }
                catch (Exception ex) { AppLog.Error($"[NoteWindow] 退出刷盘失败: {ex.Message}", ex); }
                return;
            }

            try
            {
                ViewModel.FlushSaveBlocking();
            }
            catch (Exception ex)
            {
                AppLog.Error($"[NoteWindow] 关闭时同步刷盘失败: {ex.Message}", ex);
            }

            // 刷盘失败不再静默关窗（P2-6）：磁盘满/文件被占用时直接关窗，
            // 「保存失败」的唯一反馈载体（底部状态栏）随窗口一起销毁，最后一段编辑无声丢失。
            // 拦截关闭，由用户选择重试或放弃；已删除便签不在此列（IsDeleted 走取消路径）。
            if (!ViewModel.Note.IsDeleted && ViewModel.SaveState == NoteSaveState.Failed)
            {
                var choice = MessageBox.Show(
                    this,
                    Loc.T("Note.Error.SaveFailedBody",
                        "便签内容尚未成功保存（可能是磁盘已满或文件被占用）。\n\n" +
                        "「确定」：放弃本次更改并关闭窗口；\n" +
                        "「取消」：留在便签，稍后点击底部状态栏重试，或先复制正文再手动关闭。"),
                    Loc.T("Note.Error.SaveFailedTitle", "保存失败"),
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);

                if (choice == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                }
            }
        };
    }

    /// <summary>
    /// 顶部工具栏支持拖拽移动窗口
    /// </summary>
    private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>
    /// 精准跳行与高亮定位算法实现
    /// </summary>
    public void JumpToSearchHit(int targetCharIndex, int keywordLength)
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();

        // 调度至 Loaded 优先级，确保 TextBox 完成文本排版测量后滚动生效
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var text = EditorTextBox.Text;
            if (string.IsNullOrEmpty(text)) return;

            // 1. 安全防越界保护（防止搜索后内容变动导致索引越界）
            int safeStart = Math.Clamp(targetCharIndex, 0, text.Length);
            int safeLen = Math.Clamp(keywordLength, 0, text.Length - safeStart);

            // 2. 聚焦并高亮选中目标文本
            EditorTextBox.Focus();
            EditorTextBox.Select(safeStart, safeLen);

            // 3. 计算视觉行号与矩形坐标，智能视口居中
            try
            {
                var charRect = EditorTextBox.GetRectFromCharacterIndex(safeStart);
                double targetOffset = EditorTextBox.VerticalOffset + charRect.Top - (EditorTextBox.ActualHeight / 2.5);
                EditorTextBox.ScrollToVerticalOffset(Math.Max(0, targetOffset));
            }
            catch (Exception ex)
            {
                // 兜底调用系统按行滚动（记录原因便于排查跳转定位异常）
                AppLog.Warn($"[NoteWindow] 精确跳行定位失败，降级为按行滚动: {ex.Message}");
                int visualLine = EditorTextBox.GetLineIndexFromCharacterIndex(safeStart);
                EditorTextBox.ScrollToLine(visualLine);
            }
        });
    }

    private void NewNoteButton_Click(object sender, RoutedEventArgs e)
    {
        // 触发新建便签解耦消息
        WeakReferenceMessenger.Default.Send(new NewNoteRequestedMessage());
    }

    private void MoreMenuButton_Click(object sender, RoutedEventArgs e)
    {
        // 注意：MoreMenuPopup.StaysOpen="False"，鼠标点击按钮时 Popup 会先因失去外部点击而自动关闭，
        // 若此处使用 IsOpen = !IsOpen 取反，会在已关闭的基础上再次打开，导致菜单只能开不能关。
        // 因此这里只负责「打开」，关闭交给 Popup 自身的 StaysOpen 行为。
        MoreMenuPopup.IsOpen = true;
    }

    private void ShowNotesList_Click(object sender, RoutedEventArgs e)
    {
        MoreMenuPopup.IsOpen = false;
        // 调度至 Normal 优先级异步执行，确保 Popup 关闭完成并释放焦点后，再唤醒并置前主列表窗口
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            WeakReferenceMessenger.Default.Send(new ShowNotesListRequestedMessage());
        });
    }

    private async void ColorSelected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NoteColor color)
        {
            MoreMenuPopup.IsOpen = false;
            try
            {
                await ViewModel.SetColorAsync(color);
            }
            catch (Exception ex)
            {
                // 数据库/IO 异常不允许打到 async void 崩溃进程（P2-5）
                AppLog.Error($"[NoteWindow] 修改便签颜色失败: {ex.Message}", ex);
                MessageBox.Show(this, Loc.T("Note.Error.ColorFailed", "修改颜色失败：{0}", ex.Message),
                    Loc.T("Common.OperationFailed", "操作失败"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.N)
            {
                NewNoteButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.W)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.D)
            {
                DeleteButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.H)
            {
                ShowNotesList_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.P)
            {
                ViewModel.TogglePinCommand.Execute(null);
                e.Handled = true;
            }
        }

        // 纯键盘用户离开编辑区的通道（F-P1-11 剩余项）：编辑区 AcceptsTab="True" 会吞掉 Tab
        // 用于插入制表符，若无本通道，键盘用户进入正文后将无法用键盘到达任何工具按钮。
        // 仅在焦点确实位于正文编辑区时拦截，避免影响其它控件的 Esc/Tab 行为。
        if ((e.Key == Key.Escape ||
             (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control))
            && Keyboard.FocusedElement == EditorTextBox)
        {
            EditorTextBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        MoreMenuPopup.IsOpen = false;
        try
        {
            await ViewModel.DeleteAsync();
        }
        catch (Exception ex)
        {
            // 归档未落库时保留窗口并给出提示，避免用户误以为删除成功（P2-5）；
            // 原实现在此异常时其后的 Close() 不执行，窗口滞留却无任何反馈
            AppLog.Error($"[NoteWindow] 删除便签失败: {ex.Message}", ex);
            MessageBox.Show(this, Loc.T("Note.Error.DeleteFailed", "删除便签失败：{0}", ex.Message),
                Loc.T("Common.OperationFailed", "操作失败"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Close();
    }


    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void NoteWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            double step = e.Delta > 0 ? 1.0 : -1.0;
            ViewModel.ChangeFontSize(step);
        }
    }
}
