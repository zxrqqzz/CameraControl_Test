using System.Windows;
using CameraControl.Presentation.ViewModels;

namespace CameraControl.Presentation;

/// <summary>主窗口。业务逻辑都在 <see cref="MainViewModel"/> 里，这里只做界面粘合。</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// PasswordBox 的 Password 不能直接绑定（WPF 有意设计），
    /// 这里在变更时把值同步进 ViewModel 的输入缓冲。
    /// 值只驻留内存，保存时才加密落盘。
    /// </summary>
    /// <summary>密钥被接受后清空输入框：落盘之后不该继续显示在界面上。</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is MainViewModel viewModel)
        {
            viewModel.ApiKeyAccepted += (_, _) =>
            {
                ApiKeyBox.Clear();
                viewModel.SetPendingApiKey(null);
            };
        }
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is System.Windows.Controls.PasswordBox box)
            viewModel.SetPendingApiKey(box.Password);
    }
}
