using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Services;
using StickyNotes.Services.Localization;
using StickyNotes.Sync;
using StickyNotes.ViewModels;
using StickyNotes.Views;

namespace StickyNotes;

/// <summary>
/// 应用程序主入口生命周期管理
/// </summary>
public partial class App : Application
{
    private IServiceProvider? _serviceProvider;
    private bool _hasPerformedShutdown;

    /// <summary>
    /// 全局应用是否正在关闭中（用于隔离普通窗口关闭与应用退出）
    /// </summary>
    public static bool IsShuttingDown { get; private set; }

    /// <summary>
    /// 在**发起退出之前**置位关闭标志（托盘「退出便签」、系统注销/关机等所有主动退出路径都必须先调用）。
    /// 这是 F-P0-1 的关键：WPF 的真实事件顺序是「全部窗口 Closed → App.OnExit」，
    /// 若等到 OnExit 才置位，窗口关闭回调会误判为「用户主动关闭」而把 IsOpen 写成 false，
    /// 导致下次启动一张贴纸都不出现。因此必须在 Shutdown() 之前先声明退出意图。
    /// </summary>
    public static void BeginShutdown()
    {
        if (IsShuttingDown) return;
        IsShuttingDown = true;
        AppLog.Info("[App] 已进入退出流程，窗口关闭回调将不再回写 IsOpen=false");
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 2. 注册三层全局异常捕获，记录日志并安全刷盘，杜绝静默崩溃与数据丢失
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error($"[App] Dispatcher 未处理异常: {args.Exception.Message}", args.Exception);

            try
            {
                _serviceProvider?.GetService<AutoSaveCoordinator>()?.FlushAllDirectToStorage();
            }
            catch (Exception flushEx)
            {
                AppLog.Error($"[App] 异常兜底刷盘失败: {flushEx.Message}", flushEx);
            }

            // 仅对「已知可恢复」的异常标记为已处理，其余一律放行，让应用按正常崩溃路径退出。
            // 原实现无条件 args.Handled = true，会把破坏数据一致性的严重异常也吞掉，
            // 使应用在半坏状态下继续运行且用户毫无提示（原 F-P2-28）。
            args.Handled = IsRecoverable(args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLog.Error($"[App] AppDomain 未处理异常: {ex.Message}", ex);
            }
            try
            {
                _serviceProvider?.GetService<AutoSaveCoordinator>()?.FlushAllDirectToStorage();
            }
            catch (Exception flushEx)
            {
                AppLog.Error($"[App] AppDomain 异常兜底刷盘失败: {flushEx.Message}", flushEx);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error($"[App] TaskScheduler 未观察异常: {args.Exception.Message}", args.Exception);
            args.SetObserved();
        };

        // 3. 监听 Windows 系统关机/注销事件
        SystemEvents.SessionEnding += OnSessionEnding;

        AppLog.Info($"[App] StickyNotes 启动，环境: {Environment.Version}, OS: {Environment.OSVersion}");

        // 4. 构建依赖注入服务容器
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // 4a. 应用界面语言。必须早于数据库初始化与任何窗口创建 ——
        // 连「启动失败」弹窗都应使用用户选定的语言，否则语言设置对启动期报错完全无效。
        // 解析 SettingsService 会首次读取 settings.json，此处只读不写，不影响原子写语义。
        try
        {
            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
            I18nService.EnsureInstance().Init(settingsService.Language);
        }
        catch (Exception ex)
        {
            // 语言加载失败绝不能阻断启动：语言包问题不该让用户的便签打不开
            AppLog.Error($"[App] 界面语言初始化失败，已回退默认语言（应用继续启动）: {ex.Message}", ex);
        }

        // 5. 初始化数据库与执行 PRAGMA 增量迁移。
        // 整个启动链必须显式兜底：OnStartup 是 async void，若在此处抛异常，
        // DispatcherUnhandledException 会把它记为已处理，而此时既无主窗口也无托盘图标，
        // 进程却继续存活并已持有单实例 Mutex —— 用户此后双击快捷方式一律被判定为
        // 「第二实例」而秒退，表现为「点了没反应，且再也打不开」（原 F-P1-3）。
        try
        {
            var dbCtx = _serviceProvider.GetRequiredService<SqliteDatabaseContext>();
            await dbCtx.InitializeAndMigrateAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error($"[App] 数据库初始化/迁移失败，启动中止: {ex.Message}", ex);
            ShowStartupFailure(AppPaths.DatabasePath, ex);
            Shutdown(1);
            return;
        }

        // 6. 每日启动冷备份在后台线程异步执行，不阻塞 UI 渲染呈现
        _ = Task.Run(() => BackupService.RunDailyBackupIfNeeded());

        // 7. 恢复上次未关闭的便签贴纸（保持原位与物理尺寸）
        try
        {
            // 显式解析会话锁：其构造会启动「全局输入 + 闲置计时 + 锁屏监听」，
            // 必须早于任何窗口创建，保证 --autostart/--minimized 路径下冷启动锁定同样生效。
            _serviceProvider.GetRequiredService<SessionLockCoordinator>();

            var repo = _serviceProvider.GetRequiredService<INoteRepository>();
            var windowManager = _serviceProvider.GetRequiredService<WindowManager>();
            var allActive = await repo.GetAllActiveAsync();

            foreach (var note in allActive.Where(n => n.IsOpen))
            {
                windowManager.OpenOrActivateNote(note);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"[App] 恢复桌面便签失败，启动中止: {ex.Message}", ex);
            ShowStartupFailure(AppPaths.DatabasePath, ex);
            Shutdown(1);
            return;
        }

        // 8. 保持后台托盘常驻模式
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 9. 初始化系统托盘图标与全局快捷键。
        // 此段必须有独立兜底：托盘/热键初始化若抛异常而无人捕获，会经
        // DispatcherUnhandledException 走崩溃路径，用户只看到一次无提示的闪退（N-10）。
        // 两者失败都不影响核心功能，降级为「无托盘 / 无热键」继续运行并记录日志。
        try
        {
            _serviceProvider.GetRequiredService<TrayIconService>().Initialize();
        }
        catch (Exception ex)
        {
            AppLog.Error($"[App] 托盘图标初始化失败（应用继续运行，仅缺少托盘图标）: {ex.Message}", ex);
        }

        try
        {
            _serviceProvider.GetRequiredService<HotKeyService>().Initialize();
        }
        catch (Exception ex)
        {
            AppLog.Error($"[App] 全局热键初始化失败（应用继续运行，仅缺少全局热键）: {ex.Message}", ex);
        }

        // 9a. 启动网络同步触发器（未启用同步时内部为空转，失败不影响主流程）
        try
        {
            _serviceProvider.GetRequiredService<SyncHost>().Start();
        }
        catch (Exception ex)
        {
            AppLog.Error($"[App] 同步服务启动失败（应用继续运行，仅缺少网络同步）: {ex.Message}", ex);
        }

        // 10. 管理中心主窗口呈现（若携带 --autostart 或 --minimized 参数，则保持托盘静默不弹窗）。
        // 主窗口是最后的功能底线：创建/呈现失败时给出可读提示并安全退出，而非无声闪退。
        try
        {
            var mainWindow = _serviceProvider.GetRequiredService<NotesListWindow>();
            MainWindow = mainWindow;

            bool startMinimized = e.Args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase) ||
                                                  a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            if (!startMinimized)
            {
                mainWindow.Show();
            }
            else
            {
                AppLog.Info("[App] 检测到自启动参数 (--autostart/--minimized)，主窗口保持在后台托盘");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"[App] 主窗口创建/呈现失败，启动中止: {ex.Message}", ex);
            ShowStartupFailure(AppPaths.DatabasePath, ex);
            Shutdown(1);
            return;
        }

