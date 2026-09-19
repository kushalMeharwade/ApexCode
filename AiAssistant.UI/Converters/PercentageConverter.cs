using System;
using System.Globalization;
using System.Windows.Data;

namespace AiAssistant.UI.Converters
{
    public class PercentageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double totalWidth && double.TryParse(parameter?.ToString(), out double percentage))
            {
                return totalWidth * percentage;
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
