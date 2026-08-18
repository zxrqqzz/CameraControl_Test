using CameraControl.Models;
using CameraControl.Services;
using GalaSoft.MvvmLight.Command;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CameraControl.ViewModels
{
    /// <summary>
    /// 用户坐标系直接读取 ViewModel
    /// 直接读取示教器中已建立好的用户直角坐标系(UF)，
    /// 读取到的 X/Y 即二维坐标，避免三点手动标定与坐标转换引入的累积误差。
    /// </summary>
    public class UserCoordinateViewModel : INotifyPropertyChanged
    {
        private readonly RobotService _robot;
        private bool _isConnecting;

        public LogViewModel LogVM { get; } = App.LogVM;

        /// <summary>机器人IP(与标定界面共用，自动保存)</summary>
        public string RobotIp
        {
            get => App.Calibration.RobotIp;
            set { if (App.Calibration.RobotIp != value) { App.Calibration.RobotIp = value; OnPropertyChanged(); } }
        }

        /// <summary>用户坐标系编号(UF1~UF63)</summary>
        public int UserFrameNumber { get; set; } = 1;

        private string _userFrameText = "1";
        public string UserFrameText
        {
            get => _userFrameText;
            set
            {
                if (_userFrameText == value) return;
                _userFrameText = value;
                OnPropertyChanged();
                if (int.TryParse(value, out int n)) UserFrameNumber = n;
            }
        }

        /// <summary>当前读取到的二维坐标 X(突出显示)</summary>
        private string _currentX2Text = "--";
        public string CurrentX2Text
        {
            get => _currentX2Text;
            set { _currentX2Text = value; OnPropertyChanged(); }
        }

        /// <summary>当前读取到的二维坐标 Y(突出显示)</summary>
        private string _currentY2Text = "--";
        public string CurrentY2Text
        {
            get => _currentY2Text;
            set { _currentY2Text = value; OnPropertyChanged(); }
        }

        /// <summary>当前读取到的旋转角 Rz(绕用户Z轴, 突出显示)</summary>
        private string _currentRzText = "--";
        public string CurrentRzText
        {
            get => _currentRzText;
            set { _currentRzText = value; OnPropertyChanged(); }
        }

        /// <summary>输出文本框内容(可选择复制)</summary>
        private string _outputText = "";
        public string OutputText
        {
            get => _outputText;
            set { _outputText = value; OnPropertyChanged(); }
        }

        /// <summary>手动输入二维坐标 X2</summary>
        private string _inputX2 = "0";
        public string InputX2
        {
            get => _inputX2;
            set { _inputX2 = value; OnPropertyChanged(); }
        }

        /// <summary>手动输入二维坐标 Y2</summary>
        private string _inputY2 = "0";
        public string InputY2
        {
            get => _inputY2;
            set { _inputY2 = value; OnPropertyChanged(); }
        }

        /// <summary>手动输入旋转角 Rz(绕用户Z轴)</summary>
        private string _inputRz = "0";
        public string InputRz
        {
            get => _inputRz;
            set { _inputRz = value; OnPropertyChanged(); }
        }

        /// <summary>二维坐标记录(含旋转角, 格式 "X：数据，Y：数据，Rz：数据°")</summary>
        public ObservableCollection<UserCoordinateRecord> User2DRecords { get; } = new ObservableCollection<UserCoordinateRecord>();

        private bool _isRecording2D;
        /// <summary>记录二维坐标开关(按下开始记录，再次点击停止并保存)</summary>
        public bool IsRecording2D
        {
            get => _isRecording2D;
            set
            {
                if (_isRecording2D == value) return;
                _isRecording2D = value;
                OnPropertyChanged();
                if (value)
                    LogVM.AddLog("已开启记录: 之后读取的标定点坐标(含旋转角)将被存储，再次点击\"记录坐标\"保存。");
                else
                    SaveRecord2DData();
            }
        }

        public ICommand ConnectCommand { get; }
        public ICommand ReadUserFrameCommand { get; }
        public ICommand Convert2DToPendantCommand { get; }
        public ICommand ClearCommand { get; }

        public UserCoordinateViewModel(RobotService robot)
        {
            _robot = robot;
            ConnectCommand = new RelayCommand(Connect);
            ReadUserFrameCommand = new RelayCommand(ReadUserFrame);
            Convert2DToPendantCommand = new RelayCommand(Convert2DToPendant);
            ClearCommand = new RelayCommand(Clear);
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

        /// <summary>
        /// 读取当前位置在用户坐标系(UF)中的坐标，直接作为二维坐标使用。
        /// </summary>
        private async void ReadUserFrame()
        {
            try
            {
                if (UserFrameNumber < 1 || UserFrameNumber > 63)
                {
                    string msg = "用户坐标系编号无效，请输入1~63。";
                    OutputText = msg;
                    LogVM.AddLog(msg);
                    return;
                }

                if (!_robot.IsConnected)
                {
                    AppendOutput("未连接机器人，自动尝试连接 ...");
                    LogVM.AddLog("未连接机器人，自动尝试连接 ...");
                    await _robot.ConnectAsync(RobotIp);
                }

                var m = await _robot.ReadPositionInUserFrameAsync((byte)UserFrameNumber);

                // 用户坐标系中的 X/Y 即二维坐标，直接读取，无任何转换误差
                double x2 = m.X;
                double y2 = m.Y;
                double rz = m.Cartesian[5];   // 绕用户Z轴(垂直于用户坐标平面)的旋转角
                CurrentX2Text = x2.ToString("F3");
                CurrentY2Text = y2.ToString("F3");
                CurrentRzText = rz.ToString("F3");
                InputX2 = x2.ToString("F3");
                InputY2 = y2.ToString("F3");
                InputRz = rz.ToString("F3");

                if (IsRecording2D)
                    User2DRecords.Add(new UserCoordinateRecord { X = x2, Y = y2, Rz = rz });

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] 读取用户坐标(UF{UserFrameNumber})成功");
                sb.AppendLine($"  二维坐标: X2={x2:F3}  Y2={y2:F3}");
                sb.AppendLine($"  旋转角(绕用户Z轴): Rz={rz:F3}°   (Rx={m.Cartesian[3]:F3}°  Ry={m.Cartesian[4]:F3}°)");
                sb.AppendLine($"  用户直角坐标(mm/°): X={m.X:F3}  Y={m.Y:F3}  Z={m.Z:F3}  " +
                              $"Rx={m.Cartesian[3]:F3}  Ry={m.Cartesian[4]:F3}  Rz={m.Cartesian[5]:F3}");
                sb.AppendLine($"  六轴关节角(°): S={m.Ax1:F3}  L={m.Ax2:F3}  U={m.Ax3:F3}  " +
                              $"R={m.Ax4:F3}  B={m.Ax5:F3}  T={m.Ax6:F3}");
                string warn = CheckRange(x2, y2);
                if (warn.Length > 0) sb.AppendLine("  " + warn);
                if (IsRecording2D) sb.AppendLine("  已记录标定点(含旋转角)。");
                OutputText = sb.ToString();
                LogVM.AddLog($"读取用户坐标(UF{UserFrameNumber})成功: X2={x2:F3}  Y2={y2:F3}  Rz={rz:F3}°");
            }
            catch (Exception e)
            {
                string msg = "读取用户坐标失败: " + e.Message;
                OutputText = msg;
                LogVM.AddLog(msg);
            }
        }

        /// <summary>
        /// 输入二维坐标(X2,Y2)，取当前用户坐标系姿态，输出可在示教器直接运行的
        /// 用户直角坐标(X,Y,Z,Rx,Ry,Rz)，无需基坐标转换，避免中间误差。
        /// </summary>
        private async void Convert2DToPendant()
        {
            try
            {
                if (UserFrameNumber < 1 || UserFrameNumber > 63)
                {
                    string msg = "用户坐标系编号无效，请输入1~63。";
                    OutputText = msg;
                    LogVM.AddLog(msg);
                    return;
                }

                if (!double.TryParse(InputX2, out double x2) || !double.TryParse(InputY2, out double y2))
                {
                    string msg = "二维坐标输入无效，请输入数值。";
                    OutputText = msg;
                    LogVM.AddLog(msg);
                    return;
                }

                // Rz 未填/无效时回退到当前姿态
                bool hasRz = double.TryParse(InputRz, out double rz);

                if (!_robot.IsConnected)
                {
                    AppendOutput("未连接机器人，自动尝试连接 ...");
                    LogVM.AddLog("未连接机器人，自动尝试连接 ...");
                    await _robot.ConnectAsync(RobotIp);
                }

                // 读取当前用户坐标系姿态，取 Z 与朝向作为目标姿态
                var cur = await _robot.ReadPositionInUserFrameAsync((byte)UserFrameNumber);
                if (!hasRz) rz = cur.Cartesian[5];

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] 二维坐标 -> 用户直角坐标(UF{UserFrameNumber})");
                sb.AppendLine($"  输入二维坐标: X2={x2:F3}  Y2={y2:F3}");
                sb.AppendLine($"  输入旋转角: Rz={rz:F3}°  {(hasRz ? "(使用输入值)" : "(未输入,取当前姿态)")}");
                sb.AppendLine($"  用户直角坐标(mm/°): X={x2:F3}  Y={y2:F3}  Z={cur.Z:F3}  " +
                              $"Rx={cur.Cartesian[3]:F3}  Ry={cur.Cartesian[4]:F3}  Rz={rz:F3}");
                sb.AppendLine($"  旋转角(绕用户Z轴): Rz={rz:F3}°");
                sb.AppendLine("  提示: 在示教器中选择该用户坐标系(UF)，用此直角坐标即可定位到该二维点，无需额外转换。");
                OutputText = sb.ToString();
                LogVM.AddLog("二维坐标转换为用户直角坐标成功");
            }
            catch (Exception e)
            {
                string msg = "坐标转换失败: " + e.Message;
                OutputText = msg;
                LogVM.AddLog(msg);
            }
        }

        /// <summary>清空二维坐标记录</summary>
        private void Clear()
        {
            User2DRecords.Clear();
            CurrentX2Text = "--";
            CurrentY2Text = "--";
            CurrentRzText = "--";
            OutputText = "已清空标定点记录(含旋转角)。";
            LogVM.AddLog("已清空标定点记录");
        }

        /// <summary>启动时恢复用户坐标系编号设置</summary>
        public void RestoreSettings(int userFrameNumber)
        {
            if (userFrameNumber < 1) userFrameNumber = 1;
            if (userFrameNumber > 63) userFrameNumber = 63;
            UserFrameText = userFrameNumber.ToString();
        }

        /// <summary>
        /// 保存标定点记录为 txt 文件(每行格式: X：数据，Y：数据，Rz：数据°)
        /// 无论保存成功、失败还是取消，都会清空记录。
        /// </summary>
        private void SaveRecord2DData()
        {
            if (User2DRecords.Count == 0)
            {
                string msg = "没有可保存的标定点数据。";
                OutputText = msg;
                LogVM.AddLog(msg);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "保存标定点(含旋转角)",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = ".txt",
                FileName = $"标定点_UF{UserFrameNumber}_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var sb = new StringBuilder();
                    foreach (var r in User2DRecords)
                        sb.AppendLine(r.Display);
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));

                    string msg = $"标定点已保存({User2DRecords.Count}条): {dlg.FileName}";
                    OutputText = msg;
                    LogVM.AddLog(msg);
                }
                catch (Exception e)
                {
                    string msg = $"保存标定点失败: {e.Message}";
                    OutputText = msg;
                    LogVM.AddLog(msg);
                }
            }
            else
            {
                string msg = "取消保存标定点，记录已清空。";
                OutputText = msg;
                LogVM.AddLog(msg);
            }

            User2DRecords.Clear();
        }

        /// <summary>校验二维坐标是否在设定取值范围内，超出返回警告文本</summary>
        private string CheckRange(double x2, double y2)
        {
            var s = App.Calibration.Settings;
            if (x2 < 0 || x2 > s.BoardWidthMm || y2 < 0 || y2 > s.BoardHeightMm)
            {
                return $"警告: 二维坐标 X2={x2:F3}, Y2={y2:F3} 超出标定板取值范围 " +
                       $"[X2:0~{s.BoardWidthMm:F1}mm, Y2:0~{s.BoardHeightMm:F1}mm]。";
            }
            return "";
        }

        private void AppendOutput(string line)
        {
            OutputText = $"[{DateTime.Now:HH:mm:ss.fff}] {line}\r\n" + OutputText;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
