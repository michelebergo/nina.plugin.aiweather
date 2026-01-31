using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AIWeather.Models;

namespace AIWeather.Converters
{
    /// <summary>
    /// Converts CaptureMode enum to Visibility based on parameter
    /// </summary>
    public class EnumToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
                return Visibility.Collapsed;

            try
            {
                // Parse the enum value
                if (value is CaptureMode mode)
                {
                    // Parse the parameter as integer
                    if (int.TryParse(parameter.ToString(), out int targetMode))
                    {
                        return ((int)mode == targetMode) ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
            catch
            {
                // Fall through to default
            }

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
