using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Services.Localization;

namespace StickyNotes.Services;

/// <summary>
/// Windows 系统任务栏托盘图标服务 (基于成熟的 H.NotifyIcon.Wpf 控件)
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly WindowManager _windowManager;
    private readonly SettingsService _settingsService;
    private TaskbarIcon? _taskbarIcon;
    private HwndSource? _activationHelperHwnd;
    private bool _isDisposed;

    // 托盘菜单项引用：语言切换时用于改写文案（代码构建的 UI 不参与 XAML 绑定刷新）
    private MenuItem? _menuNew;
    private MenuItem? _menuOpen;
    private MenuItem? _menuSettings;
    private MenuItem? _menuExit;

    public TrayIconService(WindowManager windowManager, SettingsService settingsService)
    {
        _windowManager = windowManager;
        _settingsService = settingsService;

        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (_, _) => RefreshTrayTexts());
    }

    /// <summary>
    /// 供单元测试或诊断查看底层 TaskbarIcon 实例
    /// </summary>
    internal TaskbarIcon? TaskbarIconForTest => _taskbarIcon;

    /// <summary>
    /// 初始化托盘图标及右键上下文菜单与第二实例唤醒消息监听
    /// </summary>
    public void Initialize()
    {
        if (_taskbarIcon != null || _isDisposed)
        {
            return;
        }

        // 1. 初始化第二实例唤醒监听窗口（解耦的轻量级消息接收器）
        // 主列表窗口在 --minimized/--autostart 启动路径下不会被 Show()，没有 HWND；
        // 因此常驻后台必须有专用的 HwndSource 监听 WM_ACTIVATE_INSTANCE。
        InitActivationHelperWindow();

        // 2. 初始化 TaskbarIcon 托盘控件
        // 只绑定 LeftClickCommand：双击时 Shell 会先送左键抬起再送双击事件，激活动作本就
        // 会被幂等地执行多次；再绑 DoubleClickCommand 只会让同一次双击触发三遍激活（N-5）。
        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = Loc.T("Tray.Tooltip", "彩色便签"),
            MenuActivation = PopupActivationMode.RightClick,
            LeftClickCommand = new RelayCommand(OpenOrActivateListWindow)
        };

        // 加载应用程序托盘图标
        LoadTrayIcon(_taskbarIcon);

        // 3. 构建上下文菜单
        _taskbarIcon.ContextMenu = CreateContextMenu();

        // 强制创建托盘图标（通过 C# 编程式构造时调用）
        try
        {
            _taskbarIcon.ForceCreate();
            AppLog.Info("[TrayIconService] 系统托盘图标创建成功 (H.NotifyIcon.Wpf)");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[TrayIconService] ForceCreate 托盘图标异常: {ex.Message}", ex);
        }
    }


    private void LoadTrayIcon(TaskbarIcon taskbarIcon)
    {
        // 优先使用 Pack URI 加载嵌入的 AppIcon.ico 资源
        try
        {
            var uri = new Uri("pack://application:,,,/StickyNotes;component/Assets/AppIcon.ico", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(uri);
            if (streamInfo != null)
            {
                using (streamInfo.Stream)
                {
                    taskbarIcon.Icon = new System.Drawing.Icon(streamInfo.Stream);
                    return;
                }
            }

            taskbarIcon.IconSource = new BitmapImage(uri);
            return;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[TrayIconService] Pack URI 加载托盘图标失败: {ex.Message}，尝试本地文件降级", ex);
        }

        // 降级尝试：从当前程序运行目录或 Assets 目录直接读取
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
                Path.Combine(AppContext.BaseDirectory, "Resources", "App.ico"),
                Path.Combine(AppContext.BaseDirectory, "AppIcon.ico")
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    using var fs = File.OpenRead(path);
                    taskbarIcon.Icon = new System.Drawing.Icon(fs);
                    return;
                }
            }
        }
        catch (Exception fallbackEx)
        {
            AppLog.Warn($"[TrayIconService] 本地文件降级加载托盘图标失败: {fallbackEx.Message}", fallbackEx);
        }
    }

    private void InitActivationHelperWindow()
    {
        if (_activationHelperHwnd != null)
        {
            return;
        }

        try
        {
            var parameters = new HwndSourceParameters("StickyNotes_InstanceHelper")
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0
            };

            _activationHelperHwnd = new HwndSource(parameters);
            _activationHelperHwnd.AddHook(ActivationHook);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[TrayIconService] 创建第二实例激活监听窗口失败: {ex.Message}", ex);
        }
    }

    private IntPtr ActivationHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 实例唤醒消息必须由本窗口兜底处理（而不是只挂主列表窗口）：
        // 主列表窗口在 --minimized/--autostart 启动路径下不会被 Show()，
        // 因此它此刻没有 HWND，消息永远收不到；本隐藏 helper 窗口则始终存在。
        if (msg == NativeMethods.WM_ACTIVATE_INSTANCE && NativeMethods.WM_ACTIVATE_INSTANCE != 0)
        {
            handled = true;

            var dispatcher = _activationHelperHwnd?.Dispatcher;
            if (dispatcher != null)
            {
                dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Normal,
                    new Action(OpenOrActivateListWindow));
            }
            else
            {
                OpenOrActivateListWindow();
            }

            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void OpenOrActivateListWindow()
    {
        _windowManager.OpenOrActivateNotesListWindow();
    }

    private ContextMenu CreateContextMenu()
    {
        var contextMenu = new ContextMenu();

        // 菜单仅保留四个常用动作，文案求短，功能与行为保持原样不变。
        // 托盘菜单是代码构建的，不走 XAML 绑定，故语言切换时需手工改写 Header（见 RefreshTrayTexts）。
        var itemNew = new MenuItem { Header = Loc.T("Tray.NewNote", "新建") };
        itemNew.Click += (_, _) => WeakReferenceMessenger.Default.Send(new NewNoteRequestedMessage());
        contextMenu.Items.Add(itemNew);
        _menuNew = itemNew;

        var itemList = new MenuItem { Header = Loc.T("Tray.Open", "打开") };
        itemList.Click += (_, _) => WeakReferenceMessenger.Default.Send(new ShowNotesListRequestedMessage());
        contextMenu.Items.Add(itemList);
        _menuOpen = itemList;

        var itemSettings = new MenuItem { Header = Loc.T("Tray.Settings", "设置") };
        itemSettings.Click += (_, _) => _windowManager.OpenOrActivateSettingsWindow();
        contextMenu.Items.Add(itemSettings);
        _menuSettings = itemSettings;

        contextMenu.Items.Add(new Separator());

        var itemExit = new MenuItem { Header = Loc.T("Tray.Exit", "退出") };
        itemExit.Click += (_, _) =>
        {
            AppLog.Info("[TrayIconService] 用户点击托盘退出菜单");
            // 退出顺序至关重要（F-P0-1）：先持久化置顶便签坐标 —— 此刻窗口字典仍完整；
            // 若拖到 App.OnExit 才做，Shutdown() 已先关闭全部窗口并清空字典，坐标将永久丢失。
            // 随后标记退出，使窗口 Closed 回调跳过 IsOpen=false 回写。
            // 先置全局退出标志（P1-6）：让带未保存草稿的窗口（如同步设置）在 Closing 中
            // 识别到退出流程并跳过确认框，避免 Shutdown 被取消后应用停留在半退出状态。
            App.BeginShutdown();
            _windowManager.BeginShutdownAndPersistPinnedPlacement();

            // 最后关闭应用。
            Application.Current.Shutdown();
        };
        contextMenu.Items.Add(itemExit);
        _menuExit = itemExit;

        return contextMenu;
    }

    /// <summary>
    /// 界面语言切换后改写托盘图标提示与菜单文案。
    /// 托盘菜单与 ToolTip 均为代码构建，不参与 XAML 绑定刷新，必须手工同步。
    /// </summary>
    private void RefreshTrayTexts()
    {
        if (_taskbarIcon != null)
        {
            _taskbarIcon.ToolTipText = Loc.T("Tray.Tooltip", "彩色便签");
        }

        if (_menuNew != null) _menuNew.Header = Loc.T("Tray.NewNote", "新建");
        if (_menuOpen != null) _menuOpen.Header = Loc.T("Tray.Open", "打开");
        if (_menuSettings != null) _menuSettings.Header = Loc.T("Tray.Settings", "设置");
        if (_menuExit != null) _menuExit.Header = Loc.T("Tray.Exit", "退出");
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        if (_taskbarIcon != null)
        {
            _taskbarIcon.Dispose();
            _taskbarIcon = null;
        }

        if (_activationHelperHwnd != null)
        {
            _activationHelperHwnd.RemoveHook(ActivationHook);
            _activationHelperHwnd.Dispose();
            _activationHelperHwnd = null;
        }
    }
}
