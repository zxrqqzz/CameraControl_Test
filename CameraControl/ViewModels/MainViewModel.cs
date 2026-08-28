using CameraControl.Services;
using GalaSoft.MvvmLight.Command;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CameraControl.ViewModels
{
    internal class MainViewModel : INotifyPropertyChanged
    {
        /// <summary>
        /// 日志存储
        /// </summary>
        public LogViewModel LogVM { get; } = App.LogVM;

        // 相机采集服务(SDK 逻辑全部封装在 Services 层)
        private readonly ICameraService _cameraService = new HikCameraService();

        /// <summary>相机服务实例(供参数设置界面访问 SDK 参数)</summary>
        public HikCameraService CameraService => (HikCameraService)_cameraService;

        // 命令绑定
        public ICommand SingleCaptureCommand { get; }
        public ICommand ContinuousCaptureCommand { get; }
        public ICommand ConnectCameraCommand { get; }
        public ICommand DisconnectCameraCommand { get; }

        // 导航
        public bool IsMainView { get; private set; } = true;
        public bool IsCalibrationView { get; private set; }
        public bool IsCalibrationToolsView { get; private set; }
        public bool IsUserCoordView { get; private set; }
        public bool IsRobotControlView { get; private set; }
        public ICommand ShowMainViewCommand { get; }
        public ICommand ShowCalibrationViewCommand { get; }
        public ICommand ShowCalibrationToolsViewCommand { get; }
        public ICommand ShowUserCoordViewCommand { get; }
        public ICommand ShowRobotControlViewCommand { get; }

        // 标定界面
        public CalibrationViewModel CalibrationVM { get; }

        // 用户坐标系直接读取界面
        public UserCoordinateViewModel UserCoordVM { get; }

        // 机器人控制界面
        public RobotControlViewModel RobotControlVM { get; }

        private string _cameraStatusText = "未连接";
        /// <summary>相机连接状态文本</summary>
        public string CameraStatusText
        {
            get => _cameraStatusText;
            set { _cameraStatusText = value; OnPropertyChanged(); }
        }

        private bool _isCameraConnected;
        /// <summary>相机是否已连接</summary>
        public bool IsCameraConnected
        {
            get => _isCameraConnected;
            set { _isCameraConnected = value; OnPropertyChanged(); }
        }

        private BitmapSource? _currentImage;
        /// <summary>当前显示的图像(主界面 Image 绑定)</summary>
        public BitmapSource? CurrentImage
        {
            get => _currentImage;
            set { _currentImage = value; OnPropertyChanged(); }
        }

        // 十字辅助线
        private bool _isCrosshairEnabled;
        /// <summary>十字线是否显示</summary>
        public bool IsCrosshairEnabled
        {
            get => _isCrosshairEnabled;
            set { _isCrosshairEnabled = value; OnPropertyChanged(); }
        }

        private bool _isCrosshairViewportMode = true;
        /// <summary>十字线模式: true=居中视口, false=居中图片</summary>
        public bool IsCrosshairViewportMode
        {
            get => _isCrosshairViewportMode;
            set
            {
                if (_isCrosshairViewportMode != value)
                {
                    _isCrosshairViewportMode = value;
                    OnPropertyChanged();
                    // 自动开启十字线
                    if (!IsCrosshairEnabled) IsCrosshairEnabled = true;
                }
            }
        }

        /// <summary>十字线模式: true=居中图片(与ViewportMode相反)</summary>
        public bool IsCrosshairImageMode
        {
            get => !_isCrosshairViewportMode;
            set { IsCrosshairViewportMode = !value; }
        }

        public MainViewModel()
        {
            // 订阅服务日志与图像事件
            _cameraService.LogMessage += msg => LogVM.AddLog(msg);
            _cameraService.ImageReceived += OnImageReceived;

            SingleCaptureCommand = new RelayCommand(SingleCapture);
            ContinuousCaptureCommand = new RelayCommand(ContinuousCapture);
            ConnectCameraCommand = new RelayCommand(ConnectCamera);
            DisconnectCameraCommand = new RelayCommand(DisconnectCamera);

            ShowMainViewCommand = new RelayCommand(ShowMainView);
            ShowCalibrationViewCommand = new RelayCommand(ShowCalibrationView);
            ShowCalibrationToolsViewCommand = new RelayCommand(ShowCalibrationToolsView);
            ShowUserCoordViewCommand = new RelayCommand(ShowUserCoordView);
            ShowRobotControlViewCommand = new RelayCommand(ShowRobotControlView);

            CalibrationVM = App.Calibration;
            UserCoordVM = App.UserCoordinate;
            RobotControlVM = new RobotControlViewModel(new RobotService());

            // 枚举并连接相机
            _cameraService.Initialize();
            UpdateConnectionState();
        }

        /// <summary>收到图像: 切回 UI 线程刷新显示</summary>
        private void OnImageReceived(BitmapSource image)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(new Action(() => CurrentImage = image));
            else
                CurrentImage = image;
        }

        /// <summary>根据服务状态刷新连接显示</summary>
        private void UpdateConnectionState()
        {
            CameraStatusText = _cameraService.IsConnected ? "已连接" : "未连接";
            IsCameraConnected = _cameraService.IsConnected;
        }

        /// <summary>切换到主界面</summary>
        private void ShowMainView()
        {
            IsMainView = true;
            IsCalibrationView = false;
            IsCalibrationToolsView = false;
            IsUserCoordView = false;
            IsRobotControlView = false;
            OnPropertyChanged(nameof(IsMainView));
            OnPropertyChanged(nameof(IsCalibrationView));
            OnPropertyChanged(nameof(IsCalibrationToolsView));
            OnPropertyChanged(nameof(IsUserCoordView));
            OnPropertyChanged(nameof(IsRobotControlView));
        }

        /// <summary>切换到标定界面</summary>
        private void ShowCalibrationView()
        {
            IsMainView = false;
            IsCalibrationView = true;
            IsCalibrationToolsView = false;
            IsUserCoordView = false;
            IsRobotControlView = false;
            OnPropertyChanged(nameof(IsMainView));
            OnPropertyChanged(nameof(IsCalibrationView));
            OnPropertyChanged(nameof(IsCalibrationToolsView));
            OnPropertyChanged(nameof(IsUserCoordView));
            OnPropertyChanged(nameof(IsRobotControlView));
        }

        /// <summary>切换到标定工具界面</summary>
        private void ShowCalibrationToolsView()
        {
            IsMainView = false;
            IsCalibrationView = false;
            IsCalibrationToolsView = true;
            IsUserCoordView = false;
            IsRobotControlView = false;
            OnPropertyChanged(nameof(IsMainView));
            OnPropertyChanged(nameof(IsCalibrationView));
            OnPropertyChanged(nameof(IsCalibrationToolsView));
            OnPropertyChanged(nameof(IsUserCoordView));
            OnPropertyChanged(nameof(IsRobotControlView));
        }

        /// <summary>切换到用户坐标系直接读取界面</summary>
        private void ShowUserCoordView()
        {
            IsMainView = false;
            IsCalibrationView = false;
            IsCalibrationToolsView = false;
            IsUserCoordView = true;
            IsRobotControlView = false;
            OnPropertyChanged(nameof(IsMainView));
            OnPropertyChanged(nameof(IsCalibrationView));
            OnPropertyChanged(nameof(IsCalibrationToolsView));
            OnPropertyChanged(nameof(IsUserCoordView));
            OnPropertyChanged(nameof(IsRobotControlView));
        }

        /// <summary>切换到机器人控制界面</summary>
        private void ShowRobotControlView()
        {
            IsMainView = false;
            IsCalibrationView = false;
            IsCalibrationToolsView = false;
            IsUserCoordView = false;
            IsRobotControlView = true;
            OnPropertyChanged(nameof(IsMainView));
            OnPropertyChanged(nameof(IsCalibrationView));
            OnPropertyChanged(nameof(IsCalibrationToolsView));
            OnPropertyChanged(nameof(IsUserCoordView));
            OnPropertyChanged(nameof(IsRobotControlView));
        }

        /// <summary>
        /// 连接相机
        /// </summary>
        private void ConnectCamera()
        {
            _cameraService.Connect();
            UpdateConnectionState();
        }

        /// <summary>
        /// 断开相机
        /// </summary>
        private void DisconnectCamera()
        {
            _cameraService.Disconnect();
            UpdateConnectionState();
        }

        /// <summary>
        /// 单次采集
        /// </summary>
        private void SingleCapture()
        {
            _cameraService.CaptureOnce();
        }

        /// <summary>
        /// 连续采集
        /// </summary>
        private void ContinuousCapture()
        {
            _cameraService.ToggleContinuousCapture();
        }

        /// <summary>
        /// 关闭SDK，释放资源
        /// </summary>
        public void Dispose()
        {
            _cameraService.ImageReceived -= OnImageReceived;
            _cameraService.Dispose();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
