using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace StickyNotes.Services.Localization;

/// <summary>
/// XAML 侧本地化标记扩展。用法：
/// <code>
/// xmlns:loc="clr-namespace:StickyNotes.Services.Localization"
///
/// &lt;Button Content="{loc:Loc Sync.Now, Default='立即同步'}"
///         ToolTip="{loc:Loc Sync.NowTooltip, Default='立即与云端同步一次'}" /&gt;
/// </code>
/// </summary>
/// <remarks>
/// 之所以不用 <c>{DynamicResource}</c> 或 ResourceDictionary：
///   * <c>{DynamicResource}</c> 需要先把字符串挂进 App 级 ResourceDictionary，
///     翻译者改文案要动 XAML 资源字典，且键拼错时静默无异常。
///   * 本扩展的键是「点号路径 + 内联兜底原文」，翻译者只看 JSON 就能补全语言，
///     中文原文留在调用点便于 review 与 diff。
///
/// 热刷新机制：返回 <c>Binding{ Source = I18nService.Instance, Path = "[Key]" }</c>，
/// 语言切换时 <see cref="I18nService"/> 发 <c>PropertyChanged("Item[]")</c>，所有绑定自动重算。
/// </remarks>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    /// <summary>语言包键（点号路径）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>缺键时的兜底文案，通常就是原始中文。</summary>
    public string Default { get; set; } = string.Empty;

    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key))
        {
            return Default ?? string.Empty;
        }

        // TargetObject 是 DependencyObject 时才可能建立绑定；
        // 在 Style Setter / 设计器求值等场景下拿不到，改为一次性静态取值（不参与热刷新）。
        var target = serviceProvider?.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;

        if (target?.TargetObject is DependencyObject)
        {
            var binding = new Binding
            {
                Source = I18nService.Instance,
                Path = new PropertyPath($"[{Key}]"),
                Mode = BindingMode.OneWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                // 键缺失时 I18nService.Get 已保证非 null，这里只是防御性兜底
                FallbackValue = string.IsNullOrEmpty(Default) ? Key : Default,
                TargetNullValue = string.IsNullOrEmpty(Default) ? Key : Default
            };
            return binding.ProvideValue(serviceProvider);
        }

        return I18nService.Instance.Get(Key, Default);
    }
}