using CameraControl.Services;
using CameraControl.ViewModels;
using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;

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

        // 全局用户坐标系直接读取 ViewModel
        public static UserCoordinateViewModel UserCoordinate { get; }

        // 全局触发服务
        public static GlobalTriggerService GlobalTrigger { get; }

        // 设备管理
        public static DeviceManagementViewModel DeviceManagement { get; } = new DeviceManagementViewModel();

        // 应用设置自动保存/加载
        public static AppSettingsService Settings { get; }

        static App()
        {
            Calibration = new CalibrationViewModel(Robot);
            UserCoordinate = new UserCoordinateViewModel(Robot);
            GlobalTrigger = new GlobalTriggerService(Communication, Calibration);
            Settings = new AppSettingsService();
            Settings.LoadAll();
        }

        /// <summary>
        /// 全局异常兜底: 机器人连接失败、后台任务异常等都不会再导致程序崩溃，
        /// 统一记录到日志窗口，界面线程异常标记为已处理。
        /// </summary>
        public App()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogVM.AddLog("未处理异常(界面线程): " + e.Exception.Message);
            e.Handled = true;
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
            string msg = e.Exception != null && e.Exception.InnerExceptions.Count > 0
                ? e.Exception.InnerExceptions[0].Message
                : e.Exception?.Message ?? "未知异常";
            LogVM.AddLog("后台任务异常(已忽略): " + msg);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                LogVM.AddLog("致命异常: " + ex.Message);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Settings?.SaveAll();
            base.OnExit(e);
        }
    }

}
