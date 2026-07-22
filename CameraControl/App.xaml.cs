using CameraControl.ViewModels;
using System.Configuration;
using System.Data;
using System.Windows;

namespace CameraControl
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // 全局日志 ViewModel
        public static LogViewModel LogVM { get; } = new LogViewModel();
    }

}
