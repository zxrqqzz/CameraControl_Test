using System.Windows.Controls;

namespace CameraControl.Presentation.Views;

/// <summary>
/// 系统页：自检 / 指标 / 审计三块只读面板。
/// 数据在 <see cref="ViewModels.MainViewModel"/> 上，这里只做视图粘合。
/// </summary>
public partial class SystemPage : UserControl
{
    public SystemPage() => InitializeComponent();
}
