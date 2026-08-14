using CameraControl.ViewModels;
using System.Windows;

namespace CameraControl.Views
{
    /// <summary>
    /// 全局触发弹窗
    /// </summary>
    public partial class GlobalTriggerWindow : Window
    {
        public GlobalTriggerWindow()
        {
            InitializeComponent();
            DataContext = new GlobalTriggerViewModel(App.GlobalTrigger);
        }
    }
}