using System.Globalization;
using System.Windows.Data;
using StickyNotes.Services.Localization;

namespace StickyNotes.Converters;

/// <summary>
/// 将 UTC DateTime 转换为符合用户习惯的本地化友好时间描述
/// </summary>
public sealed class FriendlyDateTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dt)
        {
            var local = dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
            var now = DateTime.Now;
            var diff = now - local;

            if (diff.TotalMinutes < 1)
                return Loc.T("Time.JustNow", "刚刚");
            if (diff.TotalMinutes < 60)
                return Loc.T("Time.MinutesAgo", "{0} 分钟前", (int)diff.TotalMinutes);
            if (diff.TotalHours < 24 && now.Date == local.Date)
                return Loc.T("Time.Today", "今天 {0}", local.ToString("HH:mm", culture));
            if (now.Date.AddDays(-1) == local.Date)
                return Loc.T("Time.Yesterday", "昨天 {0}", local.ToString("HH:mm", culture));
            if (now.Year == local.Year)
                return local.ToString("MM-dd HH:mm", culture);

            return local.ToString("yyyy-MM-dd", culture);
        }

        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
