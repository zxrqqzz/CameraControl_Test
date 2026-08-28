using CameraControl.ViewModels;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace CameraControl
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private MainViewModel _vm;
        private Views.CommunicationWindow? _commWindow;
        private Views.GlobalTriggerWindow? _triggerWindow;
        private Views.DeviceManagementWindow? _deviceWindow;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainViewModel();
            DataContext = _vm;

            // 监听图像变化更新十字线
            _vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.CurrentImage))
                    UpdateCrosshair();
            };
        }

        /// <summary>打开设备管理弹窗</summary>
        private void OnDeviceManagementClick(object sender, RoutedEventArgs e)
        {
            if (_deviceWindow == null)
            {
                _deviceWindow = new Views.DeviceManagementWindow { Owner = this };
                _deviceWindow.Closed += (_, _) => _deviceWindow = null;
            }
            _deviceWindow.Show();
            _deviceWindow.Activate();
        }

        /// <summary>打开相机参数设置弹窗</summary>
        private void OnParameterSettingsClick(object sender, RoutedEventArgs e)
        {
            var paramWindow = new Views.CameraParameterWindow(_vm.CameraService) { Owner = this };
            paramWindow.Show();
        }

        /// <summary>打开通讯管理弹窗</summary>
        private void OnCommunicationClick(object sender, RoutedEventArgs e)
        {
            if (_commWindow == null)
            {
                _commWindow = new Views.CommunicationWindow { Owner = this };
                _commWindow.Closed += (_, _) => _commWindow = null;
            }
            _commWindow.Show();
            _commWindow.Activate();
        }

        /// <summary>打开全局触发弹窗</summary>
        private void OnGlobalTriggerClick(object sender, RoutedEventArgs e)
        {
            if (_triggerWindow == null)
            {
                _triggerWindow = new Views.GlobalTriggerWindow { Owner = this };
                _triggerWindow.Closed += (_, _) => _triggerWindow = null;
            }
            _triggerWindow.Show();
            _triggerWindow.Activate();
        }

        /// <summary>十字线下拉菜单点击</summary>
        private void CrosshairDropdown_Click(object sender, MouseButtonEventArgs e)
        {
            CrosshairPopup.IsOpen = true;
            e.Handled = true;
        }

        /// <summary>十字线按钮点击 - 切换显示/隐藏</summary>
        private void BtnCrosshair_Click(object sender, RoutedEventArgs e)
        {
            _vm.IsCrosshairEnabled = !_vm.IsCrosshairEnabled;
            UpdateCrosshair();
        }

        /// <summary>更新十字线位置和可见性</summary>
        private void UpdateCrosshair()
        {
            if (CrosshairCanvas == null) return;

            bool show = _vm.IsCrosshairEnabled;
            CrosshairCanvas.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            if (!show) return;

            double w = CrosshairCanvas.ActualWidth;
            double h = CrosshairCanvas.ActualHeight;

            if (w < 1 || h < 1) return;

            double cx, cy;

            if (_vm.IsCrosshairViewportMode)
            {
                // 模式1: 十字线始终在视口中心
                cx = w / 2;
                cy = h / 2;
            }
            else
            {
                // 模式2: 十字线在图片中心（图片居中显示，所以也在视口中心）
                // 由于Image使用Stretch="Uniform"且居中，图片中心就是视口中心
                cx = w / 2;
                cy = h / 2;
            }

            // 更新水平线
            CrosshairH.X1 = 0;
            CrosshairH.Y1 = cy;
            CrosshairH.X2 = w;
            CrosshairH.Y2 = cy;

            // 更新垂直线
            CrosshairV.X1 = cx;
            CrosshairV.Y1 = 0;
            CrosshairV.X2 = cx;
            CrosshairV.Y2 = h;
        }

        /// <summary>窗口大小变化时更新十字线</summary>
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateCrosshair();
        }

        private bool _isPanning;
        private Point _panLastPos;

        /// <summary>日志复制 - 将选中项拼接写入剪贴板</summary>
        private void OnCopyLogs(object sender, ExecutedRoutedEventArgs e)
        {
            if (LogListBox == null) return;
            var selected = LogListBox.SelectedItems?.Cast<string>().ToArray();
            if (selected == null || selected.Length == 0) return;
            Clipboard.SetText(string.Join(Environment.NewLine, selected));
        }

        /// <summary>仅当存在选中项时才允许复制</summary>
        private void OnCanExecuteCopyLogs(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = LogListBox != null && LogListBox.SelectedItems?.Count > 0;
        }

        private const double ZoomMin = 0.1;
        private const double ZoomMax = 20.0;
        private const double ZoomStep = 1.15;

        /// <summary>滚轮缩放 - 以鼠标位置为中心放大缩小</summary>
        private void ImageContainer_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scale = ZoomScale.ScaleX;
            double factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
            double newScale = Math.Clamp(scale * factor, ZoomMin, ZoomMax);
            if (Math.Abs(newScale - scale) < 0.0001) return;

            double ratio = newScale / scale;
            var pos = e.GetPosition(ImageContainer);
            double tx = pos.X - (pos.X - ZoomTranslate.X) * ratio;
            double ty = pos.Y - (pos.Y - ZoomTranslate.Y) * ratio;

            ZoomScale.ScaleX = newScale;
            ZoomScale.ScaleY = newScale;
            ZoomTranslate.X = tx;
            ZoomTranslate.Y = ty;
            e.Handled = true;
        }

        /// <summary>1:1 还原 - 缩放与平移复位</summary>
        private void BtnResetZoom_Click(object sender, RoutedEventArgs e)
        {
            ZoomScale.ScaleX = 1;
            ZoomScale.ScaleY = 1;
            ZoomTranslate.X = 0;
            ZoomTranslate.Y = 0;
        }

        /// <summary>开始平移(按住左键拖动查看放大后的图像)</summary>
        private void Image_PanStart(object sender, MouseButtonEventArgs e)
        {
            _isPanning = true;
            _panLastPos = e.GetPosition(ImageContainer);
            DisplayImage.CaptureMouse();
            e.Handled = true;
        }

        /// <summary>平移过程中</summary>
        private void Image_PanMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            var pos = e.GetPosition(ImageContainer);
            ZoomTranslate.X += pos.X - _panLastPos.X;
            ZoomTranslate.Y += pos.Y - _panLastPos.Y;
            _panLastPos = pos;
        }

        /// <summary>结束平移</summary>
        private void Image_PanEnd(object sender, MouseButtonEventArgs e)
        {
            _isPanning = false;
            DisplayImage.ReleaseMouseCapture();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _vm?.Dispose();
            App.Robot.Dispose();
            App.Communication.Dispose();
        }
    }
}