        // 11. 启动初始化稳定后，延迟 5 秒修剪冷启动与 JIT 编译产生的瞬时工作集页面
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            NativeMethods.TrimWorkingSet();
        });
    }

    /// <summary>
    /// 判断异常是否属于「已知可恢复」类别。只有这类异常才允许被标记为已处理并继续运行；
    /// 数据一致性、IO、内存等严重异常一律放行，交由正常崩溃路径处理并留下可见痕迹。
    /// </summary>
    private static bool IsRecoverable(Exception ex) => ex switch
    {
        // UI/标记层异常：出现即意味着界面状态已不可信，一律放行走崩溃路径，绝不吞掉
        InvalidOperationException => false,
        NotSupportedException => false,
        System.Windows.Markup.XamlParseException => false,

        // 明确的 IO / 内存 / 数据库类异常绝不吞掉
        System.IO.IOException => false,
        UnauthorizedAccessException => false,
        OutOfMemoryException => false,
        Microsoft.Data.Sqlite.SqliteException => false,

        // ArgumentException 多由绑定/转换器参数引起，属可恢复的表现层问题
        ArgumentException => true,

        // 其余未知异常默认放行，避免把真实故障变成静默半坏状态
        _ => false
    };

    /// <summary>
    /// 启动失败时给出可读提示（含数据库路径与日志路径），避免静默僵尸进程占死单实例 Mutex
    /// </summary>
    private void ShowStartupFailure(string databasePath, Exception ex)
    {
        try
        {
            MessageBox.Show(
                Loc.T("App.StartupFailedBody",
                    "StickyNotes 启动失败，已安全退出。\n\n" +
                    $"原因：{ex.Message}\n\n" +
                    $"数据库：{databasePath}\n" +
                    $"日志：{AppPaths.LogsDirectory}\n\n" +
                    "可尝试：从 backups/ 目录恢复一份备份，或重命名数据库文件后重新启动。",
                    ex.Message, databasePath, AppPaths.LogsDirectory),
                Loc.T("App.StartupFailedTitle", "启动失败"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception showEx)
        {
            // 弹窗本身失败时至少保证日志里有记录
            AppLog.Error($"[App] 启动失败提示框展示失败: {showEx.Message}", showEx);
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        AppLog.Info($"[App] 收到操作系统注销/关机通知 ({e.Reason})，立即同步落盘");
        // 关机不经过 Shutdown()，此处必须直接完成「先存坐标、再标记退出」的原子动作
        _serviceProvider?.GetService<WindowManager>()?.BeginShutdownAndPersistPinnedPlacement();
        PerformSafeShutdown();
    }

    /// <summary>
    /// 执行应用退出前的统一安全持久化（坐标记忆 + 脏数据刷盘）。
    /// 主路径已在 TrayIconService 中于 Shutdown() 之前调用（那时窗口字典仍完整）；
    /// 此处的调用是对 SessionEnding、异常退出等场景的兜底，用 _hasPerformedShutdown 保证幂等。
    /// </summary>
    private void PerformSafeShutdown()
    {
        if (_hasPerformedShutdown) return;

        _hasPerformedShutdown = true;

        try
        {
            // 1. 仅持久化「桌面置顶」便签的精确坐标与尺寸（不写 IsOpen，避免依赖退出事件顺序）
            _serviceProvider?.GetService<WindowManager>()?.PersistPinnedWindowsPlacementOnExit();

            // 2. 声明退出：确保随后窗口关闭回调不回写 IsOpen=false（幂等）
            BeginShutdown();

            // 3. 将所有待写脏数据直接写入 SQLite，绝不走 UI 消息总线，防止死锁
            _serviceProvider?.GetService<AutoSaveCoordinator>()?.FlushAllDirectToStorage();

            // 4. 停止同步触发器与后端连接（先落盘再停同步，保证退出前最后一批修改已入库）
            _serviceProvider?.GetService<SyncHost>()?.Dispose();

            AppLog.Info("[App] 退出前安全落盘完成");
        }
        catch (Exception ex)
        {
            AppLog.Error($"[App] 退出安全落盘异常: {ex.Message}", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        PerformSafeShutdown();

        _serviceProvider?.GetService<TrayIconService>()?.Dispose();
        _serviceProvider?.GetService<HotKeyService>()?.Dispose();

        if (_serviceProvider is IDisposable disp)
        {
            disp.Dispose();
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // 核心基础设施与数据层
        services.AddSingleton<SqliteDatabaseContext>();
        // 硬删除台账（P2-1）：仓储写入、同步引擎读取，先于其消费者注册
        services.AddSingleton<HardDeleteLedger>();
        services.AddSingleton<INoteRepository, NoteRepository>();

        // 领域服务与配置
        services.AddSingleton(I18nService.EnsureInstance());
        services.AddSingleton<SettingsService>();
        services.AddSingleton<PinService>();
        // 会话级空闲锁：必须在各窗口之前注册，使锁定状态成为进程内唯一真相源。
        // 冷启动初值（LockOnLaunch）在此构造时即确定。
        services.AddSingleton<SessionLockCoordinator>();
        services.AddSingleton<ExportImportService>();
        services.AddSingleton<ISearchService, SearchService>();
        services.AddSingleton<AutoSaveCoordinator>(sp =>
            new AutoSaveCoordinator(sp.GetRequiredService<INoteRepository>()));
        services.AddSingleton<WindowManager>();
        services.AddSingleton<AutoStartService>();
        services.AddSingleton<HotKeyService>();
        services.AddSingleton<TrayIconService>();

        // 网络同步
        services.AddSingleton<SyncEngine>();
        services.AddSingleton<SyncStateStore>();
        services.AddSingleton<SyncHost>();

        // ViewModels
        services.AddSingleton<NotesListViewModel>();
        services.AddSingleton<ArchivedNotesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<SyncSettingsViewModel>();
        services.AddTransient<NoteViewModel>();

        // Views
        services.AddSingleton<NotesListWindow>();
        services.AddTransient<ArchivedNotesWindow>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<SyncSettingsWindow>();
    }
}
