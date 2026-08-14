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
        public ICommand ShowMainViewCommand { get; }
        public ICommand ShowCalibrationViewCommand { get; }

        // 机器人
        public string RobotIp { get; set; } = "192.168.1.11";
        public ICommand ConnectRobotCommand { get; }
        public ICommand ReadRobotCommand { get; }

        private string _robotOutputText = "";
        public string RobotOutputText
        {
            get => _robotOutputText;
            set { _robotOutputText = value; OnPropertyChanged(); }
        }

        // 标定界面
        public CalibrationViewModel CalibrationVM { get; }

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
            ConnectRobotCommand = new RelayCommand(ConnectRobot);
            ReadRobotCommand = new RelayCommand(ReadRobot);

            CalibrationVM = new CalibrationViewModel(App.Robot);

            DeviceCamera();

        }

        /// <summary>切换到主界面</summary>
        private void ShowMainView()
        {
            IsMainView = true;
            IsCalibrationView = false;
            OnPropertyChanged(nameof(IsMainView));
            OnPropertyChanged(nameof(IsCalibrationView));
        }

        /// <summary>切换到标定界面</summary>
        private void ShowCalibrationView()
        {
            IsMainView = false;
            IsCalibrationView = true;
            OnPropertyChanged(nameof(IsMainView));
            OnPropertyChanged(nameof(IsCalibrationView));
        }

        private bool _isConnecting;

        /// <summary>连接机器人(后台线程执行，避免阻塞UI)</summary>
        private async void ConnectRobot()
        {
            if (_isConnecting) return;
            _isConnecting = true;
            try
            {
                RobotOutputText = $"正在连接机器人 {RobotIp} ...";
                await App.Robot.ConnectAsync(RobotIp);
                RobotOutputText = $"机器人 {RobotIp} 连接成功。";
            }
            catch (Exception e)
            {
                string msg = "连接机器人失败: " + e.Message;
                App.LogVM.AddLog(msg);
                RobotOutputText = msg;
            }
            finally
            {
                _isConnecting = false;
            }
        }

        /// <summary>读取机器人六个轴的位置信息(未连接时自动连接，均后台执行并带超时)</summary>
        private async void ReadRobot()
        {
            try
            {
                if (!App.Robot.IsConnected)
                {
                    App.LogVM.AddLog("未连接机器人，自动尝试连接 ...");
                    await App.Robot.ConnectAsync(RobotIp);
                }

                var m = await App.Robot.ReadPositionAsync();
                var sb = new StringBuilder();
                sb.AppendLine($"读取时间: {m.ReadTime:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine("六轴关节角(度): ");
                sb.AppendLine($"  S={m.Ax1:F3}  L={m.Ax2:F3}  U={m.Ax3:F3}");
                sb.AppendLine($"  R={m.Ax4:F3}  B={m.Ax5:F3}  T={m.Ax6:F3}");
                sb.AppendLine("六轴直角坐标(mm/度): ");
                sb.AppendLine($"  X={m.X:F3}  Y={m.Y:F3}  Z={m.Z:F3}");
                sb.AppendLine($"  Rx={m.Cartesian[3]:F3}  Ry={m.Cartesian[4]:F3}  Rz={m.Cartesian[5]:F3}");
                sb.AppendLine("脉冲: ");
                sb.AppendLine($"  {string.Join("  ", Array.ConvertAll(m.Pulse, v => v.ToString("F0")))}");
                RobotOutputText = sb.ToString();
                App.LogVM.AddLog("读取机器人位置成功");
            }
            catch (Exception e)
            {
                string msg = "读取机器人位置失败: " + e.Message;
                App.LogVM.AddLog(msg);
                RobotOutputText = msg;
            }
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