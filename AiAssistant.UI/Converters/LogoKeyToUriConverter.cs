using System;
using System.Globalization;
using System.Windows.Data;

namespace AiAssistant.UI.Converters;

public class LogoKeyToUriConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var logoResourceKey = value as string;
        return ProviderLogoResolver.Resolve(logoResourceKey);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
