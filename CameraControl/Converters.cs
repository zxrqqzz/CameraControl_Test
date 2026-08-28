using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CameraControl
{
    /// <summary>字符串相等判断: Source == Parameter ? true : false</summary>
    public class StringEqualsConverter : IValueConverter
    {
        public object Convert(object value, System.Type targetType, object parameter, CultureInfo culture) =>
            value?.ToString() == parameter?.ToString();

        public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture) =>
            value is bool b && b ? parameter?.ToString() ?? string.Empty : Binding.DoNothing;
    }

    /// <summary>布尔取反</summary>
    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, System.Type targetType, object parameter, CultureInfo culture) =>
            value is bool b ? !b : true;

        public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture) =>
            value is bool b ? !b : true;
    }
}
