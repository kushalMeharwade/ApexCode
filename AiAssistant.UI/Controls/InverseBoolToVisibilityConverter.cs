using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AiAssistant.UI.Controls;

/// <summary>
/// Converts a boolean to Visibility: Visible if false, Collapsed if true (inverse of BooleanToVisibilityConverter).
/// Used for showing static status text when running state is false.
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
