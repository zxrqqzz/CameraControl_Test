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
        public ICommand ShowMainViewCommand { get; }
        public ICommand ShowCalibrationViewCommand { get; }
        public ICommand ShowCalibrationToolsViewCommand { get; }
        public ICommand ShowUserCoordViewCommand { get; }

        // 标定界面
        public CalibrationViewModel CalibrationVM { get; }

        // 用户坐标系直接读取界面
        public UserCoordinateViewModel UserCoordVM { get; }

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

            CalibrationVM = App.Calibration;
            UserCoordVM = App.UserCoordinate;

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
            OnPropertyChanged(nameof(IsMainView));
            OnPropertyChanged(nameof(IsCalibrationView));
            OnPropertyChanged(nameof(IsCalibrationToolsView));
            OnPropertyChanged(nameof(IsUserCoordView));
        }

        /// <summary>切换到标定界面</summary>
        private void ShowCalibrationView()
        {
            IsMainView = false;
            IsCalibrationView = true;
            IsCalibrationToolsView = false;
            IsUserCoordView = false;
            OnPropertyChanged(nameof(IsMainView));
            OnPropertyChanged(nameof(IsCalibrationView));
            OnPropertyChanged(nameof(IsCalibrationToolsView));
            OnPropertyChanged(nameof(IsUserCoordView));
        }

        /// <summary>切换到标定工具界面</summary>
        private void ShowCalibrationToolsView()
        {
            IsMainView = false;
            IsCalibrationView = false;
            IsCalibrationToolsView = true;
            IsUserCoordView = false;
            OnPropertyChanged(nameof(IsMainView));
            OnPropertyChanged(nameof(IsCalibrationView));
            OnPropertyChanged(nameof(IsCalibrationToolsView));
            OnPropertyChanged(nameof(IsUserCoordView));
        }

        /// <summary>切换到用户坐标系直接读取界面</summary>
        private void ShowUserCoordView()
        {
            IsMainView = false;
            IsCalibrationView = false;
            IsCalibrationToolsView = false;
            IsUserCoordView = true;
            OnPropertyChanged(nameof(IsMainView));
            OnPropertyChanged(nameof(IsCalibrationView));
            OnPropertyChanged(nameof(IsCalibrationToolsView));
            OnPropertyChanged(nameof(IsUserCoordView));
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
