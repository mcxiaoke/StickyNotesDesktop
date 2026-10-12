using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Services.Localization;

namespace StickyNotes.Tests;

/// <summary>
/// 本地化契约测试（P5 质量守卫）。
///
/// 保护四件事：
///   1) 各语言包的键集合完全一致（缺 key 时会静默回退到源语言，肉眼极难发现）；
///   2) 同一 key 在各语言中的格式化占位符一致（否则某一语言会显示字面量 {0}）；
///   3) 代码里引用的每个键都真实存在于语言包中 —— **同时扫描 *.cs 与 *.xaml**
///      （CarroDesk 参考实现只扫 .cs，其 384 处 XAML 调用完全无校验）；
///   4) XAML 中不得出现未走 {loc:Loc} 的裸中文字面量（防止新增界面时回退到硬编码）。
///
/// 直接读取源码树中的语言包，不为测试在 I18nService 上开放诊断接口。
/// </summary>
[TestClass]
public class LocalizationContractTests
{
    /// <summary>C# 侧：Loc.T("...") / Loc.Format("...")</summary>
    private static readonly Regex CsKeyPattern =
        new(@"Loc\.(?:T|Format)\s*\(\s*""([A-Za-z0-9_.]+)""", RegexOptions.Compiled);

    /// <summary>XAML 侧：{loc:Loc Key, Default='...'} —— 本项目特有，CarroDesk 未覆盖</summary>
    private static readonly Regex XamlKeyPattern =
        new(@"\{loc:Loc\s+([A-Za-z0-9_.]+)", RegexOptions.Compiled);

    /// <summary>XAML 中未本地化的裸中文属性值</summary>
    private static readonly Regex HardcodedXamlPattern = new(
        @"(?<=(?:Content|Header|ToolTip|Title|Text|PlaceholderText|AutomationProperties\.Name)="")" +
        @"(?!\{loc:Loc|\{Binding|\{StaticResource|\{\x27?\x27?\}?\s*$)[^""]*[\u4e00-\u9fff][^""]*",
        RegexOptions.Compiled);

