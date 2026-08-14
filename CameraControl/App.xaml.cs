using CameraControl.Services;
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

        // 全局机器人通信服务(共享同一个连接)
        public static RobotService Robot { get; } = new RobotService();

        // 全局通讯管理服务(设备列表与数据收发)
        public static CommunicationService Communication { get; } = new CommunicationService();

        // 全局标定 ViewModel(主界面与全局触发共用)
        public static CalibrationViewModel Calibration { get; }

        // 全局触发服务
        public static GlobalTriggerService GlobalTrigger { get; }

        static App()
        {
            Calibration = new CalibrationViewModel(Robot);
            GlobalTrigger = new GlobalTriggerService(Communication, Calibration);
        }
    }

}
