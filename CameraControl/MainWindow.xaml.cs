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

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _vm?.Dispose();
            App.Robot.Dispose();
            App.Communication.Dispose();
        }
    }
}