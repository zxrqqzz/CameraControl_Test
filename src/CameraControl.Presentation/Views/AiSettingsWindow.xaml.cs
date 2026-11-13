using System.Windows;
using System.Windows.Controls;
using CameraControl.Presentation.ViewModels;

namespace CameraControl.Presentation.Views;

/// <summary>
/// AI 设置窗口（模型配置 + 上下文档位）。
///
/// 从主界面上搬出来的原因：DSH 式布局要把主体让给对话，
/// 而"模型/密钥/档位"是低频操作，摊在首屏只会挤占答复区。
/// </summary>
public partial class AiSettingsWindow : Window
{
    private MainViewModel? _subscribed;

    public AiSettingsWindow()
    {
        InitializeComponent();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribed is not null)
            _subscribed.ApiKeyAccepted -= OnApiKeyAccepted;

        if (e.NewValue is not MainViewModel viewModel) return;

        _subscribed = viewModel;
        viewModel.ApiKeyAccepted += OnApiKeyAccepted;
    }

    /// <summary>密钥落盘后清空输入框，不在界面上继续显示。</summary>
    private void OnApiKeyAccepted(object? sender, EventArgs e)
    {
        ApiKeyBox.Clear();
        _subscribed?.SetPendingApiKey(null);
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is PasswordBox box)
            viewModel.SetPendingApiKey(box.Password);
    }
}
