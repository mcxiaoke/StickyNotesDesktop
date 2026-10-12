using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StickyNotes.Infrastructure;
using StickyNotes.Services;
using StickyNotes.Sync;

namespace StickyNotes.ViewModels;

public record SyncBackendOption(string Label, SyncBackendType Type);

public record SyncIntervalOption(string Label, int Minutes);

/// <summary>
/// 网络同步独立设置窗口 ViewModel（自 SettingsViewModel 拆分，2026-10-04）：
/// 后端选择与凭据、测试连接、立即同步、同步状态展示。
///
/// 2026-10-05 改为「草稿 + 显式保存」模型：
/// 之前所有输入框 setter 直接写 Sync 并立即落盘（每敲一个字写一次 settings.json），
/// 且「测试连接」用界面当前值构造临时后端，等于测试一通过界面值就已替换旧配置。
/// 现在界面所有编辑只改草稿字段，点「保存」才写回 Sync 并落盘；
/// 「测试连接」同样只读草稿，不触碰已保存配置；关闭窗口未保存则丢弃草稿。
/// </summary>
public partial class SyncSettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly SyncHost? _syncHost;

    // ---- 草稿：WebDAV 密码 / S3 SecretKey 输入框的明文即时值（保存时才 DPAPI 加密落盘；空表示沿用已存凭据） ----
    private string _webDavPasswordInput = string.Empty;
    private string _s3SecretKeyInput = string.Empty;

    // ---- 草稿：其余可编辑项（保存时才写回 Sync） ----
    private bool _syncEnabledDraft;
    private bool _syncEnableEncryptionDraft;
    private bool _syncAllowInsecureHttpDraft;
    private string _syncWebDavUrlDraft = string.Empty;
    private string _syncWebDavUsernameDraft = string.Empty;
    private string _syncS3EndpointDraft = string.Empty;
    private string _syncS3BucketDraft = string.Empty;
    private string _syncS3BasePrefixDraft = string.Empty;
    private string _syncS3AccessKeyDraft = string.Empty;

    /// <summary>构造期初始化草稿时抑制"未保存"标记</summary>
    private bool _suppressDraftDirty;

    [ObservableProperty]
    private bool _isSyncBusy;

    partial void OnIsSyncBusyChanged(bool value) => SaveSyncSettingsCommand.NotifyCanExecuteChanged();

    /// <summary>草稿：存储后端（保存时才写回 Sync）</summary>
    [ObservableProperty]
    private SyncBackendType _selectedSyncBackendType;

    /// <summary>草稿：后台同步间隔（分钟，保存时才写回 Sync）</summary>
    [ObservableProperty]
    private int _selectedSyncIntervalMinutes;

    public IReadOnlyList<SyncBackendOption> SyncBackendOptions { get; } = new List<SyncBackendOption>
    {
        new("WebDAV", SyncBackendType.WebDav),
        new("Cloudflare R2 (S3 兼容)", SyncBackendType.S3)
    };

    public IReadOnlyList<SyncIntervalOption> SyncIntervalOptions { get; } = new List<SyncIntervalOption>
    {
        new("5 分钟", 5),
        new("10 分钟", 10),
        new("15 分钟（默认）", 15),
        new("30 分钟", 30),
        new("60 分钟", 60),
        new("120 分钟", 120)
    };

    public bool IsWebDavPanelVisible => SelectedSyncBackendType == SyncBackendType.WebDav;

    public bool IsS3PanelVisible => SelectedSyncBackendType == SyncBackendType.S3;

    public string SyncDeviceId => string.IsNullOrWhiteSpace(Sync.DeviceId) ? "（尚未生成）" : Sync.DeviceId;

    public SyncSettingsViewModel(SettingsService settingsService, SyncHost? syncHost = null)
    {
        _settingsService = settingsService;
        _syncHost = syncHost;

        // 草稿初始化为当前已保存值（抑制标脏，避免打开窗口就提示"未保存"）
        _suppressDraftDirty = true;
        try
        {
            var s = Sync;
            _syncEnabledDraft = s.Enabled;
            _syncEnableEncryptionDraft = s.EnableEncryption;
            _syncAllowInsecureHttpDraft = s.WebDavAllowInsecureHttp;
            SelectedSyncBackendType = s.BackendType;
            SelectedSyncIntervalMinutes = s.EffectiveIntervalMinutes;
            _syncWebDavUrlDraft = s.WebDavUrl;
            _syncWebDavUsernameDraft = s.WebDavUsername;
            _syncS3EndpointDraft = s.S3Endpoint;
            _syncS3BucketDraft = s.S3Bucket;
            _syncS3BasePrefixDraft = s.S3BasePrefix;
            _syncS3AccessKeyDraft = s.S3AccessKey;
        }
        finally
        {
            _suppressDraftDirty = false;
        }

        OnPropertyChanged(nameof(IsWebDavPanelVisible));
        OnPropertyChanged(nameof(IsS3PanelVisible));

        if (_syncHost != null)
        {
            _syncHost.StateChanged += RefreshSyncStatusOnDispatcher;
        }

        RefreshSyncStatus();
    }

    /// <summary>
    /// 解除对单例 <see cref="SyncHost"/>.StateChanged 的订阅（P2-7）。
    /// 本 VM 注册为 Transient 而 SyncHost 是单例：窗口关闭后若不摘除订阅，
    /// 单例会经事件委托长期持有已关闭的 VM → 窗口 → 整棵视觉树，反复开关窗口持续泄漏，
    /// 且每次状态变化都向全部历史实例派发刷新。由 SyncSettingsWindow.Closed 调用。
    /// </summary>
    public void DetachSyncHostEvents()
    {
        if (_syncHost != null)
        {
            _syncHost.StateChanged -= RefreshSyncStatusOnDispatcher;
        }
    }

    public bool SyncEnabled
    {
        get => _syncEnabledDraft;
        set
        {
            if (_syncEnabledDraft == value) return;
            _syncEnabledDraft = value;
            NotifyDraftChanged();
        }
    }

    /// <summary>草稿：是否启用端到端防偷窥加密（保存时才写回 Sync）</summary>
    public bool SyncEnableEncryption
    {
        get => _syncEnableEncryptionDraft;
        set
        {
            if (_syncEnableEncryptionDraft == value) return;
            _syncEnableEncryptionDraft = value;
            NotifyDraftChanged();
        }
    }

    /// <summary>显式允许明文 http://（内网 NAS；协议设计 §5.1）</summary>
    public bool SyncAllowInsecureHttp
    {
        get => _syncAllowInsecureHttpDraft;
        set
        {
            if (_syncAllowInsecureHttpDraft == value) return;
            _syncAllowInsecureHttpDraft = value;
            NotifyDraftChanged();
        }
    }

    public string SyncWebDavUrl
    {
        get => _syncWebDavUrlDraft;
        set
        {
            if (_syncWebDavUrlDraft == value) return;
            _syncWebDavUrlDraft = value ?? string.Empty;
            NotifyDraftChanged();
        }
    }

    public string SyncWebDavUsername
    {
        get => _syncWebDavUsernameDraft;
        set
        {
            if (_syncWebDavUsernameDraft == value) return;
            _syncWebDavUsernameDraft = value ?? string.Empty;
            NotifyDraftChanged();
        }
    }

    public string SyncWebDavCredentialHint
    {
        get
        {
            if (!string.IsNullOrEmpty(_webDavPasswordInput))
                return "凭据：已输入新密码（保存后生效）";
            return string.IsNullOrEmpty(Sync.WebDavPassword) ? "凭据：未配置" : "凭据：已保存（输入新值可替换）";
        }
    }

    public string SyncS3Endpoint
    {
        get => _syncS3EndpointDraft;
        set
        {
            if (_syncS3EndpointDraft == value) return;
            _syncS3EndpointDraft = value ?? string.Empty;
            NotifyDraftChanged();
        }
    }

    public string SyncS3Bucket
    {
        get => _syncS3BucketDraft;
        set
        {
            if (_syncS3BucketDraft == value) return;
            _syncS3BucketDraft = value ?? string.Empty;
            NotifyDraftChanged();
        }
    }

    public string SyncS3BasePrefix
    {
        get => _syncS3BasePrefixDraft;
        set
        {
            if (_syncS3BasePrefixDraft == value) return;
            _syncS3BasePrefixDraft = value ?? string.Empty;
            NotifyDraftChanged();
        }
    }

    public string SyncS3AccessKey
    {
        get => _syncS3AccessKeyDraft;
        set
        {
            if (_syncS3AccessKeyDraft == value) return;
            _syncS3AccessKeyDraft = value ?? string.Empty;
            NotifyDraftChanged();
        }
    }

    public string SyncS3CredentialHint
    {
        get
        {
            if (!string.IsNullOrEmpty(_s3SecretKeyInput))
                return "凭据：已输入新密钥（保存后生效）";
            return string.IsNullOrEmpty(Sync.S3SecretKey) ? "凭据：未配置" : "凭据：已保存（输入新值可替换）";
        }
    }

    /// <summary>是否有尚未落盘的编辑（决定「保存」按钮可用与提示文案）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveHintText))]
    [NotifyCanExecuteChangedFor(nameof(SaveSyncSettingsCommand))]
    private bool _hasUnsavedChanges;

    private string _saveHintText = "配置修改后需点「保存」才会写入配置文件";
    /// <summary>保存提示行（未保存时提醒，保存后回显已保存时刻与同步触发）</summary>
    public string SaveHintText
    {
        get => _saveHintText;
        private set => SetProperty(ref _saveHintText, value);
    }

    private string _syncStatusText = "同步未启用";
    /// <summary>同步状态行（最近成功时间 / 最近错误摘要）</summary>
    public string SyncStatusText
    {
        get => _syncStatusText;
        private set => SetProperty(ref _syncStatusText, value);
    }

    partial void OnSelectedSyncBackendTypeChanged(SyncBackendType value)
    {
        OnPropertyChanged(nameof(IsWebDavPanelVisible));
        OnPropertyChanged(nameof(IsS3PanelVisible));
        NotifyDraftChanged();
    }

    partial void OnSelectedSyncIntervalMinutesChanged(int value)
    {
        NotifyDraftChanged();
    }

    private SyncSettings Sync => _settingsService.Settings.Sync;

    /// <summary>草稿变更：仅标脏，不落盘</summary>
    private void NotifyDraftChanged()
    {
        if (_suppressDraftDirty) return;

        HasUnsavedChanges = true;
        SaveHintText = "⚠️ 配置已修改，点「保存」后将立即生效";
    }

    /// <summary>用草稿构造一份 SyncSettings（测试连接 / 校验用，不写回已保存配置）</summary>
    private SyncSettings BuildDraftSyncSettings()
    {
        var s = Sync;
        return new SyncSettings
        {
            Enabled = _syncEnabledDraft,
            EnableEncryption = _syncEnableEncryptionDraft,
            BackendType = SelectedSyncBackendType,
            DeviceId = s.DeviceId,
            IntervalMinutes = SelectedSyncIntervalMinutes,
            WebDavUrl = _syncWebDavUrlDraft,
            WebDavUsername = _syncWebDavUsernameDraft,
            WebDavAllowInsecureHttp = _syncAllowInsecureHttpDraft,
            WebDavPassword = _webDavPasswordInput.Length == 0 ? s.WebDavPassword : CredentialProtector.Protect(_webDavPasswordInput),
            S3Endpoint = _syncS3EndpointDraft,
            S3Bucket = _syncS3BucketDraft,
            S3BasePrefix = _syncS3BasePrefixDraft,
            S3AccessKey = _syncS3AccessKeyDraft,
            S3SecretKey = _s3SecretKeyInput.Length == 0 ? s.S3SecretKey : CredentialProtector.Protect(_s3SecretKeyInput)
        };
    }

    /// <summary>由窗口 code-behind 在 PasswordChanged 时推送（只更新草稿，不落盘）</summary>
    public void SetWebDavPasswordInput(string password)
    {
        _webDavPasswordInput = password ?? string.Empty;
        OnPropertyChanged(nameof(SyncWebDavCredentialHint));
        if (_webDavPasswordInput.Length == 0) return; // 清空输入框不代表清除已存凭据

        NotifyDraftChanged();
    }

    /// <summary>由窗口 code-behind 在 PasswordChanged 时推送（只更新草稿，不落盘）</summary>
    public void SetS3SecretKeyInput(string secretKey)
    {
        _s3SecretKeyInput = secretKey ?? string.Empty;
        OnPropertyChanged(nameof(SyncS3CredentialHint));
        if (_s3SecretKeyInput.Length == 0) return;

        NotifyDraftChanged();
    }

    private bool CanSaveSyncSettings() => HasUnsavedChanges && !IsSyncBusy;

    /// <summary>凭据已写入配置文件：窗口据此清空密码框，避免明文继续留在界面</summary>
    public event Action? CredentialsCommitted;

    /// <summary>把草稿写回已保存配置并落盘（唯一入口：测试连接不会触发保存）</summary>
    [RelayCommand(CanExecute = nameof(CanSaveSyncSettings))]
    private void SaveSyncSettings()
    {
        var s = Sync;
        var draft = BuildDraftSyncSettings();

        s.Enabled = draft.Enabled;
        s.EnableEncryption = draft.EnableEncryption;
        s.BackendType = draft.BackendType;
        s.IntervalMinutes = draft.IntervalMinutes;
        s.WebDavUrl = draft.WebDavUrl;
        s.WebDavUsername = draft.WebDavUsername;
        s.WebDavAllowInsecureHttp = draft.WebDavAllowInsecureHttp;
        s.WebDavPassword = draft.WebDavPassword;
        s.S3Endpoint = draft.S3Endpoint;
        s.S3Bucket = draft.S3Bucket;
        s.S3BasePrefix = draft.S3BasePrefix;
        s.S3AccessKey = draft.S3AccessKey;
        s.S3SecretKey = draft.S3SecretKey;

        // 凭据明文不回填输入框，避免密码在内存/界面里残留（窗口同步清空 PasswordBox）
        _webDavPasswordInput = string.Empty;
        _s3SecretKeyInput = string.Empty;

        _settingsService.SaveSettings();
        _syncHost?.ApplySettingsChanged();

        HasUnsavedChanges = false;
        SaveHintText = s.Enabled && s.IsConfigured
            ? $"已保存（{DateTime.Now:HH:mm:ss}），正在触发同步…"
            : $"已保存（{DateTime.Now:HH:mm:ss}）";
        CredentialsCommitted?.Invoke();
        OnPropertyChanged(nameof(SyncWebDavCredentialHint));
        OnPropertyChanged(nameof(SyncS3CredentialHint));

        RefreshSyncStatus();
        AppLog.Info($"[SyncSettings] 同步配置已保存（后端={s.BackendType}, 间隔={s.IntervalMinutes}, 启用={s.Enabled}, 加密={s.EnableEncryption}）");
    }

    [RelayCommand]
    private async Task TestSyncConnectionAsync()
    {
        if (IsSyncBusy) return;
        IsSyncBusy = true;
        try
        {
            // 仅用草稿探测连通性，不写回已保存配置
            await StorageBackendFactory.TestConnectionAsync(BuildDraftSyncSettings());
            MessageBox.Show(
                "连接成功，凭据有效。\n如需让本次修改正式生效，请点「保存」。",
                "测试连接", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"连接失败：{ex.Message}", "测试连接", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncNowManualAsync()
    {
        if (IsSyncBusy) return;
        if (_syncHost == null) return;

        if (HasUnsavedChanges)
        {
            MessageBox.Show(
                "当前有未保存的同步配置修改。\n请先点「保存」再同步。",
                "立即同步", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsSyncBusy = true;
        try
        {
            var summary = await _syncHost.SyncNowAsync("manual");
            if (summary != null)
            {
                MessageBox.Show(
                    $"同步完成。\n远端对象：{summary.Listed}\n下行应用：{summary.Downloaded}（守卫跳过 {summary.GuardedSkipped}）\n上传：{summary.Uploaded}",
                    "立即同步", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (!Sync.Enabled || !Sync.IsConfigured)
            {
                MessageBox.Show("请先启用同步并保存服务器信息。", "立即同步", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    /// <summary>窗口关闭时由 code-behind 询问：是否放弃未保存的修改</summary>
    public bool ConfirmDiscardDraft()
    {
        if (!HasUnsavedChanges) return true;

        var answer = MessageBox.Show(
            "同步配置有未保存的修改，关闭将丢弃这些修改）。\n确定要关闭吗？",
            "同步设置", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return answer == MessageBoxResult.OK;
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
        if (!Sync.Enabled)
        {
            SyncStatusText = HasUnsavedChanges
                ? "同步未启用（有未保存修改）"
                : "同步未启用";
            return;
        }

        if (_syncHost == null)
        {
            SyncStatusText = "同步服务未运行";
            return;
        }

        var state = _syncHost.CurrentState;
        var countsSummary = state.LastUploadedCount.HasValue || state.LastDownloadedCount.HasValue
            ? $"（上传 {state.LastUploadedCount ?? 0}，下载 {state.LastDownloadedCount ?? 0}）"
            : string.Empty;

        if (state.LastError != null)
        {
            var attempt = state.LastAttemptAt?.ToLocalTime().ToString("HH:mm:ss") ?? "--";
            var lastOk = state.LastSuccessAt != null
                ? state.LastSuccessAt.Value.ToLocalTime().ToString("MM-dd HH:mm") + countsSummary
                : "从未成功";
            var error = state.LastError.Length > 80 ? state.LastError[..80] + "…" : state.LastError;
            SyncStatusText = $"上次同步失败（{attempt}）：{error}；最近成功：{lastOk}";
        }
        else if (state.LastSuccessAt != null)
        {
            SyncStatusText = $"最近同步：{state.LastSuccessAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}{countsSummary}";
        }
        else
        {
            SyncStatusText = "已启用，尚未同步";
        }
    }
}
