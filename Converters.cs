using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AIWeather
{
    public class RunningToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isRunning)
            {
                return isRunning ? "⏹ Stop" : "▶ Start";
            }
            return "▶ Start";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class RunningToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isRunning)
            {
                return isRunning 
                    ? new SolidColorBrush(Color.FromRgb(163, 21, 21))  // Red when running
                    : new SolidColorBrush(Color.FromRgb(14, 99, 156)); // Blue when stopped
            }
            return new SolidColorBrush(Color.FromRgb(14, 99, 156));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
