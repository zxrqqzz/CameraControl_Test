using CameraControl.ViewModels;
using System.Windows;

namespace CameraControl.Views
{
    /// <summary>
    /// 通讯管理弹窗
    /// </summary>
    public partial class CommunicationWindow : Window
    {
        public CommunicationWindow()
        {
            InitializeComponent();
            DataContext = new CommunicationViewModel(App.Communication);
        }
    }
}