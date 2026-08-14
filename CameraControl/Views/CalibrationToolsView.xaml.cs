using System.Windows.Controls;

namespace CameraControl.Views
{
    /// <summary>
    /// 标定工具界面(从标定界面剥离的功能):
    /// 机器人连接、位置读取/记录、取值范围与精度设置、坐标系导入导出、坐标转换、精度测试。
    /// </summary>
    public partial class CalibrationToolsView : UserControl
    {
        public CalibrationToolsView()
        {
            InitializeComponent();
        }
    }
}