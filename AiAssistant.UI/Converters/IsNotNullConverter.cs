using System;
using System.Globalization;
using System.Windows.Data;

namespace AiAssistant.UI.Converters;

/// <summary>
/// Converter that returns true if the value is not null, false otherwise.
/// Used for defensive null-checking in XAML MultiDataTriggers to prevent
/// binding to null values that would cause raw markdown to display.
/// </summary>
public class IsNotNullConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("IsNotNullConverter does not support two-way binding.");
    }
}
