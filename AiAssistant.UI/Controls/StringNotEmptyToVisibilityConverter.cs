using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AiAssistant.UI.Controls;

/// <summary>
/// Converts a string to Visibility: Visible if non-empty, Collapsed if null or empty.
/// Used for error message display in ChatPanel.
/// </summary>
public class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
