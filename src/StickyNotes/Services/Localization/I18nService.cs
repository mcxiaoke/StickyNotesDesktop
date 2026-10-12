using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using StickyNotes.Infrastructure;

namespace StickyNotes.Services.Localization;

/// <summary>
/// 语言包服务：加载 JSON 语言包、按当前语言解析文案、并向 WPF 绑定与代码侧广播切换事件。
///
/// 设计要点（相对 CarroDesk 参考实现的四处修正）：
///   1. JSON 解析失败一律写 AppLog，不再 <c>catch { }</c> 静默吞掉（静默会让整站退化成兜底文案且无迹可寻）。
///   2. 回退链为「当前语言 → zh-CN（源语言）→ 调用方兜底值 → 键名」，
///      而非 CarroDesk 的「当前 → 另一种语言」，后者在三种语言以上会中英日混排。
///   3. 语言包以 <see cref="EmbeddedResource"/> 进主程序集，规避卫星程序集与 Costura 的冲突
///      （Costura 在 MSBuild 生成卫星程序集之前运行，无法嵌入 *.resources.dll）。
///   4. 外部语言包目录为「数据目录/lang」，便携模式下随 exe 内聚，发版后可补语言而无需重编译。
/// </summary>
public sealed class I18nService : INotifyPropertyChanged
{
    /// <summary>源语言（回退终点）。所有语言包最终以此为兜底，保证绝不出现空白界面。</summary>
    public const string SourceLanguage = "zh-CN";

    /// <summary>「跟随系统」在配置中的取值。</summary>
    public const string AutoLanguage = "auto";

    private static I18nService? _instance;

    /// <summary>
    /// 全局实例。XAML 标记扩展与静态门面 <see cref="Loc"/> 需要一个不依赖 DI 的稳定引用，
    /// 因为 XAML 解析早于任何 ViewModel 构造。DI 容器注册的仍是同一个对象（见 <see cref="EnsureInstance"/>）。
    /// </summary>
    public static I18nService Instance => EnsureInstance();

    private readonly Dictionary<string, Dictionary<string, string>> _locales
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _languageDisplayNames
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _loadErrors = new();

    private string _currentLanguage = SourceLanguage;
    private CultureInfo _currentCulture = CultureInfo.InvariantCulture;

    /// <summary>语言切换事件。供代码构建的 UI（托盘菜单等）显式刷新——它们不走 XAML 绑定。</summary>
    public event Action? LanguageChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>当前生效的语言代码（如 zh-CN / en-US）。</summary>
    public string CurrentLanguage => _currentLanguage;

    /// <summary>当前生效的区域信息，供 <see cref="Format"/> 做数字/日期本地化。</summary>
    public CultureInfo CurrentCulture => _currentCulture;

    /// <summary>已加载语言代码 → 显示名，供设置页下拉框枚举。</summary>
    public IReadOnlyDictionary<string, string> AvailableLanguages => _languageDisplayNames;

    /// <summary>索引器：供 XAML 绑定 <c>Path="[Key]"</c> 使用。</summary>
    public string this[string key] => Get(key);

    private I18nService()
    {
        LoadEmbeddedLocales();
        LoadExternalLocales();
        _currentCulture = SafeCreateCulture(SourceLanguage) ?? CultureInfo.InvariantCulture;
    }

    /// <summary>
    /// 供 DI 注册使用，确保容器与静态门面拿到同一个实例。
    /// </summary>
    public static I18nService EnsureInstance() => _instance ??= new I18nService();

    /// <summary>
    /// 应用启动时按配置应用语言。必须在任何窗口创建之前调用。
    /// </summary>
    public void Init(string configuredLanguage) => ApplyLanguage(configuredLanguage);

