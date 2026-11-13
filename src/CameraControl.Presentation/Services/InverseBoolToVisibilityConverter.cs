using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CameraControl.Presentation.Services;

/// <summary>
/// bool 取反后转 Visibility（true → Collapsed，false → Visible）。
///
/// 用在"运行中换按钮"这类场景：发送键在 IsAiRunning=false 时显示，
/// 停止键在 true 时显示，两个键占同一个位置。
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Visibility → bool 没有语义。");
}
