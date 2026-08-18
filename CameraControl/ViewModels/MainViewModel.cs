using GalaSoft.MvvmLight.Command;
using MvCameraControl;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace CameraControl.ViewModels
{
    internal class MainViewModel : INotifyPropertyChanged
    {
        /// <summary>
        /// 日志存储
        /// </summary>
        public LogViewModel LogVM { get; } = App.LogVM;

        // 相机对象
        IDevice? _camera = null;
        private IntPtr _displayHandle;   // PictureBox 的句柄

        // 相机IP与PC网卡IP
        private const string CameraIp = "192.168.1.10";
        private const string NetExportIp = "192.168.1.100";

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

        public MainViewModel()
        {
            SDKSystem.Initialize();//初始化

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

            DeviceCamera();

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
        /// 相机设备枚举和创建
        /// </summary>
        public void DeviceCamera()
        {
            //枚举设备，获取设备信息
            List<IDeviceInfo> devInfoList = null;
            int ret = DeviceEnumerator.EnumDevicesEx(
                 DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice, "", out devInfoList);
            if (ret == MvError.MV_OK)
            {
                /*处理XXX厂商的相机*/
                App.LogVM.AddLog($"枚举设备成功，设备数量: {devInfoList.Count}");
                foreach (var dev in devInfoList)
                    App.LogVM.AddLog($"型号: {dev.ModelName}, 厂商: {dev.ManufacturerName}, 序列号: {dev.SerialNumber}");
            }

            // 打开相机
            ConnectCamera();
        }

        /// <summary>
        /// 连接相机(创建/打开设备，可反复调用)
        /// </summary>
        private void ConnectCamera()
        {
            try
            {
                if (_camera == null)
                {
                    _camera = DeviceFactory.CreateDeviceByIp(CameraIp, NetExportIp);
                    LogVM.AddLog($"已创建相机对象: {_camera}");
                }

                // 打开设备
                int ret = _camera.Open();
                if (ret != MvError.MV_OK)
                {
                    LogVM.AddLog($"打开相机失败: 0x{ret:X}");
                    CameraStatusText = "连接失败";
                    IsCameraConnected = false;
                    return;
                }

                LogVM.AddLog($"相机 {_camera} 连接成功");
                CameraStatusText = "已连接";
                IsCameraConnected = true;
            }
            catch (Exception e)
            {
                LogVM.AddLog($"打开相机失败: {e.Message}");
                CameraStatusText = "连接失败";
                IsCameraConnected = false;
            }
        }

        /// <summary>
        /// 断开相机(关闭设备，可再次连接)
        /// </summary>
        private void DisconnectCamera()
        {
            try
            {
                if (_camera == null) return;
                int ret = _camera.Close();
                if (ret != MvError.MV_OK)
                    LogVM.AddLog($"关闭相机返回: 0x{ret:X}");
                else
                    LogVM.AddLog("相机已断开");
            }
            catch (Exception e)
            {
                LogVM.AddLog($"断开相机异常: {e.Message}");
            }
            finally
            {
                CameraStatusText = "未连接";
                IsCameraConnected = false;
            }
        }



        /// <summary>
        /// 单次采集
        /// </summary>
        private void SingleCapture()
        {
            

        }
        /// <summary>
        /// 连续采集
        /// </summary>
        private void ContinuousCapture()
        {

        }

        

        /// <summary>
        /// 关闭SDK，释放资源
        /// </summary>
        public void Dispose()
        {
            try { _camera?.Dispose(); } catch { }
            _camera = null;
            SDKSystem.Finalize();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}