    private static readonly Regex PlaceholderPattern = new(@"\{(\d+)\}", RegexOptions.Compiled);

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }

        Assert.IsNotNull(dir, "未能定位仓库根目录（向上找不到 Directory.Build.props）");

        string src = Path.Combine(dir!.FullName, "src", "StickyNotes");
        Assert.IsTrue(Directory.Exists(src), "未找到项目目录: " + src);
        return src;
    }

    /// <summary>展开语言包为「点号键 → 文案」字典（复刻 I18nService.FlattenJson 的语义）</summary>
    private static Dictionary<string, string> LoadLocale(string code)
    {
        string path = Path.Combine(FindSourceRoot(), "Resources", "Locales", code + ".json");
        Assert.IsTrue(File.Exists(path), "语言包不存在: " + path);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Flatten(doc.RootElement, string.Empty, result);
        return result;
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> target)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("_meta"))
            {
                continue;
            }

            string key = string.IsNullOrEmpty(prefix) ? property.Name : prefix + "." + property.Name;
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                Flatten(property.Value, key, target);
            }
            else if (property.Value.ValueKind != JsonValueKind.Null)
            {
                target[key] = property.Value.GetString() ?? string.Empty;
            }
        }
    }

    /// <summary>源码目录下排除 bin/obj 的 *.cs / *.xaml</summary>
    private static IEnumerable<string> SourceFiles(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"));

    [TestMethod]
    public void Locales_AllLanguages_DefineExactlyTheSameKeySet()
    {
        var localesDir = Path.Combine(FindSourceRoot(), "Resources", "Locales");
        var codes = Directory.GetFiles(localesDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();

        Assert.IsTrue(codes.Count >= 2,
            $"至少应有源语言与一份译文，实际只有 {codes.Count} 个：{string.Join(", ", codes)}");

        var source = LoadLocale(I18nService.SourceLanguage);
        Assert.IsTrue(source.Count > 0, "源语言包为空，检查 Resources/Locales/zh-CN.json");

        foreach (var code in codes)
        {
            var locale = LoadLocale(code!);

            var onlySource = source.Keys.Except(locale.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k).ToList();
            var onlyLocale = locale.Keys.Except(source.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k).ToList();

            Assert.AreEqual(0, onlySource.Count,
                $"[{code}] 相比 {I18nService.SourceLanguage} 缺失的键:\n" + string.Join("\n", onlySource));
            Assert.AreEqual(0, onlyLocale.Count,
                $"[{code}] 相比 {I18nService.SourceLanguage} 多出的键:\n" + string.Join("\n", onlyLocale));
        }
    }

    [TestMethod]
    public void Locales_Placeholders_MatchAcrossLanguages()
    {
        var localesDir = Path.Combine(FindSourceRoot(), "Resources", "Locales");
        var source = LoadLocale(I18nService.SourceLanguage);

        foreach (var path in Directory.GetFiles(localesDir, "*.json"))
        {
            var code = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(code, I18nService.SourceLanguage, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var locale = LoadLocale(code!);
            var mismatches = new List<string>();

            foreach (var key in source.Keys)
            {
                if (!locale.TryGetValue(key, out var otherValue))
                {
                    continue;
                }

                string sourceSlots = string.Join(",", PlaceholderPattern.Matches(source[key])
                    .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().OrderBy(v => v));
                string otherSlots = string.Join(",", PlaceholderPattern.Matches(otherValue)
                    .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().OrderBy(v => v));

                if (!string.Equals(sourceSlots, otherSlots, StringComparison.Ordinal))
                {
                    mismatches.Add($"{key}: {I18nService.SourceLanguage}=[{sourceSlots}] {code}=[{otherSlots}]");
                }
            }

            Assert.AreEqual(0, mismatches.Count,
                $"[{code}] 占位符不一致（该语言将显示字面量 {{0}}）:\n" + string.Join("\n", mismatches));
        }
    }

    /// <summary>
    /// 扫描 C# 与 XAML 的全部键引用，校验其在语言包中真实存在。
    /// XAML 部分是本项目补齐 CarroDesk 缺口的关键项。
    /// </summary>
    [TestMethod]
    public void Locales_EveryReferencedKey_IsDefined()
    {
        string root = FindSourceRoot();
        var locale = LoadLocale(I18nService.SourceLanguage);

        var csRefs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in SourceFiles(root, "*.cs"))
        {
            foreach (Match m in CsKeyPattern.Matches(File.ReadAllText(file)))
            {
                csRefs.Add(m.Groups[1].Value);
            }
        }

        var xamlRefs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in SourceFiles(root, "*.xaml"))
        {
            foreach (Match m in XamlKeyPattern.Matches(File.ReadAllText(file)))
            {
                xamlRefs.Add(m.Groups[1].Value);
            }
        }

        Assert.IsTrue(csRefs.Count > 0, "未扫描到任何 Loc.T/Loc.Format 调用，正则或目录结构可能已变化");
        Assert.IsTrue(xamlRefs.Count > 0, "未扫描到任何 {loc:Loc ...} 引用，正则或目录结构可能已变化");

        var missingCs = csRefs.Where(k => !locale.ContainsKey(k)).ToList();
        var missingXaml = xamlRefs.Where(k => !locale.ContainsKey(k)).ToList();

        Assert.AreEqual(0, missingCs.Count,
            "C# 引用了但语言包缺失的键（会静默回退到调用点硬编码兜底）:\n" + string.Join("\n", missingCs));
        Assert.AreEqual(0, missingXaml.Count,
            "XAML 引用了但语言包缺失的键（界面将显示键名或兜底原文）:\n" + string.Join("\n", missingXaml));
    }

    /// <summary>
    /// 防回退守卫：XAML 中不允许出现未走 {loc:Loc} 的裸中文字面量。
    /// CarroDesk 因缺此守卫，实测残留 129 条硬编码中文而无人察觉。
    /// </summary>
    [TestMethod]
    public void Xaml_NoHardcodedChineseLiterals()
    {
        string root = FindSourceRoot();
        var violations = new List<string>();

        foreach (var file in SourceFiles(root, "*.xaml"))
        {
            string text = File.ReadAllText(file);
            int line = 0;
            foreach (var raw in text.Split('\n'))
            {
                line++;
                foreach (Match m in HardcodedXamlPattern.Matches(raw))
                {
                    violations.Add($"{Path.GetFileName(file)}:{line}: {m.Value.Trim()}");
                }
            }
        }

        Assert.AreEqual(0, violations.Count,
            "XAML 存在未本地化的裸中文字面量，请改用 {loc:Loc Key, Default='原文'}:\n"
            + string.Join("\n", violations));
    }

    /// <summary>语言包必须能被 I18nService 正常加载与解析（冒烟）</summary>
    [TestMethod]
    public void I18nService_LoadsLocales_AndResolvesKeys()
    {
        var service = I18nService.Instance;

        Assert.IsTrue(service.AvailableLanguages.Count >= 2,
            $"已加载语言数应 >= 2，实际 {service.AvailableLanguages.Count}："
            + string.Join(", ", service.AvailableLanguages.Keys));

        // 已知键必须取得到非空文案
        Assert.AreEqual("立即同步", service.Get("Sync.Now"));

        // 未知键：回退到键名本身，绝不返回 null 或空串
        Assert.AreEqual("This.Key.Does.Not.Exist", service.Get("This.Key.Does.Not.Exist"));
        Assert.AreEqual("兜底文案", service.Get("This.Key.Does.Not.Exist", "兜底文案"));

        // 切到英文后，源语言键应回落到英文文案而不是键名
        service.ApplyLanguage("en-US");
        try
        {
            Assert.AreEqual("Sync now", service.Get("Sync.Now"));
            Assert.AreEqual("en-US", service.CurrentLanguage);
        }
        finally
        {
            service.ApplyLanguage(I18nService.SourceLanguage);
        }
    }
}