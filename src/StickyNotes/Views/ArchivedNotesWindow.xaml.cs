using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StickyNotes.Infrastructure;
using StickyNotes.Models;
using StickyNotes.Services;
using StickyNotes.ViewModels;

namespace StickyNotes.Views;

public partial class ArchivedNotesWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly PinService? _pinService;
    private readonly SessionLockCoordinator? _sessionLock;

    public ArchivedNotesViewModel ViewModel { get; }

    public ArchivedNotesWindow(
        ArchivedNotesViewModel viewModel,
        PinService? pinService = null,
        SessionLockCoordinator? sessionLock = null)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _pinService = pinService;
        _sessionLock = sessionLock;
        PinOverlay.PinService = pinService;

        // 会话锁是跨窗口共享的单一状态：解锁一次即全会话解锁。
        // 旧实现在此探测主列表窗口的解锁状态，探测失败时本窗口会**额外再要一次 PIN**。
        if (_sessionLock != null)
        {
            _sessionLock.LockRequested += (_, _) => SyncLockState();
            _sessionLock.Unlocked += (_, _) => SyncLockState();
            PinOverlay.Unlocked += (_, _) => _sessionLock.Unlock();
        }

        Loaded += async (_, _) =>
        {
            SyncLockState();

            try
            {
                await ViewModel.LoadArchivedNotesAsync();
            }
            catch (Exception ex)
            {
                // 数据库/IO 异常不允许在 async void 中崩溃进程（P2-5）
                AppLog.Error($"[ArchivedNotesWindow] 加载归档列表失败: {ex.Message}", ex);
                MessageBox.Show(this, $"加载归档列表失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            FocusArchivedList();
        };

        // 键盘支持（F-P1-10 剩余项）：Ctrl+F 聚焦搜索框（占位文案此前承诺了该快捷键但未实现，N-6）、
        // Esc 清空搜索词；列表内 ↑/↓/Home/End 由 ListBox 原生导航承担。
        PreviewKeyDown += ArchivedNotesWindow_PreviewKeyDown;
    }

    /// <summary>
    /// 把会话锁状态同步到遮罩。窗口只反映状态，不自行判定锁定时机。
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
    /// 加载完成后恢复键盘焦点：有归档则落到首项（↑/↓ 即可导航），否则落到搜索框
    /// </summary>
    private void FocusArchivedList()
    {
        if (PinOverlay.IsLocked) return;

        if (ArchivedNotesListBox.Items.Count > 0)
        {
            ArchivedNotesListBox.SelectedIndex = 0;
            if (ArchivedNotesListBox.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item)
            {
                item.Focus();
            }
        }
        else
        {
            SearchBox.Focus();
        }
    }

    private void ArchivedNotesWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+F 聚焦搜索框（N-6）：搜索框占位文案承诺了该快捷键但窗口原先并未实现
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            e.Handled = true;
            return;
        }

        // Esc 清空搜索词（与主列表窗口一致）
        if (e.Key == Key.Escape && ViewModel.SearchText.Length > 0)
        {
            ViewModel.SearchText = string.Empty;
            e.Handled = true;
        }
    }

    private async void RestoreNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Note note })
        {
            try
            {
                await ViewModel.RestoreNoteCommand.ExecuteAsync(note);
            }
            catch (Exception ex)
            {
                AppLog.Error($"[ArchivedNotesWindow] 恢复便签 {note.Id} 失败: {ex.Message}", ex);
                MessageBox.Show(this, $"恢复便签失败：{ex.Message}", "操作失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void HardDeleteNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Note note })
        {
            // 显式指定 Owner：避免确认框被主窗口遮挡，用户误以为无响应而重复点击（F-P1-10 剩余项）
            var result = MessageBox.Show(
                this,
                "确定要彻底删除该便签吗？\n此操作无法撤销，数据将永久丢失。",
                "彻底删除确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    await ViewModel.HardDeleteNoteCommand.ExecuteAsync(note);
                }
                catch (Exception ex)
                {
                    AppLog.Error($"[ArchivedNotesWindow] 彻底删除便签 {note.Id} 失败: {ex.Message}", ex);
                    MessageBox.Show(this, $"彻底删除失败：{ex.Message}", "操作失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private async void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "确定要清空所有已归档的便签吗？\n此操作将永久删除所有归档便签，无法撤销。",
            "清空归档确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning
        );

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                await ViewModel.ClearAllArchivedCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                AppLog.Error($"[ArchivedNotesWindow] 清空归档失败: {ex.Message}", ex);
                MessageBox.Show(this, $"清空归档失败：{ex.Message}", "操作失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
