using System.Windows.Controls;

namespace CameraControl.Presentation.Views;

/// <summary>
/// 左侧导航。选中项与内容区页签同绑
/// <see cref="ViewModels.MainViewModel.SelectedPageIndex"/>，不再跨控件按名字互绑。
/// </summary>
public partial class NavSidebarView : UserControl
{
    public NavSidebarView() => InitializeComponent();
}
