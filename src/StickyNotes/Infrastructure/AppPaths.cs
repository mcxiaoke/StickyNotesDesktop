using System.IO;
using StickyNotes.Services.Localization;

namespace StickyNotes.Infrastructure;

/// <summary>
/// 应用程序路径管理中心，集中管理数据库、备份与日志目录，支持测试环境隔离重定向
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// 环境变量名：设置后覆盖数据目录（便携部署 / 自动化测试 / CI 隔离用）
    /// </summary>
    public const string DataDirEnvVarName = "STICKYNOTES_DATA_DIR";

    private static string? _dataDirOverride;
    private static string? _baseDirectoryOverride;

    /// <summary>
    /// 程序可执行文件基准目录（支持单元测试重定向）
    /// </summary>
    public static string AppBaseDirectory
    {
        get => _baseDirectoryOverride ?? AppDomain.CurrentDomain.BaseDirectory;
        set => _baseDirectoryOverride = value;
    }

    /// <summary>
    /// 便携模式标记文件路径（exe 同级 portable.ini）
    /// </summary>
    public static string PortableFlagPath => Path.Combine(AppBaseDirectory, "portable.ini");

    /// <summary>
    /// 便携模式数据目录（exe 同级 app_data）
    /// </summary>
    public static string PortableDataDirectory => Path.Combine(AppBaseDirectory, "app_data");

    /// <summary>
    /// 标准漫游模式数据目录（%LOCALAPPDATA%\StickyNotes）
    /// </summary>
    public static string RoamingDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StickyNotes"
    );

    /// <summary>
    /// 是否处于便携模式（通过检测 exe 同级是否存在 portable.ini 决定）
    /// </summary>
    public static bool IsPortableMode => File.Exists(PortableFlagPath);

    /// <summary>
    /// 测试环境可通过此属性重定向数据目录，避免污染生产数据（优先级最高）
    /// </summary>
    public static string? DataDirOverride
    {
        get => _dataDirOverride;
        set => _dataDirOverride = value;
    }

    /// <summary>
    /// 当前生效的数据根目录：
    /// 优先级：DataDirOverride > STICKYNOTES_DATA_DIR 环境变量 > 便携模式 (app_data) > 标准漫游模式 (%LOCALAPPDATA%\StickyNotes)
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            var dir = ResolveDataDirectory();
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string ResolveDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_dataDirOverride))
        {
            return _dataDirOverride;
        }

        try
        {
            var envDir = Environment.GetEnvironmentVariable(DataDirEnvVarName);
            if (!string.IsNullOrWhiteSpace(envDir))
            {
                return envDir;
            }
        }
        catch (Exception ex)
        {
            // 注意：此处**不能**使用 AppLog —— AppLog 写日志需要 AppPaths.LogsDirectory，
            // 会回调 DataDirectory/ResolveDataDirectory 形成递归。改用 Debug 输出保留痕迹。
            System.Diagnostics.Debug.WriteLine($"[AppPaths] 读取数据目录环境变量失败，降级到默认路径决议: {ex.Message}");
        }

        return IsPortableMode ? PortableDataDirectory : RoamingDataDirectory;
    }

    /// <summary>
    /// 计算基于数据目录的确定性哈希（8位大写十六进制），确保单实例 Mutex 跨进程一致。
    /// 避开 .NET 8 string.GetHashCode() 默认启用的跨进程随机加盐机制 (Randomized String Hashing)。
    /// </summary>
    public static string GetDataDirectoryHash()
    {
        var normalized = (DataDirectory?.Trim().ToLowerInvariant() ?? "");
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hashBytes, 0, 4);
    }

    /// <summary>
    /// 当前数据目录对应的单实例 Mutex 名称
    /// </summary>
    public static string InstanceMutexName => $@"Local\StickyNotes_{GetDataDirectoryHash()}";

    /// <summary>
    /// 当前部署模式的友好描述文本
    /// </summary>
    public static string DeploymentModeDescription
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_dataDirOverride))
            {
                return Loc.T("Deployment.Override", "手动覆盖模式 (Override)");
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DataDirEnvVarName)))
                {
                    return Loc.T("Deployment.EnvVar", "环境变量模式 (STICKYNOTES_DATA_DIR)");
                }
            }
            catch (Exception ex)
            {
                // 同上：避免 AppPaths ↔ AppLog 递归，使用 Debug 输出
                System.Diagnostics.Debug.WriteLine($"[AppPaths] 判定数据目录来源时读取环境变量失败: {ex.Message}");
            }

            return IsPortableMode
                ? Loc.T("Deployment.Portable", "便携绿化模式 (app_data)")
                : Loc.T("Deployment.Roaming", "标准漫游模式 (%LOCALAPPDATA%)");
        }
    }

    /// <summary>
    /// SQLite 数据库文件绝对路径
    /// </summary>
    public static string DatabasePath => Path.Combine(DataDirectory, "notes.db");

    /// <summary>
    /// 主管理窗口位置与尺寸记忆配置文件路径
    /// </summary>
    public static string WindowConfigPath => Path.Combine(DataDirectory, "window.json");

    /// <summary>
    /// 应用程序设置配置文件路径
    /// </summary>
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>
    /// 网络同步运行时状态文件路径（最近成功时间/错误摘要，与用户设置分离）
    /// </summary>
    public static string SyncStatePath => Path.Combine(DataDirectory, "sync_state.json");

    /// <summary>
    /// 本地轮转备份目录
    /// </summary>
    public static string BackupsDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "backups");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// 日志目录
    /// </summary>
    public static string LogsDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
