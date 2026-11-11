using System.Windows;

namespace CameraControl.Presentation.ViewModels;

/// <summary>
/// 简单的确认对话框抽象。
///
/// 为什么单独抽出来：视图模型不该直接弹窗（难测试、也容易在非 UI 线程崩），
/// 但仍需要一个"删除前确认"的入口。
/// </summary>
public interface IDialogService
{
    /// <summary>确认对话框；返回 true 表示用户点了"是"。</summary>
    bool Confirm(string title, string message);
}

/// <summary>
/// 默认实现：**拒绝**。
///
/// 没有注入真实对话框时，删除类操作一律取消——
/// 宁可删不掉，也不能在误配置的情况下悄悄删掉密钥。
/// </summary>
public sealed class RefusingDialogService : IDialogService
{
    public bool Confirm(string title, string message) => false;
}

/// <summary>基于 MessageBox 的实现（由 App 注入）。</summary>
public sealed class MessageBoxDialogService : IDialogService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
}
