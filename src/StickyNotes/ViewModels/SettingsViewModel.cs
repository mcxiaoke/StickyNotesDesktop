using System.Diagnostics;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Win32;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Services;
using StickyNotes.Services.Localization;
using StickyNotes.Views;

namespace StickyNotes.ViewModels;

public record FontSizeOption(string Label, double Size);

public record IdleLockOption(string Label, int Minutes);

/// <summary>语言下拉项：<see cref="Code"/> 为语言代码，<see cref="Label"/> 为该语言自身的显示名（不随当前语言翻译）</summary>
public record LanguageOption(string Code, string Label);

/// <summary>
/// 应用程序独立设置窗口 ViewModel（网络同步细节已拆分至 <see cref="SyncSettingsViewModel"/>）
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly ExportImportService _exportImportService;
    private readonly AutoStartService? _autoStartService;
    private readonly PinService? _pinService;
    private readonly SyncHost? _syncHost;
    private readonly WindowManager? _windowManager;

    public IReadOnlyList<FontSizeOption> FontSizeOptions { get; } = new List<FontSizeOption>
    {
        new(Loc.T("Settings.FontSize.S", "小 (12 pt)"), 12.0),
        new(Loc.T("Settings.FontSize.M", "标准 (14 pt - 默认)"), 14.0),
        new(Loc.T("Settings.FontSize.L", "中 (16 pt)"), 16.0),
        new(Loc.T("Settings.FontSize.XL", "大 (18 pt)"), 18.0),
        new(Loc.T("Settings.FontSize.XXL", "特大 (20 pt)"), 20.0),
        new(Loc.T("Settings.FontSize.Huge", "超大 (24 pt)"), 24.0)
    };

    public IReadOnlyList<IdleLockOption> IdleLockOptions { get; } = new List<IdleLockOption>
    {
        new(Loc.T("Settings.IdleLockOption.Off", "不启用"), 0),
        new(Loc.T("Settings.IdleLockOption.M1", "闲置 1 分钟后"), 1),
        new(Loc.T("Settings.IdleLockOption.M5", "闲置 5 分钟后"), 5),
        new(Loc.T("Settings.IdleLockOption.M10", "闲置 10 分钟后（默认）"), 10),
        new(Loc.T("Settings.IdleLockOption.M30", "闲置 30 分钟后"), 30),
        new(Loc.T("Settings.IdleLockOption.M60", "闲置 60 分钟后"), 60)
    };

    /// <summary>
    /// 可选界面语言。由 <see cref="I18nService.AvailableLanguages"/> 动态派生，
    /// 新增语言包文件后无需改代码即可出现在下拉框中。
    /// </summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    [ObservableProperty]
    private double _selectedFontSize;

    [ObservableProperty]
    private int _selectedIdleLockMinutes;

    /// <summary>
    /// 当前选中的界面语言。写入即生效：落盘 settings.json + 立即切换并广播刷新。
    /// </summary>
    [ObservableProperty]
    private string _selectedLanguage;

    partial void OnSelectedLanguageChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _settingsService.Language = value;
        I18nService.Instance.ApplyLanguage(value);
        WeakReferenceMessenger.Default.Send(new LanguageChangedMessage(value));

        // 选项标签含当前语言自身的语言名，切换后需重建下拉项文案
        OnPropertyChanged(nameof(LanguageOptions));
    }

    public string PreviewText => Loc.T("Settings.PreviewText",
        "这是一条便签示例文本：支持纯文本与多行输入，清晰易读 (StickyNotes 123 ABC)");

    public string AppVersion { get; }
    public string GitCommit { get; }
    public string BuildTime { get; }
    public string RuntimeInfo { get; }
    public string DataDirectoryPath => AppPaths.DataDirectory;
    public bool IsPortableMode => AppPaths.IsPortableMode;
    public string DeploymentModeDescription => AppPaths.DeploymentModeDescription;

    public bool MinimizeToTrayOnClose
    {
        get => _settingsService.MinimizeToTrayOnClose;
        set
        {
            if (_settingsService.MinimizeToTrayOnClose != value)
            {
                _settingsService.MinimizeToTrayOnClose = value;
                OnPropertyChanged();
            }
        }
    }

    public bool EnableGlobalHotKeys
    {
        get => _settingsService.EnableGlobalHotKeys;
        set
        {
            if (_settingsService.EnableGlobalHotKeys != value)
            {
                _settingsService.EnableGlobalHotKeys = value;
                WeakReferenceMessenger.Default.Send(new HotKeyConfigChangedMessage(value));
                OnPropertyChanged();
            }
        }
    }

    public bool StartMinimized
    {
        get => _settingsService.StartMinimized;
        set
        {
            if (_settingsService.StartMinimized != value)
            {
                _settingsService.StartMinimized = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsAutoStartEnabled
    {
        get => _autoStartService?.IsAutoStartEnabled ?? false;
        set
        {
            if (_autoStartService != null && _autoStartService.IsAutoStartEnabled != value)
            {
                _autoStartService.SetAutoStart(value);
                OnPropertyChanged();
                // 「开机启动时最小化到托盘」依赖于开机自启，状态变化必须通知其可操作性
                OnPropertyChanged(nameof(IsStartMinimizedEnabled));
            }
        }
    }

    /// <summary>
    /// 「开机启动时最小化到托盘」是否可操作。开机自启关闭时该项无意义，
    /// 若仍可切换会让用户看到自相矛盾的状态组合。
    /// </summary>
    public bool IsStartMinimizedEnabled => IsAutoStartEnabled;

    /// <summary>PIN 锁定是否生效</summary>
    public bool IsPinEnabled => _pinService?.IsPinEnabled ?? false;

    /// <summary>PIN 配置数据是否已损坏（settings.json 中的盐/哈希缺失或非法，锁定已被强制失效）</summary>
    public bool IsPinDataCorrupted => _settingsService.Settings.PinDataCorrupted;

    /// <summary>
    /// PIN 锁定状态描述文字。
    /// 注意不能再写成「打开列表与归档时需输入 PIN」—— 闲时锁定改造后，
    /// 打开窗口不再必然要求 PIN，锁定时机由闲置时长与启动锁定两项决定。
    /// </summary>
    public string PinStatusText => IsPinEnabled
        ? Loc.T("Settings.PinStatusEnabled", "已启用，启动时与长时间未操作后需要输入 PIN")
        : Loc.T("Settings.PinStatusDisabled", "未启用");

    /// <summary>锁定范围声明：桌面便签不在保护范围内，必须显式写明，避免用户误判</summary>
    public string PinScopeText => Loc.T("Settings.PinScope", "当前锁定范围：便签列表窗口。");

    /// <summary>
    /// 是否处于「PIN 已启用但两道防线全关」的假安全状态。
    /// 沿用 F-P1-6 的原则：绝不允许「以为受保护实则未受保护」静默发生。
    /// </summary>
    public bool IsPinProtectionDisabled =>
        IsPinEnabled && !IsPinDataCorrupted && SelectedIdleLockMinutes == 0 && !LockOnLaunch;

    /// <summary>假安全告警文案</summary>
    public string PinProtectionDisabledWarning =>
        Loc.T("Settings.PinProtectionDisabled", "⚠ 已启用 PIN，但「闲置自动锁定」与「启动时锁定」都已关闭。");

    /// <summary>进程冷启动时是否立即锁定</summary>
    public bool LockOnLaunch
    {
        get => _settingsService.LockOnLaunch;
        set
        {
            if (_settingsService.LockOnLaunch != value)
            {
                _settingsService.LockOnLaunch = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPinProtectionDisabled));
            }
        }
    }

    public SettingsViewModel(
        SettingsService settingsService,
        ExportImportService exportImportService,
        AutoStartService? autoStartService = null,
        PinService? pinService = null,
        SyncHost? syncHost = null,
        WindowManager? windowManager = null)
    {
        _settingsService = settingsService;
        _exportImportService = exportImportService;
        _autoStartService = autoStartService;
        _pinService = pinService;
        _syncHost = syncHost;
        _windowManager = windowManager;

        _selectedFontSize = _settingsService.EditorFontSize;
        _selectedIdleLockMinutes = _settingsService.IdleLockMinutes;

        // 语言下拉：首项恒为「跟随系统」，其后是各语言包自报的原生显示名
        // （语言名按惯例不翻译，用户在英文界面下也需看到 "简体中文" 而非 "Chinese (Simplified)"）。
        var languageOptions = new List<LanguageOption>
        {
            new(I18nService.AutoLanguage, Loc.T("Tray.LangAuto", "跟随系统 (Auto)"))
        };
        languageOptions.AddRange(
            I18nService.Instance.AvailableLanguages
                .Where(p => !string.Equals(p.Key, I18nService.SourceLanguage, StringComparison.OrdinalIgnoreCase))
                .Select(p => new LanguageOption(p.Key, p.Value))
                .OrderBy(p => p.Code, StringComparer.OrdinalIgnoreCase));
        LanguageOptions = languageOptions;

        // 选中项用「已解析后的实际语言」而非配置原值：配置存的是 auto 时，
        // 下拉框必须高亮到当前真正生效的那一档，否则界面与选中项对不上。
        _selectedLanguage = _settingsService.Language;
        if (string.Equals(_selectedLanguage, I18nService.AutoLanguage, StringComparison.OrdinalIgnoreCase))
        {
            _selectedLanguage = I18nService.Instance.CurrentLanguage;
        }

        if (_syncHost != null)
        {
            _syncHost.StateChanged += RefreshSyncStatusOnDispatcher;
        }

        RefreshSyncStatus();

        // 获取程序集构建与版本信息
        var assembly = Assembly.GetExecutingAssembly();
        var infoVer = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        AppVersion = !string.IsNullOrWhiteSpace(infoVer) ? infoVer : (assembly.GetName().Version?.ToString(3) ?? "1.0.0");

        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(m => m.Key, m => m.Value);
        GitCommit = metadata.TryGetValue("GitCommit", out var commit) && !string.IsNullOrWhiteSpace(commit) ? commit : "dev";
        BuildTime = metadata.TryGetValue("BuildTime", out var time) && !string.IsNullOrWhiteSpace(time) ? time : DateTime.Now.ToString("yyyy-MM-dd");

        RuntimeInfo = $".NET 8.0 ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";

        // 界面语言变更后，本 VM 派生自语言包的属性不会自动刷新（它们是普通 CLR 属性而非绑定），
        // 必须显式重发 PropertyChanged。用弱引用总线，无需退订。
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (_, _) => RefreshLocalizedText());
    }

    /// <summary>
    /// 语言切换后重发所有派生自语言包的属性通知。
    /// </summary>
    private void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(PinStatusText));
        OnPropertyChanged(nameof(PinScopeText));
        OnPropertyChanged(nameof(PinProtectionDisabledWarning));
        OnPropertyChanged(nameof(FontSizeOptions));
        OnPropertyChanged(nameof(IdleLockOptions));
        OnPropertyChanged(nameof(SyncStatusText));
        RefreshSyncStatus();
    }

    partial void OnSelectedFontSizeChanged(double value)
    {
        _settingsService.SetEditorFontSize(value);
    }

    partial void OnSelectedIdleLockMinutesChanged(int value)
    {
        _settingsService.IdleLockMinutes = value;
        OnPropertyChanged(nameof(IsPinProtectionDisabled));
    }

    /// <summary>解析用于弹对话框的宿主窗口（当前激活窗口或主窗口）</summary>
    private static Window? GetOwnerWindow() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;

    private void RefreshPinState()
    {
        OnPropertyChanged(nameof(IsPinEnabled));
        OnPropertyChanged(nameof(PinStatusText));
        OnPropertyChanged(nameof(IsPinDataCorrupted));
        OnPropertyChanged(nameof(IsPinProtectionDisabled));
    }

    [RelayCommand]
    private void EnablePin()
    {
        if (_pinService == null || IsPinEnabled) return;

        AppLog.Info("[SettingsViewModel] 用户请求启用 PIN 锁定");
        if (PinSetupDialog.Execute(GetOwnerWindow(), _pinService, PinDialogMode.Enable))
        {
            // 用户重新设置成功后，清除「数据已损坏」告警标记
            _settingsService.Settings.PinDataCorrupted = false;
            RefreshPinState();
            AppLog.Info("[SettingsViewModel] PIN 锁定已启用");
        }
    }

    [RelayCommand]
    private void ChangePin()
    {
        if (_pinService == null || !IsPinEnabled) return;

        AppLog.Info("[SettingsViewModel] 用户请求修改 PIN");
        if (PinSetupDialog.Execute(GetOwnerWindow(), _pinService, PinDialogMode.Change))
        {
            RefreshPinState();
            AppLog.Info("[SettingsViewModel] PIN 已修改");
        }
    }

    [RelayCommand]
    private void ClearPin()
    {
        if (_pinService == null || !IsPinEnabled) return;

        AppLog.Info("[SettingsViewModel] 用户请求清除 PIN");
        if (PinSetupDialog.Execute(GetOwnerWindow(), _pinService, PinDialogMode.Disable))
        {
            RefreshPinState();
            AppLog.Info("[SettingsViewModel] PIN 已清除，锁定失效");
        }
    }

    [RelayCommand]
    public async Task ExportJsonAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("ExportImport.ExportTitle", "导出所有便签备份"),
            Filter = Loc.T("ExportImport.ExportFilter", "JSON 文件 (*.json)|*.json"),
            FileName = $"StickyNotes_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                int count = await _exportImportService.ExportNotesAsync(dialog.FileName);
                MessageBox.Show(
                    Loc.T("ExportImport.ExportSuccess", "导出成功！\n共导出 {0} 条便签至文件：\n{1}",
                        count, dialog.FileName),
                    Loc.T("ExportImport.ExportDoneTitle", "便签导出完成"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Loc.T("ExportImport.ExportFailed", "导出失败: {0}", ex.Message),
                    Loc.T("ExportImport.ExportErrorTitle", "导出错误"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }
    }

    [RelayCommand]
    public async Task ImportJsonAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("ExportImport.ImportTitle", "导入便签备份文件"),
            Filter = Loc.T("ExportImport.ImportFilter", "JSON 文件 (*.json)|*.json")
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                int count = await _exportImportService.ImportNotesAsync(dialog.FileName);
                MessageBox.Show(
                    Loc.T("ExportImport.ImportSuccess", "导入完成！\n成功导入/合并了 {0} 条便签。", count),
                    Loc.T("ExportImport.ImportDoneTitle", "便签导入完成"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );

                // 通知主管理窗口全量重载（导入可能新增/更新任意条数，不能逐条插卡片）。
                // 禁止改回 NoteCreatedMessage + new Note()：那条路径会生成一张幽灵便签。原 F-P1-2 子问题 2。
                WeakReferenceMessenger.Default.Send(new NotesReloadedRequestedMessage());
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Loc.T("ExportImport.ImportFailed", "导入失败: {0}", ex.Message),
                    Loc.T("ExportImport.ImportErrorTitle", "导入错误"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }
    }

    [RelayCommand]
    public void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.T("Common.OpenFolderFailed", "打开目录失败: {0}", ex.Message),
                Loc.T("Common.Warning", "警告"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------- 网络同步（入口与状态；详细配置在独立窗口） ----------------

    [RelayCommand]
    private void OpenSyncSettings()
    {
        if (_windowManager == null) return;
        _windowManager.OpenOrActivateSyncSettingsWindow();
    }

    private string _syncStatusText = Loc.T("Sync.StatusNotEnabled", "同步未启用");
    /// <summary>设置页入口行的同步状态摘要（最近成功时间 / 最近错误）</summary>
    public string SyncStatusText
    {
        get => _syncStatusText;
        private set => SetProperty(ref _syncStatusText, value);
    }

    private void RefreshSyncStatusOnDispatcher()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            RefreshSyncStatus();
            return;
        }

        _ = dispatcher.BeginInvoke(RefreshSyncStatus);
    }

    private void RefreshSyncStatus()
    {
        if (_syncHost == null)
        {
            SyncStatusText = Loc.T("Sync.StatusNotRunning", "同步服务未运行");
            return;
        }

        if (!_settingsService.Settings.Sync.Enabled)
        {
            SyncStatusText = Loc.T("Sync.StatusNotEnabled", "同步未启用");
            return;
        }

        var state = _syncHost.CurrentState;
        var countsSummary = state.LastUploadedCount.HasValue || state.LastDownloadedCount.HasValue
            ? Loc.T("Sync.StatusCounts", "（上传 {0}，下载 {1}）",
                state.LastUploadedCount ?? 0, state.LastDownloadedCount ?? 0)
            : string.Empty;

        if (!string.IsNullOrEmpty(state.LastError))
        {
            var lastOk = state.LastSuccessAt != null
                ? state.LastSuccessAt.Value.ToLocalTime().ToString("MM-dd HH:mm") + countsSummary
                : Loc.T("Sync.StatusNeverSynced", "从未成功");
            SyncStatusText = Loc.T("Sync.StatusError", "同步异常，最近成功：{0}（详见同步设置）", lastOk);
        }
        else if (state.LastSuccessAt != null)
        {
            SyncStatusText = Loc.T("Sync.StatusSuccess", "最近同步：{0}",
                state.LastSuccessAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + countsSummary);
        }
        else
        {
            SyncStatusText = Loc.T("Sync.StatusNotYetSynced", "已启用，尚未同步");
        }
    }
}
