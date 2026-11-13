using System.Windows;
using System.Windows.Controls;

namespace CameraControl.Presentation.Views;

/// <summary>
/// 顶栏：品牌 + 全局入口（调色卡、AI 设置）。
///
/// 调色卡是 <see cref="System.Windows.Controls.Primitives.Popup"/>（独立 HWND），
/// 界面目检工具（<c>--shot</c>）只能单独渲染它的内容面板，所以这里给它留两个内部方法，
/// 不对外暴露成公共 API。
/// </summary>
public partial class AppHeaderView : UserControl
{
    public AppHeaderView() => InitializeComponent();

    /// <summary>调色卡面板本体（Popup 的 Child），供界面目检工具单独出图。</summary>
    internal FrameworkElement? PaletteCard => PalettePopup.Child as FrameworkElement;

    /// <summary>展开 / 收起调色卡。</summary>
    internal void SetPaletteOpen(bool isOpen) => PaletteToggle.IsChecked = isOpen;
}
