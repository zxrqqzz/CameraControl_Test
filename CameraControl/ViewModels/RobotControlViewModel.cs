using CameraControl.Models;
using CameraControl.Services;
using GalaSoft.MvvmLight.Command;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CameraControl.ViewModels
{
    /// <summary>
    /// 机器人控制 ViewModel
    /// 读取坐标系列表，选择坐标系后输入坐标发送移动指令
    /// </summary>
    public class RobotControlViewModel : INotifyPropertyChanged
    {
        private readonly RobotService _robot;
        private bool _isConnecting;

        public LogViewModel LogVM { get; } = App.LogVM;

        /// <summary>机器人IP(与标定界面共用)</summary>
        public string RobotIp
        {
            get => App.Calibration.RobotIp;
            set { if (App.Calibration.RobotIp != value) { App.Calibration.RobotIp = value; OnPropertyChanged(); } }
        }

        /// <summary>坐标系类型列表</summary>
        public ObservableCollection<string> FrameTypes { get; } = new ObservableCollection<string>
        {
            "用户坐标系 (UF)",
            "工具坐标系 (UT)"
        };

        private string _selectedFrameType = "用户坐标系 (UF)";
        /// <summary>当前选择的坐标系类型</summary>
        public string SelectedFrameType
        {
            get => _selectedFrameType;
            set { _selectedFrameType = value; OnPropertyChanged(); LoadFrameList(); }
        }

        /// <summary>坐标系编号列表</summary>
        public ObservableCollection<int> FrameNumbers { get; } = new ObservableCollection<int>();

        private int _selectedFrameNumber = 1;
        /// <summary>当前选择的坐标系编号</summary>
        public int SelectedFrameNumber
        {
            get => _selectedFrameNumber;
            set { _selectedFrameNumber = value; OnPropertyChanged(); }
        }

        /// <summary>当前坐标系下的位置 X</summary>
        private string _currentX = "--";
        public string CurrentX
        {
            get => _currentX;
            set { _currentX = value; OnPropertyChanged(); }
        }

        /// <summary>当前坐标系下的位置 Y</summary>
        private string _currentY = "--";
        public string CurrentY
        {
            get => _currentY;
            set { _currentY = value; OnPropertyChanged(); }
        }

        /// <summary>当前坐标系下的位置 Z</summary>
        private string _currentZ = "--";
        public string CurrentZ
        {
            get => _currentZ;
            set { _currentZ = value; OnPropertyChanged(); }
        }

        /// <summary>当前坐标系下的姿态 Rx</summary>
        private string _currentRx = "--";
        public string CurrentRx
        {
            get => _currentRx;
            set { _currentRx = value; OnPropertyChanged(); }
        }

        /// <summary>当前坐标系下的姿态 Ry</summary>
        private string _currentRy = "--";
        public string CurrentRy
        {
            get => _currentRy;
            set { _currentRy = value; OnPropertyChanged(); }
        }

        /// <summary>当前坐标系下的姿态 Rz</summary>
        private string _currentRz = "--";
        public string CurrentRz
        {
            get => _currentRz;
            set { _currentRz = value; OnPropertyChanged(); }
        }

        /// <summary>输入坐标 X</summary>
        private string _inputX = "0";
        public string InputX
        {
            get => _inputX;
            set { _inputX = value; OnPropertyChanged(); }
        }

        /// <summary>输入坐标 Y</summary>
        private string _inputY = "0";
        public string InputY
        {
            get => _inputY;
            set { _inputY = value; OnPropertyChanged(); }
        }

        /// <summary>输入坐标 Z</summary>
        private string _inputZ = "0";
        public string InputZ
        {
            get => _inputZ;
            set { _inputZ = value; OnPropertyChanged(); }
        }

        /// <summary>输入姿态 Rx</summary>
        private string _inputRx = "0";
        public string InputRx
        {
            get => _inputRx;
            set { _inputRx = value; OnPropertyChanged(); }
        }

        /// <summary>输入姿态 Ry</summary>
        private string _inputRy = "0";
        public string InputRy
        {
            get => _inputRy;
            set { _inputRy = value; OnPropertyChanged(); }
        }

        /// <summary>输入姿态 Rz</summary>
        private string _inputRz = "0";
        public string InputRz
        {
            get => _inputRz;
            set { _inputRz = value; OnPropertyChanged(); }
        }

        /// <summary>输出文本</summary>
        private string _outputText = "";
        public string OutputText
        {
            get => _outputText;
            set { _outputText = value; OnPropertyChanged(); }
        }

        /// <summary>运动速度(%)</summary>
        private string _speedText = "10";
        public string SpeedText
        {
            get => _speedText;
            set { _speedText = value; OnPropertyChanged(); }
        }

        /// <summary>运动类型列表</summary>
        public ObservableCollection<string> MotionTypes { get; } = new ObservableCollection<string>
        {
            "关节运动 (J)",
            "直线运动 (L)"
        };

        private string _selectedMotionType = "直线运动 (L)";
        /// <summary>当前选择的运动类型</summary>
        public string SelectedMotionType
        {
            get => _selectedMotionType;
            set { _selectedMotionType = value; OnPropertyChanged(); }
        }

        /// <summary>命令列表</summary>
        public ObservableCollection<string> CommandHistory { get; } = new ObservableCollection<string>();

        public ICommand ConnectCommand { get; }
        public ICommand ReadPositionCommand { get; }
        public ICommand MoveCommand { get; }
        public ICommand MoveToOriginCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand RefreshFrameListCommand { get; }

        public RobotControlViewModel(RobotService robot)
        {
            _robot = robot;
            ConnectCommand = new RelayCommand(Connect);
            ReadPositionCommand = new RelayCommand(ReadPosition);
            MoveCommand = new RelayCommand(Move);
            MoveToOriginCommand = new RelayCommand(MoveToOrigin);
            StopCommand = new RelayCommand(Stop);
            RefreshFrameListCommand = new RelayCommand(LoadFrameList);
        }

        /// <summary>连接机器人</summary>
        private async void Connect()
        {
            if (_isConnecting) return;
            _isConnecting = true;
            try
            {
                OutputText = "正在连接机器人 " + RobotIp + " ...";
                await _robot.ConnectAsync(RobotIp);
                OutputText = "机器人连接成功: " + RobotIp;
                LoadFrameList();
            }
            catch (Exception e)
            {
                string msg = "连接机器人失败: " + e.Message;
                OutputText = msg;
                LogVM.AddLog(msg);
            }
            finally
            {
                _isConnecting = false;
            }
        }

        /// <summary>加载坐标系列表</summary>
        private void LoadFrameList()
        {
            FrameNumbers.Clear();
            // 用户坐标系 UF1~UF63, 工具坐标系 UT1~UT64
            int count = SelectedFrameType.Contains("用户") ? 63 : 64;
            for (int i = 1; i <= count; i++)
                FrameNumbers.Add(i);
        }

        /// <summary>读取当前位置</summary>
        private async void ReadPosition()
        {
            try
            {
                if (!_robot.IsConnected)
                {
                    OutputText = "未连接机器人，正在连接...";
                    await _robot.ConnectAsync(RobotIp);
                }

                RobotModel m;
                if (SelectedFrameType.Contains("用户"))
                {
                    m = await _robot.ReadPositionInUserFrameAsync((byte)SelectedFrameNumber);
                }
                else
                {
                    m = await _robot.ReadPositionAsync();
                }

                CurrentX = m.X.ToString("F3");
                CurrentY = m.Y.ToString("F3");
                CurrentZ = m.Z.ToString("F3");
                CurrentRx = m.Cartesian[3].ToString("F3");
                CurrentRy = m.Cartesian[4].ToString("F3");
                CurrentRz = m.Cartesian[5].ToString("F3");

                // 自动填入输入框
                InputX = CurrentX;
                InputY = CurrentY;
                InputZ = CurrentZ;
                InputRx = CurrentRx;
                InputRy = CurrentRy;
                InputRz = CurrentRz;

                var sb = new StringBuilder();
                string frameName = SelectedFrameType.Contains("用户") ? $"UF{SelectedFrameNumber}" : $"UT{SelectedFrameNumber}";
                sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] 读取{frameName}坐标成功");
                sb.AppendLine($"  位置(mm): X={m.X:F3}  Y={m.Y:F3}  Z={m.Z:F3}");
                sb.AppendLine($"  姿态(°): Rx={m.Cartesian[3]:F3}  Ry={m.Cartesian[4]:F3}  Rz={m.Cartesian[5]:F3}");
                OutputText = sb.ToString();
                LogVM.AddLog($"读取{frameName}坐标成功");
            }
            catch (Exception e)
            {
                string msg = "读取坐标失败: " + e.Message;
                OutputText = msg;
                LogVM.AddLog(msg);
            }
        }

        /// <summary>移动到指定位置</summary>
        private async void Move()
        {
            try
            {
                if (!_robot.IsConnected)
                {
                    OutputText = "未连接机器人，正在连接...";
                    await _robot.ConnectAsync(RobotIp);
                }

                if (!double.TryParse(InputX, out double x) ||
                    !double.TryParse(InputY, out double y) ||
                    !double.TryParse(InputZ, out double z) ||
                    !double.TryParse(InputRx, out double rx) ||
                    !double.TryParse(InputRy, out double ry) ||
                    !double.TryParse(InputRz, out double rz))
                {
                    OutputText = "输入坐标无效，请检查数值格式。";
                    return;
                }

                if (!int.TryParse(SpeedText, out int speed) || speed < 1 || speed > 100)
                {
                    OutputText = "速度请输入1~100之间的整数。";
                    return;
                }

                string frameName = SelectedFrameType.Contains("用户") ? $"UF{SelectedFrameNumber}" : $"UT{SelectedFrameNumber}";
                string motionType = SelectedMotionType.Contains("关节") ? "关节" : "直线";

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] 发送移动指令");
                sb.AppendLine($"  目标坐标系: {frameName}");
                sb.AppendLine($"  运动类型: {motionType}");
                sb.AppendLine($"  速度: {speed}%");
                sb.AppendLine($"  目标位置: X={x:F3}  Y={y:F3}  Z={z:F3}");
                sb.AppendLine($"  目标姿态: Rx={rx:F3}  Ry={ry:F3}  Rz={rz:F3}");

                // TODO: 调用YMConnect的移动指令
                // 目前先记录日志，实际移动指令需要根据YMConnect API实现
                sb.AppendLine("  指令已发送(待实现YMConnect移动API对接)");
                OutputText = sb.ToString();
                LogVM.AddLog($"发送移动指令到{frameName}: X={x:F3} Y={y:F3} Z={z:F3}");

                CommandHistory.Insert(0, $"[{DateTime.Now:HH:mm:ss}] 移动到{frameName} ({x:F1},{y:F1},{z:F1})");
            }
            catch (Exception e)
            {
                string msg = "移动指令失败: " + e.Message;
                OutputText = msg;
                LogVM.AddLog(msg);
            }
        }

        /// <summary>移动到原点(0,0,0)</summary>
        private async void MoveToOrigin()
        {
            InputX = "0";
            InputY = "0";
            InputZ = "0";
            InputRx = "0";
            InputRy = "0";
            InputRz = "0";
            Move();
        }

        /// <summary>停止运动</summary>
        private void Stop()
        {
            OutputText = "已发送停止指令";
            LogVM.AddLog("发送停止指令");
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}