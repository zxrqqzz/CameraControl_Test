using System.Windows.Controls;

namespace CameraControl.Presentation.Views;

/// <summary>
/// 标定页：棋盘参数 → 采集 → 求解结果。
/// 面板状态在 <see cref="ViewModels.CalibrationPanelViewModel"/> 上。
/// </summary>
public partial class CalibrationPage : UserControl
{
    public CalibrationPage() => InitializeComponent();
}
