using System;
using System.Globalization;
using System.Windows.Data;
using YouTubeDownloader.ViewModels;

namespace YouTubeDownloader.Converters;

/// <summary>
/// Enum値とbool値を相互変換するコンバーター
/// </summary>
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return false;

        var enumValue = value.ToString();
        var targetValue = parameter.ToString();

        return enumValue == targetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue && boolValue && parameter != null)
        {
            return Enum.Parse(typeof(NavigationItem), parameter.ToString()!);
        }
        return Binding.DoNothing;
    }
}
