using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CameraControl.Domain.Capture;

namespace CameraControl.Presentation.Services;

/// <summary>
/// 把 <see cref="FrameImage"/> 转成 WPF 可显示的位图。
///
/// 全部在内存中完成，**不产生任何临时文件**（符合隐私约束）。
/// 生成的位图会 Freeze，便于跨线程安全使用。
/// </summary>
public static class FrameToBitmap
{
    private const double Dpi = 96;

    /// <summary>转换；不支持的像素格式返回 null（调用方据此提示）。</summary>
    public static BitmapSource? Convert(FrameImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        System.Windows.Media.PixelFormat format = image.PixelFormat switch
        {
            "Bgr24" => System.Windows.Media.PixelFormats.Bgr24,
            "Rgb24" => System.Windows.Media.PixelFormats.Rgb24,
            "Bgra32" => System.Windows.Media.PixelFormats.Bgra32,
            "Gray8" => System.Windows.Media.PixelFormats.Gray8,
            _ => default
        };

        if (format == default) return null;

        try
        {
            var bitmap = BitmapSource.Create(
                image.Width,
                image.Height,
                Dpi,
                Dpi,
                format,
                null,
                image.Pixels.Span.ToArray(),
                image.Stride);

            bitmap.Freeze();
            return bitmap;
        }
        catch (ArgumentException)
        {
            // 尺寸/stride 与像素长度不匹配
            return null;
        }
    }

    /// <summary>把字节数格式化成易读形式。</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}

/// <summary>null → Visibility.Collapsed 的转换器（XAML 里判断有无图像）。</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null → Visible（用于"还没有图像时"的占位提示）。</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>空字符串 → Collapsed。</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string)
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
