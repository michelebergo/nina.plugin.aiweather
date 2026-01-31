using System;
using System.Globalization;
using System.Windows.Data;
using AIWeather.Models;

namespace AIWeather.Converters
{
    /// <summary>
    /// Converts between CaptureMode enum and integer for ComboBox binding
    /// </summary>
    public class EnumToIntConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is CaptureMode mode)
            {
                return (int)mode;
            }
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intValue)
            {
                return (CaptureMode)intValue;
            }
            return CaptureMode.RTSPStream;
        }
    }
}
