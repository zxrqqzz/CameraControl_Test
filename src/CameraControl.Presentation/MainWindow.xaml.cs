using System.Windows;

namespace CameraControl.Presentation;

/// <summary>
/// 主窗口：只负责摆放四个区域——顶栏、导航、内容页签、状态栏。
///
/// 界面实现分别在 <c>Views/</c> 下的独立 UserControl 里；
/// 业务逻辑在 <see cref="ViewModels.MainViewModel"/> 里；
/// 页面级交互（例如 AI 页的输入快捷键与对话流滚动）在各自页面的代码后置里。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
