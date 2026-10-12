namespace StickyNotes.Services.Localization;

/// <summary>
/// 本地化静态门面。C# 侧读取文案的唯一入口。
/// </summary>
/// <remarks>
/// 惯用写法：
/// <code>
/// ShowInfoBar(Loc.T("Sync.ErrorNotInitialized", "同步功能尚未初始化"), severity);
/// var itemExit = new MenuItem { Header = Loc.T("Tray.Exit", "退出") };
/// ShowInfoBar(Loc.T("Sync.ErrorFailed", "同步失败：{0}", ex.Message), severity);
/// </code>
/// 第二个参数（兜底原文）不是可选装饰：它让中文原文留在调用点，既便于 review 与 diff，
/// 也在语言包意外缺键时保证界面仍有正确的中文显示。
/// </remarks>
public static class Loc
{
    /// <summary>取文案，无兜底值（缺键时回退到源语言或键名）。</summary>
    public static string T(string key) => I18nService.Instance.Get(key);

    /// <summary>取文案，缺键时使用 <paramref name="defaultValue"/>。</summary>
    public static string T(string key, string defaultValue) => I18nService.Instance.Get(key, defaultValue);

    /// <summary>取文案并按 <paramref name="defaultValue"/> 的占位符格式化。</summary>
    public static string T(string key, string defaultValue, params object[] args)
        => I18nService.Instance.FormatWithDefault(key, defaultValue, args);

    /// <summary>取文案并按语言包中的占位符格式化。</summary>
    public static string Format(string key, params object[] args) => I18nService.Instance.Format(key, args);

    /// <summary>当前生效语言代码。</summary>
    public static string CurrentLanguage => I18nService.Instance.CurrentLanguage;
}