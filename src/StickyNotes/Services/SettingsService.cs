using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Sync;

namespace StickyNotes.Services;

/// <summary>
/// 应用程序全局配置实体
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// 网络同步配置（协议设计见 docs/SYNC-PROTOCOL-DESIGN-20261004.md）
    /// </summary>
    public SyncSettings Sync { get; set; } = new();

    /// <summary>
    /// 便签贴纸正文字体大小（pt）
    /// </summary>
    public double EditorFontSize { get; set; } = 14.0;

    /// <summary>
    /// 关闭主窗口时最小化到系统托盘
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>
    /// 启用全局快捷键（Win+Alt+N, Win+Alt+H）
    /// </summary>
    public bool EnableGlobalHotKeys { get; set; } = true;

    /// <summary>
    /// 开机自启时最小化到托盘
    /// </summary>
    public bool StartMinimized { get; set; } = true;

    /// <summary>
    /// 是否启用 PIN 锁定（防偷窥轻量保护，实际有效性以 PinHash/PinSalt 存在为准）
    /// </summary>
    public bool PinEnabled { get; set; }

    /// <summary>
    /// PIN 哈希盐（base64，16 字节随机值），为空表示未设置过 PIN
    /// </summary>
    public string PinSalt { get; set; } = string.Empty;

    /// <summary>
    /// PIN 的 PBKDF2-SHA256 哈希（base64），不存明文
    /// </summary>
    public string PinHash { get; set; } = string.Empty;

    /// <summary>
    /// 仅在内存中标记：加载 settings.json 时发现 PIN 数据损坏而强制失效。
    /// 该字段不参与序列化（JsonIgnore），仅用于向用户显式告警，避免「以为受保护实则未受保护」。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PinDataCorrupted { get; set; }

    /// <summary>
    /// 无键鼠输入 N 分钟后自动锁定列表/归档窗口（0 表示不启用），窗口保持在原位不被隐藏。
    /// 取代原 <c>ListAutoCloseMinutes</c>：后者只是自动隐藏窗口，而隐藏会让下次打开必然重新锁定，
    /// 且完全不参与锁定判定，导致「设定时间没生效」的观感。
    /// 详见 docs/PIN-IDLE-LOCK-DESIGN-20261011.md
    /// </summary>
    public int IdleLockMinutes { get; set; } = 10;

    /// <summary>
    /// 进程冷启动（含托盘退出后重新双击 exe）时立即锁定，默认开启。
    /// 这是防「退出再重开绕过锁定」的唯一防线，关闭前应明确告知风险。
    /// </summary>
    public bool LockOnLaunch { get; set; } = true;
}

