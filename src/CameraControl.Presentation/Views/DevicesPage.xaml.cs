using System.Windows.Controls;

namespace CameraControl.Presentation.Views;

/// <summary>
/// 设备页：枚举 → 选中相机 → 连接 → 采图预览。
/// 数据与命令都在 <see cref="ViewModels.MainViewModel"/> 上，这里只做视图粘合。
/// </summary>
public partial class DevicesPage : UserControl
{
    public DevicesPage() => InitializeComponent();
}
