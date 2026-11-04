using CameraControl.Models;
using CameraControl.Services;
using GalaSoft.MvvmLight.Command;
using System;
using System.ComponentModel;
using System.Linq;
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

        // 当前生效的采集通道(按设备管理的选中相机切换: 海康 SDK / UVC 预留通道)
        private ICameraService? _cameraService;

        // 两条采集通道(按需创建)
        private HikCameraService? _hikService;
        private UvcCameraService? _uvcService;

        /// <summary>
        /// 海康相机服务实例(供参数设置界面访问 SDK 参数)，未创建或未连接时为 null
        /// </summary>
        public HikCameraService? HikCamera => _hikService;

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

            // 海康 SDK 初始化 + 枚举(不再自动连到硬编码 IP)
            EnsureHikService();
            _hikService!.Initialize();
            _cameraService = _hikService;

            // 设备管理里已经有可连接的相机时，启动顺手连上第一台
            if (FindConnectableTarget() != null)
                ConnectCamera();
            else
                UpdateConnectionState();
        }

        // ────────────────────────────────────────────────────────────────
        // 采集通道管理
        // ────────────────────────────────────────────────────────────────

        private HikCameraService EnsureHikService()
        {
            if (_hikService == null)
            {
                _hikService = new HikCameraService();
                WireService(_hikService);
            }
            return _hikService;
        }

        /// <summary>为指定设备挑选采集通道</summary>
        private ICameraService ResolveService(CameraDevice device)
        {
            if (device.Source == DeviceSource.Uvc)
            {
                if (_uvcService == null)
                {
                    _uvcService = new UvcCameraService();
                    WireService(_uvcService);
                    _uvcService.Initialize();
                }
                return _uvcService;
            }

            HikCameraService hik = EnsureHikService();
            if (!HikSdkRuntime.IsInitialized) hik.Initialize();
            return hik;
        }

        private void WireService(ICameraService service)
        {
            service.LogMessage += OnServiceLog;
            service.ImageReceived += OnImageReceived;
        }

        private void UnwireService(ICameraService service)
        {
            service.LogMessage -= OnServiceLog;
            service.ImageReceived -= OnImageReceived;
        }

        private void OnServiceLog(string message) => LogVM.AddLog(message);

        /// <summary>收到图像: 切回 UI 线程刷新显示</summary>
        private void OnImageReceived(BitmapSource image)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(new Action(() => CurrentImage = image));
            else
                CurrentImage = image;
        }

        /// <summary>找出当前应该连接的相机: 优先设备管理里选中的那个，否则第一台可连接的</summary>
        private CameraDevice? FindConnectableTarget()
        {
            CameraDevice? selected = App.DeviceManagement.SelectedCamera;
            if (selected != null && selected.CanConnect) return selected;

            return App.DeviceManagement.Devices
                .OfType<CameraDevice>()
                .FirstOrDefault(d => d.CanConnect);
        }

        /// <summary>根据服务状态刷新连接显示</summary>
        private void UpdateConnectionState()
        {
            bool connected = _cameraService?.IsConnected == true;
            IsCameraConnected = connected;

            CameraDevice? target = _cameraService switch
            {
                HikCameraService hik => hik.TargetDevice,
                UvcCameraService uvc => uvc.Device,
                _ => null
            };

            CameraStatusText = connected
                ? (string.IsNullOrWhiteSpace(target?.Name) ? "已连接" : "已连接 · " + target!.Name)
                : "未连接";
        }

        // ────────────────────────────────────────────────────────────────
        // 导航
        // ────────────────────────────────────────────────────────────────

        /// <summary>切换到主界面</summary>
        private void ShowMainView() => Navigate(main: true, calibration: false, tools: false, userCoord: false, robot: false);

        /// <summary>切换到标定界面</summary>
        private void ShowCalibrationView() => Navigate(main: false, calibration: true, tools: false, userCoord: false, robot: false);

        /// <summary>切换到标定工具界面</summary>
        private void ShowCalibrationToolsView() => Navigate(main: false, calibration: false, tools: true, userCoord: false, robot: false);

        /// <summary>切换到用户坐标系直接读取界面</summary>
        private void ShowUserCoordView() => Navigate(main: false, calibration: false, tools: false, userCoord: true, robot: false);

        /// <summary>切换到机器人控制界面</summary>
        private void ShowRobotControlView() => Navigate(main: false, calibration: false, tools: false, userCoord: false, robot: true);

        private void Navigate(bool main, bool calibration, bool tools, bool userCoord, bool robot)
        {
            IsMainView = main;
            IsCalibrationView = calibration;
            IsCalibrationToolsView = tools;
            IsUserCoordView = userCoord;
            IsRobotControlView = robot;

            OnPropertyChanged(nameof(IsMainView));
            OnPropertyChanged(nameof(IsCalibrationView));
            OnPropertyChanged(nameof(IsCalibrationToolsView));
            OnPropertyChanged(nameof(IsUserCoordView));
            OnPropertyChanged(nameof(IsRobotControlView));
        }

        // ────────────────────────────────────────────────────────────────
        // 采集命令
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 连接相机: 连接「设备管理」里选中的相机(没选中就取第一台可连接的)。
        /// </summary>
        private void ConnectCamera()
        {
            CameraDevice? target = FindConnectableTarget();
            if (target == null)
            {
                LogVM.AddLog("没有可连接的相机。请先在「设备管理」里点「扫描」自动发现，或手工添加设备。");
                UpdateConnectionState();
                return;
            }

            ICameraService service = ResolveService(target);

            // 切换采集通道: 先把旧通道断掉，避免两台相机同时占着
            if (!ReferenceEquals(service, _cameraService))
            {
                try { _cameraService?.Disconnect(); } catch { }
                _cameraService = service;
            }

            bool ok = service.Connect(target);
            UpdateConnectionState();

            if (!ok && !service.SupportsStreaming)
                LogVM.AddLog($"「{target.Name}」的采集通道尚未实现(UVC 预留接口)，目前只能登记设备，暂不支持预览。");
        }

        /// <summary>
        /// 断开相机
        /// </summary>
        private void DisconnectCamera()
        {
            try { _cameraService?.Disconnect(); } catch { }

            // 隐私: 断开后立刻丢掉当前显示的画面引用，不留在内存里
            ClearDisplayedFrame();
            UpdateConnectionState();
        }

        /// <summary>
        /// 丢弃当前显示的画面。
        /// 相机帧只以 BitmapSource 存在于内存(不做任何序列化/落盘)，
        /// 这里把引用置空，交给 GC 回收；程序退出后内存自然清空。
        /// </summary>
        private void ClearDisplayedFrame() => CurrentImage = null;

        /// <summary>
        /// 单次采集
        /// </summary>
        private void SingleCapture()
        {
            if (_cameraService == null) return;
            _cameraService.CaptureOnce();
        }

        /// <summary>
        /// 连续采集
        /// </summary>
        private void ContinuousCapture()
        {
            if (_cameraService == null) return;
            _cameraService.ToggleContinuousCapture();
        }

        /// <summary>
        /// 关闭SDK，释放资源。
        /// 隐私: 同时清空当前画面引用，保证程序关闭后不留残影。
        /// </summary>
        public void Dispose()
        {
            ClearDisplayedFrame();

            if (_hikService != null)
            {
                UnwireService(_hikService);
                _hikService.Dispose();
                _hikService = null;
            }

            if (_uvcService != null)
            {
                UnwireService(_uvcService);
                _uvcService.Dispose();
                _uvcService = null;
            }

            _cameraService = null;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