/// <summary>
/// 应用程序配置与设置服务
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public AppSettings Settings { get; private set; }

    public double EditorFontSize
    {
        get => Settings.EditorFontSize;
        set => SetEditorFontSize(value);
    }

    public bool MinimizeToTrayOnClose
    {
        get => Settings.MinimizeToTrayOnClose;
        set
        {
            if (Settings.MinimizeToTrayOnClose != value)
            {
                Settings.MinimizeToTrayOnClose = value;
                SaveSettings();
            }
        }
    }

    public bool EnableGlobalHotKeys
    {
        get => Settings.EnableGlobalHotKeys;
        set
        {
            if (Settings.EnableGlobalHotKeys != value)
            {
                Settings.EnableGlobalHotKeys = value;
                SaveSettings();
            }
        }
    }

    public bool StartMinimized
    {
        get => Settings.StartMinimized;
        set
        {
            if (Settings.StartMinimized != value)
            {
                Settings.StartMinimized = value;
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// 无键鼠输入多少分钟后自动锁定（0 = 不启用）
    /// </summary>
    public int IdleLockMinutes
    {
        get => Settings.IdleLockMinutes;
        set
        {
            var clamped = Math.Clamp(value, 0, 120);
            if (Settings.IdleLockMinutes != clamped)
            {
                Settings.IdleLockMinutes = clamped;
                SaveSettings();
            }
        }
    }

    /// <summary>进程冷启动时是否立即锁定</summary>
    public bool LockOnLaunch
    {
        get => Settings.LockOnLaunch;
        set
        {
            if (Settings.LockOnLaunch != value)
            {
                Settings.LockOnLaunch = value;
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// 以指定配置文件路径构造（测试入口）；为空时使用 <see cref="AppPaths.SettingsPath"/>。
    /// </summary>
    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? AppPaths.SettingsPath;
        Settings = LoadSettings(_settingsPath);
    }

    public void SetEditorFontSize(double newFontSize)
    {
        // 限制在安全有效字号区间 10 ~ 36 pt
        newFontSize = Math.Clamp(newFontSize, 10.0, 36.0);
        if (Math.Abs(Settings.EditorFontSize - newFontSize) > 0.1)
        {
            Settings.EditorFontSize = newFontSize;
            SaveSettings();
            WeakReferenceMessenger.Default.Send(new FontSizeChangedMessage(newFontSize));
        }
    }

    public void SaveSettings()
    {
        try
        {
            var targetPath = _settingsPath;
            var json = JsonSerializer.Serialize(Settings, JsonOptions);

            // 原子写入：先写临时文件并落盘，再用 File.Replace 整体替换目标文件。
            // 直接 File.WriteAllText 覆盖时，写到一半断电/崩溃会留下半截 JSON，
            // 而 LoadSettings 在解析失败时会静默回退默认值 —— 于是 PinSalt/PinHash 一起丢失，
            // 用户以为仍受 PIN 保护实则已完全失效（原 F-P1-6）。
            var tempPath = targetPath + ".tmp";
            File.WriteAllText(tempPath, json);

            if (File.Exists(targetPath))
            {
                // 保留上一版作为 .bak，便于 settings 损坏时人工恢复
                File.Replace(tempPath, targetPath, targetPath + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, targetPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"[SettingsService] 保存设置失败: {ex.Message}", ex);
        }
    }

    private static AppSettings LoadSettings(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    loaded.EditorFontSize = Math.Clamp(loaded.EditorFontSize, 10.0, 36.0);

                    // PIN 数据完整性校验：开关已打开但盐/哈希缺失或非合法 base64，
                    // 说明 settings.json 曾被损坏或被人工不完整编辑。
                    // 此时绝不能静默降级为「无 PIN」——必须显式告知用户，否则用户会在
                    // 毫无察觉的情况下失去防护（原 F-P1-6 的核心危害）。
                    if (loaded.PinEnabled)
                    {
                        if (string.IsNullOrEmpty(loaded.PinSalt) || string.IsNullOrEmpty(loaded.PinHash))
                        {
                            loaded.PinEnabled = false;
                            loaded.PinDataCorrupted = true;
                            AppLog.Error("[SettingsService] 检测到 settings.json 中 PinEnabled 为真但 PIN 盐/哈希缺失，PIN 锁定已失效并要求用户重新设置");
                        }
                        else if (!IsValidBase64(loaded.PinSalt) || !IsValidBase64(loaded.PinHash))
                        {
                            loaded.PinEnabled = false;
                            loaded.PinDataCorrupted = true;
                            AppLog.Error("[SettingsService] 检测到 settings.json 中 PIN 盐/哈希不是合法 base64，PIN 锁定已失效并要求用户重新设置");
                        }
                    }

                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            // 解析失败不再静默：明确记录错误并尝试用 .bak 兜底恢复
            AppLog.Error($"[SettingsService] 加载设置失败，将尝试回退备份: {ex.Message}", ex);

            var recovered = TryRecoverFromBackup(path);
            if (recovered != null)
            {
                AppLog.Warn("[SettingsService] 已从 settings.json.bak 成功恢复配置");
                return recovered;
            }
        }

        return new AppSettings();
    }

    /// <summary>
    /// settings.json 解析失败时，尝试从 .bak 备份恢复（写坏的主文件不应带走用户的全部设置）
    /// </summary>
    private static AppSettings? TryRecoverFromBackup(string path)
    {
        try
        {
            var backupPath = path + ".bak";
            if (!File.Exists(backupPath)) return null;

            var json = File.ReadAllText(backupPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json);
            if (loaded != null)
            {
                loaded.EditorFontSize = Math.Clamp(loaded.EditorFontSize, 10.0, 36.0);
                return loaded;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"[SettingsService] 从备份恢复设置同样失败: {ex.Message}", ex);
        }

        return null;
    }

    private static bool IsValidBase64(string value)
    {
        Span<byte> buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out _);
    }
}
