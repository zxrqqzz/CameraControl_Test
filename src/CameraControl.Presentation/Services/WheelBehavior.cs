using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CameraControl.Presentation.Services;

/// <summary>
/// 让"内嵌的可滚动控件"不再吞掉滚轮事件。
///
/// 问题：聊天里的 Markdown 用 FlowDocumentScrollViewer 显示，鼠标一进去滚轮就只滚它，
/// 外层对话流纹丝不动——用户体感是"鼠标进入列表就滚不动页面了"。
/// 实际上下方向我们不需要它自己滚（高度由内容撑开），只有横向可能需要（宽表格/长代码）。
///
/// 用法：<c>beh:WheelBehavior.PassThrough="True"</c>
/// 行为：
///   · Shift + 滚轮 → 横向滚（滚内层，如果它确实有横向内容）
///   · 其余情况 → 把滚轮转发给外层 ScrollViewer（整页/对话流），并标记已处理
/// </summary>
public static class WheelBehavior
{
    public static readonly DependencyProperty PassThroughProperty = DependencyProperty.RegisterAttached(
        "PassThrough",
        typeof(bool),
        typeof(WheelBehavior),
        new PropertyMetadata(false, OnPassThroughChanged));

    public static void SetPassThrough(DependencyObject element, bool value) =>
        element.SetValue(PassThroughProperty, value);

    public static bool GetPassThrough(DependencyObject element) =>
        (bool)element.GetValue(PassThroughProperty);

    private static void OnPassThroughChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if (e.NewValue is true)
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        else
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject source) return;

        // Shift + 滚轮：横向滚动（内层确实有横向溢出时才拦）
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            ScrollViewer? inner = FindDescendant<ScrollViewer>(source);

            if (inner is not null && inner.ScrollableWidth > 0)
            {
                inner.ScrollToHorizontalOffset(inner.HorizontalOffset - e.Delta);
                e.Handled = true;
                return;
            }
        }

        // 其余情况：把滚轮转给外层 ScrollViewer（页面/对话流）
        ScrollViewer? outer = FindAncestor<ScrollViewer>(source);

        if (outer is null) return;

        outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = source
        });

        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        while (start is not null)
        {
            if (start is T match) return match;

            start = start is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(start)
                : LogicalTreeHelper.GetParent(start);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is T match) return match;

            T? nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }

        return null;
    }
}
