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

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _vm?.Dispose();
            App.Robot.Dispose();
            App.Communication.Dispose();
        }
    }
}