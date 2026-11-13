using System.Globalization;
using System.Reflection;
using System.Windows.Data;

namespace CameraControl.Presentation.Services;

/// <summary>
/// ComboBox 选中项 → 显示文字（配合自定义 ComboBox 模板）。
///
/// 为什么需要：自定义模板里 ContentPresenter 直接绑 <c>SelectionBoxItem</c>，
/// 选中项不是字符串时只会渲染 ToString()——于是下拉项显示的是名字，
/// 收起状态却显示 "CameraControl.Presentation.ViewModels.CameraRowViewModel"。
/// 系统默认模板替我们做了 DisplayMemberPath 到选中项的映射，自定义模板要自己补。
///
/// 输入：[0] 选中项，[1] DisplayMemberPath。
/// 没设 DisplayMemberPath（或值是字符串）时原样返回，不影响其它用法。
/// </summary>
public sealed class DisplayMemberConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2)
            return values.Length > 0 ? values[0] : null;

        object? item = values[0];

        if (item is null)
            return string.Empty;

        if (item is string || values[1] is not string path || string.IsNullOrWhiteSpace(path))
            return item;

        PropertyInfo? property = item.GetType().GetProperty(path, BindingFlags.Public | BindingFlags.Instance);

        return property?.GetValue(item) ?? item;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("选中项显示文字是只读的。");
}