    /// <summary>
    /// 切换语言并广播刷新。<paramref name="configuredLanguage"/> 可为 <see cref="AutoLanguage"/>。
    /// </summary>
    public void ApplyLanguage(string? configuredLanguage)
    {
        var target = ResolveLanguageCode(configuredLanguage);

        // 语言未变化时直接返回，避免设置页回写触发无谓的全量绑定重算
        if (string.Equals(_currentLanguage, target, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentLanguage = target;
        _currentCulture = SafeCreateCulture(target) ?? CultureInfo.InvariantCulture;

        AppLog.Info($"[I18nService] 界面语言切换为 {target}");

        // "Item[]" 是 WPF 索引器绑定的标准刷新约定：触发所有 {loc:Loc Key} 重新取值
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// 把配置值（auto / 具体语言代码 / 区域代码）解析为本应用实际拥有的语言代码。
    /// </summary>
    public string ResolveLanguageCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            string.Equals(code, AutoLanguage, StringComparison.OrdinalIgnoreCase))
        {
            var sys = CultureInfo.CurrentUICulture.Name;
            return MatchLanguage(sys) ?? SourceLanguage;
        }

        if (_locales.ContainsKey(code))
        {
            return code;
        }

        // 区域码回退到语言前缀：zh-TW → zh-CN，en-GB → en-US
        return MatchLanguage(code) ?? SourceLanguage;
    }

    /// <summary>按语言前缀（zh* / en* …）在已加载语言包中寻找匹配项。</summary>
    private string? MatchLanguage(string? code)
    {
        if (string.IsNullOrEmpty(code) || _locales.Count == 0)
        {
            return null;
        }

        if (_locales.ContainsKey(code))
        {
            return code;
        }

        var prefix = code.Split('-')[0];
        foreach (var candidate in _locales.Keys)
        {
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 取文案。回退链：当前语言 → <see cref="SourceLanguage"/> → <paramref name="defaultValue"/> → 键名。
    /// 永不死循环、永不返回 null，保证 UI 永远有可显示文本。
    /// </summary>
    public string Get(string? key, string? defaultValue = null)
    {
        if (string.IsNullOrEmpty(key))
        {
            return defaultValue ?? string.Empty;
        }

        if (_locales.TryGetValue(_currentLanguage, out var current) && current.TryGetValue(key, out var value))
        {
            return value;
        }

        if (!string.Equals(_currentLanguage, SourceLanguage, StringComparison.OrdinalIgnoreCase)
            && _locales.TryGetValue(SourceLanguage, out var source)
            && source.TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        return defaultValue ?? key;
    }

    /// <summary>取文案并按当前区域做 <see cref="string.Format(IFormatProvider, string, object[])"/>。</summary>
    public string Format(string key, params object[] args)
    {
        var pattern = Get(key);
        if (args is null || args.Length == 0)
        {
            return pattern;
        }

        try
        {
            return string.Format(_currentCulture, pattern, args);
        }
        catch (FormatException ex)
        {
            // 占位符与实参不匹配是语言包缺陷，必须留痕而不是静默吞掉
            AppLog.Error($"[I18nService] 文案 \"{key}\" 格式化失败（占位符与参数个数不匹配）: {ex.Message}", ex);
            return pattern;
        }
    }

    /// <summary>取文案（带兜底值）并格式化。</summary>
    public string FormatWithDefault(string key, string defaultValue, params object[] args)
    {
        var pattern = Get(key, defaultValue);
        if (args is null || args.Length == 0)
        {
            return pattern;
        }

        try
        {
            return string.Format(_currentCulture, pattern, args);
        }
        catch (FormatException ex)
        {
            AppLog.Error($"[I18nService] 文案 \"{key}\" 格式化失败: {ex.Message}", ex);
            return pattern;
        }
    }

    /// <summary>
    /// 加载嵌入在主程序集中的语言包（Resources/Locales/*.json）。
    /// 嵌入而非松散文件的原因见类注释第 3 点。
    /// </summary>
    private void LoadEmbeddedLocales()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                    !name.Contains("Locales", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using var stream = assembly.GetManifestResourceStream(name);
                    if (stream is null)
                    {
                        continue;
                    }

                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    ParseAndRegisterLocale(reader.ReadToEnd(), name);
                }
                catch (Exception ex)
                {
                    RecordLoadError(name, ex);
                }
            }
        }
        catch (Exception ex)
        {
            // 清单资源枚举本身失败属于极端情况，仍需留痕
            RecordLoadError("embedded-locales", ex);
        }
    }

    /// <summary>
    /// 加载外部语言包（数据目录/lang/*.json），同语言代码会覆盖内置版本。
    /// 这样发版后用户/翻译者可自行放入新语言，无需等待官方重新打包。
    /// </summary>
    private void LoadExternalLocales()
    {
        string langDir;
        try
        {
            langDir = Path.Combine(AppPaths.DataDirectory, "lang");
        }
        catch (Exception ex)
        {
            RecordLoadError("resolve-lang-dir", ex);
            return;
        }

        if (!Directory.Exists(langDir))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.GetFiles(langDir, "*.json"))
            {
                try
                {
                    ParseAndRegisterLocale(File.ReadAllText(file, Encoding.UTF8), file);
                }
                catch (Exception ex)
                {
                    RecordLoadError(file, ex);
                }
            }
        }
        catch (Exception ex)
        {
            RecordLoadError(langDir, ex);
        }
    }

    private void ParseAndRegisterLocale(string json, string source)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                RecordLoadError(source, new InvalidDataException("语言包根节点必须是 JSON 对象"));
                return;
            }

            var root = doc.RootElement;

            // _meta 声明语言代码与显示名；缺失 _meta.code 的文件直接判为无效语言包
            if (!root.TryGetProperty("_meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
            {
                RecordLoadError(source, new InvalidDataException("语言包缺少 _meta 节"));
                return;
            }

            var code = meta.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(code))
            {
                RecordLoadError(source, new InvalidDataException("语言包 _meta.code 为空"));
                return;
            }

            code = code.Trim();

            if (!_locales.TryGetValue(code, out var dict))
            {
                dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _locales[code] = dict;
            }

            var displayName = meta.TryGetProperty("name", out var nameEl) && nameEl.GetString() is { Length: > 0 } n
                ? n
                : code;
            _languageDisplayNames[code] = displayName;

            FlattenJson(root, string.Empty, dict);
        }
        catch (JsonException ex)
        {
            RecordLoadError(source, ex);
        }
    }

    /// <summary>递归展开嵌套 JSON 为点号分隔键（Common.Cancel → "Common"→"Cancel"）。</summary>
    private static void FlattenJson(JsonElement element, string prefix, Dictionary<string, string> target)
    {
        foreach (var property in element.EnumerateObject())
        {
            // _meta 是元数据，不作为文案键
            if (property.NameEquals("_meta"))
            {
                continue;
            }

            var fullKey = string.IsNullOrEmpty(prefix) ? property.Name : prefix + "." + property.Name;

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                FlattenJson(property.Value, fullKey, target);
            }
            else if (property.Value.ValueKind != JsonValueKind.Null)
            {
                target[fullKey] = property.Value.GetString() ?? string.Empty;
            }
        }
    }

    private static CultureInfo? SafeCreateCulture(string code)
    {
        try
        {
            return new CultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private void RecordLoadError(string source, Exception ex)
    {
        var message = $"[I18nService] 语言包加载失败 ({source}): {ex.Message}";
        _loadErrors.Add(message);
        AppLog.Error(message, ex);
    }
}