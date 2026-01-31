using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
    
    public class BoolToGridLengthConverter : IValueConverter
    {
        public string TrueValue { get; set; } = "100";
        public string FalseValue { get; set; } = "0";
        
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                var widthStr = boolValue ? TrueValue : FalseValue;
                if (double.TryParse(widthStr, out var width))
                {
                    return new DataGridLength(width);
                }
            }
            return new DataGridLength(0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class FirstNonNullConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values != null)
            {
                foreach (var value in values)
                {
                    if (value != null && value != DependencyProperty.UnsetValue)
                    {
                        return value;
                    }
                }
            }
            return null;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
