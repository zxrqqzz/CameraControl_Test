using CameraControl.Models;
using CameraControl.Services;
using GalaSoft.MvvmLight.Command;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Win32;

namespace CameraControl.ViewModels
{
    /// <summary>
    /// 标定界面 ViewModel
    /// 通过3次从机器人读取的坐标(原点、X轴点、Y轴点)确定一个平面，
    /// 建立二维直角坐标系，并将机器人的六轴(直角)信息转换为二维坐标。
    /// </summary>
    public class CalibrationViewModel : INotifyPropertyChanged
    {
        private readonly RobotService _robot;
        private readonly RelayCommand _calculateCommand;
        private readonly RelayCommand _convertCurrentCommand;

        public LogViewModel LogVM { get; } = App.LogVM;

        /// <summary>4个标定点</summary>
        public ObservableCollection<CalibrationPoint> Points { get; } = new ObservableCollection<CalibrationPoint>();

        /// <summary>机器人IP(安川YMConnect协议使用固定端口，无需填写端口)</summary>
        public string RobotIp { get; set; } = "192.168.1.11";

        /// <summary>X方向取值范围上限(mm)，物料板默认 48cm=480mm</summary>
        private string _boardWidthText = "480";
        public string BoardWidthText
        {
            get => _boardWidthText;
            set { _boardWidthText = value; OnPropertyChanged(); }
        }

        /// <summary>Y方向取值范围上限(mm)，物料板默认 48cm=480mm</summary>
        private string _boardHeightText = "480";
        public string BoardHeightText
        {
            get => _boardHeightText;
            set { _boardHeightText = value; OnPropertyChanged(); }
        }

        /// <summary>坐标精度(mm)，默认 0.1(亚毫米，满足mm级)</summary>
        private string _precisionText = "0.1";
        public string PrecisionText
        {
            get => _precisionText;
            set { _precisionText = value; OnPropertyChanged(); }
        }

        /// <summary>解析当前设置(输入非法时回退默认值)</summary>
        private CalibrationSettings CurrentSettings => new CalibrationSettings
        {
            BoardWidthMm = ParseSetting(BoardWidthText, 480, 1, 10000),
            BoardHeightMm = ParseSetting(BoardHeightText, 480, 1, 10000),
            PrecisionMm = ParseSetting(PrecisionText, 0.1, 0.0001, 100)
        };

        private static double ParseSetting(string text, double def, double min, double max)
        {
            if (double.TryParse(text, out double v) && v >= min && v <= max) return v;
            return def;
        }

        /// <summary>将坐标值按精度量化(取整到精度的整数倍)</summary>
        private double Round2D(double value)
        {
            double p = CurrentSettings.PrecisionMm;
            return Math.Round(value / p) * p;
        }

        /// <summary>校验二维坐标是否在设定取值范围内，超出返回警告文本</summary>
        private string CheckRange(double x2, double y2)
        {
            var s = CurrentSettings;
            double xr = Round2D(x2), yr = Round2D(y2);
            if (xr < 0 || xr > s.BoardWidthMm || yr < 0 || yr > s.BoardHeightMm)
            {
                return $"警告: 二维坐标 X2={xr:F3}, Y2={yr:F3} 超出标定板取值范围 " +
                       $"[X2:0~{s.BoardWidthMm:F1}mm, Y2:0~{s.BoardHeightMm:F1}mm]。";
            }
            return "";
        }

        private CalibrationFrame _frame = new CalibrationFrame();

        /// <summary>恢复设置时抑制自动保存，避免启动时写回</summary>
        private bool _suppressSave;

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

        /// <summary>机器人实际位置显示文本</summary>
        private string _robotOutputText = "";
        public string RobotOutputText
        {
            get => _robotOutputText;
            set { _robotOutputText = value; OnPropertyChanged(); }
        }

        /// <summary>已读取的坐标记录(实际坐标)</summary>
        public ObservableCollection<CoordinateRecord> ReadRecords { get; } = new ObservableCollection<CoordinateRecord>();

        /// <summary>转换后的坐标记录</summary>
        public ObservableCollection<CoordinateRecord> ConvertRecords { get; } = new ObservableCollection<CoordinateRecord>();

        private bool _isRecordingRead;
        /// <summary>记录读取坐标开关(按下开始记录，再次点击停止并保存)</summary>
        public bool IsRecordingRead
        {
            get => _isRecordingRead;
            set
            {
                if (_isRecordingRead == value) return;
                _isRecordingRead = value;
                OnPropertyChanged();
                if (value)
                    LogVM.AddLog("已开启记录: 之后读取的位置坐标将被存储，再次点击\"记录读取坐标\"保存。");
                else
                    SaveReadData();
            }
        }

        private bool _isRecordingConvert;
        /// <summary>记录转换坐标开关(按下开始记录，再次点击停止并保存)</summary>
        public bool IsRecordingConvert
        {
            get => _isRecordingConvert;
            set
            {
                if (_isRecordingConvert == value) return;
                _isRecordingConvert = value;
                OnPropertyChanged();
                if (value)
                    LogVM.AddLog("已开启记录: 之后转换的坐标将被存储，再次点击\"记录转换坐标\"保存。");
                else
                    SaveConvertData();
            }
        }

        public ICommand ConnectCommand { get; }
        public ICommand ReadRobotCommand { get; }
        public ICommand ReadPointCommand { get; }
        public ICommand CalculateCommand => _calculateCommand;
        public ICommand ConvertCurrentCommand => _convertCurrentCommand;
        public ICommand Convert2DTo6AxisCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand ImportCommand { get; }

        public CalibrationViewModel(RobotService robot)
        {
            _robot = robot;

            for (int i = 1; i <= 3; i++)
                Points.Add(new CalibrationPoint { Name = "点" + i });

            ConnectCommand = new RelayCommand(Connect);
            ReadRobotCommand = new RelayCommand(ReadRobot);
            ReadPointCommand = new RelayCommand<string>(ReadPoint);
            _calculateCommand = new RelayCommand(Calculate, CanCalculate);
            _convertCurrentCommand = new RelayCommand(ConvertCurrent, () => _frame.IsValid);
            Convert2DTo6AxisCommand = new RelayCommand(Convert2DTo6Axis);
            ClearCommand = new RelayCommand(Clear);
            ExportCommand = new RelayCommand(Export);
            ImportCommand = new RelayCommand(Import);

            // 取值范围/精度、机器人IP等设置变化时自动保存
            PropertyChanged += (_, e) =>
            {
                if (_suppressSave) return;
                switch (e.PropertyName)
                {
                    case nameof(RobotIp):
                    case nameof(BoardWidthText):
                    case nameof(BoardHeightText):
                    case nameof(PrecisionText):
                        App.Settings?.SaveAll();
                        break;
                }
            };
        }

        /// <summary>
        /// 读取机器人六个轴的位置信息(实际坐标)，并记录到读取坐标列表。
        /// </summary>
        private async void ReadRobot()
        {
            try
            {
                var m = await ReadRobotAndRecordAsync();

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
                if (_frame.IsValid)
                    sb.AppendLine($"二维坐标: X2={InputX2}  Y2={InputY2} (已自动填入输入框)");
                RobotOutputText = sb.ToString();
                LogVM.AddLog("读取机器人位置成功并记录");
            }
            catch (Exception e)
            {
                string msg = "读取机器人位置失败: " + e.Message;
                LogVM.AddLog(msg);
                RobotOutputText = msg;
            }
        }

        /// <summary>
        /// 读取机器人位置，若开启"记录读取坐标"则加入读取坐标列表。
        /// 供"读取机器人位置"按钮与全局触发共用。
        /// </summary>
        public async Task<RobotModel> ReadRobotAndRecordAsync()
        {
            if (!_robot.IsConnected)
            {
                LogVM.AddLog("未连接机器人，自动尝试连接 ...");
                await _robot.ConnectAsync(RobotIp);
            }

            var m = await _robot.ReadPositionAsync();

            var rec = new CoordinateRecord
            {
                X = m.X,
                Y = m.Y,
                Z = m.Z,
                Rx = m.Cartesian[3],
                Ry = m.Cartesian[4],
                Rz = m.Cartesian[5]
            };
            Array.Copy(m.Angle, rec.Angle, rec.Angle.Length);
            Array.Copy(m.Pulse, rec.Pulse, rec.Pulse.Length);
            if (_frame.IsValid)
            {
                var p = _frame.Project(m.X, m.Y, m.Z);
                rec.X2 = Round2D(p.X2);
                rec.Y2 = Round2D(p.Y2);
                InputX2 = rec.X2.ToString("F3");
                InputY2 = rec.Y2.ToString("F3");
            }
            if (IsRecordingRead) ReadRecords.Add(rec);
            return m;
        }

        /// <summary>
        /// 将机器人位置投影为二维坐标填入输入框，再反算标定平面上的直角坐标并转换为6轴关节角。
        /// 若开启"记录转换坐标"则加入转换坐标列表。供全局触发使用。
        /// </summary>
        public async Task<RobotModel> ConvertCurrentTo6AxisAsync(RobotModel robot)
        {
            if (!_frame.IsValid)
                throw new InvalidOperationException("尚未完成标定，无法进行坐标转换。请先在标定界面建立二维直角坐标系。");

            var p = _frame.Project(robot.X, robot.Y, robot.Z);
            double x2 = Round2D(p.X2);
            double y2 = Round2D(p.Y2);
            InputX2 = x2.ToString("F3");
            InputY2 = y2.ToString("F3");

            var (x, y, z) = _frame.InverseProject(x2, y2);

            var m = await _robot.ConvertCartesianToJointAsync(
                x, y, z, robot.Cartesian[3], robot.Cartesian[4], robot.Cartesian[5]);

            var rec = new CoordinateRecord
            {
                X2 = x2,
                Y2 = y2,
                X = x,
                Y = y,
                Z = z,
                Rx = robot.Cartesian[3],
                Ry = robot.Cartesian[4],
                Rz = robot.Cartesian[5]
            };
            Array.Copy(m.Angle, rec.Angle, rec.Angle.Length);
            Array.Copy(robot.Pulse, rec.Pulse, rec.Pulse.Length);
            if (IsRecordingConvert) ConvertRecords.Add(rec);
            return m;
        }

        private bool _isConnecting;

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

        private async void ReadPoint(string arg)
        {
            int index = 1;
            if (int.TryParse(arg, out int i)) index = i;
            if (index < 1 || index > 3) return;

            try
            {
                if (!_robot.IsConnected)
                {
                    AppendOutput("未连接机器人，自动尝试连接 ...");
                    LogVM.AddLog("未连接机器人，自动尝试连接 ...");
                    await _robot.ConnectAsync(RobotIp);
                }

                var m = await _robot.ReadPositionAsync();

                var pt = Points[index - 1];
                pt.X = m.X;
                pt.Y = m.Y;
                pt.Z = m.Z;
                pt.IsRead = true;

                string line = $"{pt.Name} 已读取: X={pt.X:F3}  Y={pt.Y:F3}  Z={pt.Z:F3}  " +
                              $"(六轴角 {m.Ax1:F2}/{m.Ax2:F2}/{m.Ax3:F2}/{m.Ax4:F2}/{m.Ax5:F2}/{m.Ax6:F2}°)";
                AppendOutput(line);
                LogVM.AddLog(line);

                _calculateCommand.RaiseCanExecuteChanged();
                App.Settings?.SaveAll();
            }
            catch (Exception e)
            {
                string msg = "读取失败: " + e.Message;
                AppendOutput(msg);
                LogVM.AddLog(msg);
            }
        }

        private bool CanCalculate()
        {
            foreach (var p in Points)
                if (!p.IsRead) return false;
            return true;
        }

        private void Calculate()
        {
            if (Points.Count < 3) return;

            var p0 = Points[0]; // 原点
            var p1 = Points[1]; // X轴上的点
            var p2 = Points[2]; // Y轴上的点

            // X轴方向 = 点2 - 原点
            double ax = p1.X - p0.X, ay = p1.Y - p0.Y, az = p1.Z - p0.Z;
            double alen = Math.Sqrt(ax * ax + ay * ay + az * az);
            if (alen < 1e-6)
            {
                _frame = new CalibrationFrame();
                OutputText = "标定失败: 原点与X轴上的点重合，无法确定X轴方向。";
                _convertCurrentCommand.RaiseCanExecuteChanged();
                return;
            }
            double ex0 = ax / alen, ey0 = ay / alen, ez0 = az / alen;

            // Y轴方向 = 向量(点3-原点)中垂直于X轴的分量
            double wx = p2.X - p0.X, wy = p2.Y - p0.Y, wz = p2.Z - p0.Z;
            double proj = wx * ex0 + wy * ey0 + wz * ez0;
            double vx = wx - proj * ex0, vy = wy - proj * ey0, vz = wz - proj * ez0;
            double vlen = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            if (vlen < 1e-6)
            {
                _frame = new CalibrationFrame();
                OutputText = "标定失败: Y轴上的点在X轴上(三点共线)，无法确定Y轴方向。";
                _convertCurrentCommand.RaiseCanExecuteChanged();
                return;
            }
            double fx = vx / vlen, fy = vy / vlen, fz = vz / vlen;

            _frame = new CalibrationFrame
            {
                OriginX = p0.X, OriginY = p0.Y, OriginZ = p0.Z,
                XDirX = ex0, XDirY = ey0, XDirZ = ez0,
                YDirX = fx, YDirY = fy, YDirZ = fz,
                XScale = alen, YScale = vlen,
                IsValid = true
            };

            var sb = new StringBuilder();
            sb.AppendLine("========== 二维直角坐标系标定完成 ==========");
            sb.AppendLine($"原点(点1):     {p0.X:F3}, {p0.Y:F3}, {p0.Z:F3}");
            sb.AppendLine($"X轴方向(点1→点2): ({ex0:F3}, {ey0:F3}, {ez0:F3})");
            sb.AppendLine($"  X轴标定距离: {alen:F3} mm");
            sb.AppendLine($"Y轴方向(点1→点3垂直分量): ({fx:F3}, {fy:F3}, {fz:F3})");
            sb.AppendLine($"  Y轴标定距离: {vlen:F3} mm");

            var s = CurrentSettings;
            sb.AppendLine($"取值范围: X2∈[0, {s.BoardWidthMm:F1}] mm, Y2∈[0, {s.BoardHeightMm:F1}] mm");
            sb.AppendLine($"坐标精度: {s.PrecisionMm:0.###} mm (坐标输出按该精度取整)");
            if (alen > s.BoardWidthMm + 1e-6)
                sb.AppendLine($"警告: X轴标定距离 {alen:F1}mm 超出设定的板宽 {s.BoardWidthMm:F1}mm，请调整板宽或重新采集X轴点。");
            if (vlen > s.BoardHeightMm + 1e-6)
                sb.AppendLine($"警告: Y轴标定距离 {vlen:F1}mm 超出设定的板高 {s.BoardHeightMm:F1}mm，请调整板高或重新采集Y轴点。");
            sb.AppendLine("现在可以点击\"转换当前位姿\"将六轴信息转为2轴坐标，或点击\"导出坐标系\"保存本次标定结果。");
            OutputText = sb.ToString();

            _convertCurrentCommand.RaiseCanExecuteChanged();
            App.Settings?.SaveAll();
        }

        private async void ConvertCurrent()
        {
            try
            {
                if (!_robot.IsConnected)
                {
                    AppendOutput("未连接机器人，自动尝试连接 ...");
                    LogVM.AddLog("未连接机器人，自动尝试连接 ...");
                    await _robot.ConnectAsync(RobotIp);
                }

                var m = await _robot.ReadPositionAsync();
                var p = _frame.Project(m.X, m.Y, m.Z);
                double x2 = Round2D(p.X2);
                double y2 = Round2D(p.Y2);

                // 自动填入二维坐标手动输入处
                InputX2 = x2.ToString("F3");
                InputY2 = y2.ToString("F3");

                // 记录读取坐标(实际坐标)
                var rec = new CoordinateRecord
                {
                    X2 = x2,
                    Y2 = y2,
                    X = m.X,
                    Y = m.Y,
                    Z = m.Z,
                    Rx = m.Cartesian[3],
                    Ry = m.Cartesian[4],
                    Rz = m.Cartesian[5]
                };
                Array.Copy(m.Angle, rec.Angle, rec.Angle.Length);
                Array.Copy(m.Pulse, rec.Pulse, rec.Pulse.Length);
                if (IsRecordingRead) ReadRecords.Add(rec);

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] 当前六轴信息 -> 二维坐标");
                sb.AppendLine($"  六轴关节角(°): S={m.Ax1:F3} L={m.Ax2:F3} U={m.Ax3:F3} " +
                              $"R={m.Ax4:F3} B={m.Ax5:F3} T={m.Ax6:F3}");
                sb.AppendLine($"  六轴直角坐标(mm): X={m.X:F3} Y={m.Y:F3} Z={m.Z:F3} " +
                              $"Rx={m.Cartesian[3]:F3} Ry={m.Cartesian[4]:F3} Rz={m.Cartesian[5]:F3}");
                sb.AppendLine($"  => 二维坐标: X2={x2:F3}, Y2={y2:F3} (精度 {CurrentSettings.PrecisionMm:0.###}mm)");
                sb.AppendLine("  已自动填入手动输入框，可直接调整后点击\"转换为6轴坐标\"。");
                if (IsRecordingRead) sb.AppendLine("  已记录到读取坐标列表。");
                string warn = CheckRange(p.X2, p.Y2);
                if (warn.Length > 0) sb.AppendLine("  " + warn);
                OutputText = sb.ToString();
                LogVM.AddLog("转换当前位姿成功" + (IsRecordingRead ? "并记录" : ""));
            }
            catch (Exception e)
            {
                string msg = "转换失败: " + e.Message;
                AppendOutput(msg);
                LogVM.AddLog(msg);
            }
        }

        /// <summary>
        /// 手动输入二维坐标(X2,Y2)，反算标定平面上的三维直角坐标，
        /// 再通过机器人运动学逆解转换为6轴关节角。
        /// </summary>
        private async void Convert2DTo6Axis()
        {
            try
            {
                if (!_frame.IsValid)
                {
                    string msg = "尚未完成标定，无法进行坐标转换。请先执行第二步: 建立二维直角坐标系。";
                    OutputText = msg;
                    LogVM.AddLog(msg);
                    return;
                }

                if (!double.TryParse(InputX2, out double x2In) || !double.TryParse(InputY2, out double y2In))
                {
                    string msg = "二维坐标输入无效，请输入数值。";
                    OutputText = msg;
                    LogVM.AddLog(msg);
                    return;
                }

                // 按设定精度量化，并校验取值范围
                double x2 = Round2D(x2In);
                double y2 = Round2D(y2In);
                string warn = CheckRange(x2, y2);

                // 反投影到标定平面，得到三维直角坐标(X,Y,Z)
                var (x, y, z) = _frame.InverseProject(x2, y2);

                if (!_robot.IsConnected)
                {
                    AppendOutput("未连接机器人，自动尝试连接 ...");
                    LogVM.AddLog("未连接机器人，自动尝试连接 ...");
                    await _robot.ConnectAsync(RobotIp);
                }

                // 读取当前姿态，取当前朝向(Rx,Ry,Rz)作为目标姿态(标定不包含姿态信息)
                var cur = await _robot.ReadPositionAsync();
                var m = await _robot.ConvertCartesianToJointAsync(
                    x, y, z, cur.Cartesian[3], cur.Cartesian[4], cur.Cartesian[5]);

                // 记录转换坐标
                var rec = new CoordinateRecord
                {
                    X2 = x2,
                    Y2 = y2,
                    X = x,
                    Y = y,
                    Z = z,
                    Rx = cur.Cartesian[3],
                    Ry = cur.Cartesian[4],
                    Rz = cur.Cartesian[5]
                };
                Array.Copy(m.Angle, rec.Angle, rec.Angle.Length);
                Array.Copy(cur.Pulse, rec.Pulse, rec.Pulse.Length);
                if (IsRecordingConvert) ConvertRecords.Add(rec);

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] 二维坐标 -> 6轴坐标");
                sb.AppendLine($"  输入二维坐标: X2={x2:F3},  Y2={y2:F3} (精度 {CurrentSettings.PrecisionMm:0.###}mm)");
                if (warn.Length > 0) sb.AppendLine("  " + warn);
                sb.AppendLine($"  标定平面上的直角坐标(mm): X={x:F3}  Y={y:F3}  Z={z:F3}");
                sb.AppendLine($"  姿态取当前机器人姿态: Rx={cur.Cartesian[3]:F3}  Ry={cur.Cartesian[4]:F3}  Rz={cur.Cartesian[5]:F3}");
                sb.AppendLine($"  6轴关节角(°): S={m.Ax1:F3}  L={m.Ax2:F3}  U={m.Ax3:F3}  " +
                              $"R={m.Ax4:F3}  B={m.Ax5:F3}  T={m.Ax6:F3}");
                sb.AppendLine($"  可直接在示教器运行: X={x:F3} Y={y:F3} Z={z:F3} Rx={cur.Cartesian[3]:F3} Ry={cur.Cartesian[4]:F3} Rz={cur.Cartesian[5]:F3}  |  S={m.Ax1:F3} L={m.Ax2:F3} U={m.Ax3:F3} R={m.Ax4:F3} B={m.Ax5:F3} T={m.Ax6:F3}");
                OutputText = sb.ToString();
                LogVM.AddLog("二维坐标转换为6轴坐标成功");
            }
            catch (Exception e)
            {
                string msg = "坐标转换失败: " + e.Message;
                AppendOutput(msg);
                LogVM.AddLog(msg);
            }
        }

        private void Clear()
        {
            foreach (var p in Points)
            {
                p.X = p.Y = p.Z = 0;
                p.IsRead = false;
            }
            _frame = new CalibrationFrame();
            OutputText = "";
            _calculateCommand.RaiseCanExecuteChanged();
            _convertCurrentCommand.RaiseCanExecuteChanged();
            App.Settings?.SaveAll();
        }

        /// <summary>
        /// 保存已读取的坐标(实际坐标)为 txt 文件
        /// </summary>
        private void SaveReadData() => SaveRecords(ReadRecords, "读取坐标");

        /// <summary>
        /// 保存转换后的坐标为 txt 文件
        /// </summary>
        private void SaveConvertData() => SaveRecords(ConvertRecords, "转换坐标");

        /// <summary>
        /// 将记录保存为 txt 文件(带时间戳)。
        /// 无论保存成功、失败还是取消，松开按钮后都会清空记录。
        /// 数据包含六轴关节角与直角坐标(示教器可直接读取和运行)。
        /// </summary>
        private void SaveRecords(ObservableCollection<CoordinateRecord> records, string label)
        {
            if (records.Count == 0)
            {
                string msg = $"没有可保存的{label}数据。";
                OutputText = msg;
                LogVM.AddLog(msg);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = $"保存{label}",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = ".txt",
                FileName = $"{label}_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine(CoordinateRecord.FileHeader);
                    foreach (var r in records)
                        sb.AppendLine(r.ToFileLine());
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));

                    string msg = $"{label}已保存({records.Count}条): {dlg.FileName}";
                    OutputText = msg;
                    LogVM.AddLog(msg);
                }
                catch (Exception e)
                {
                    string msg = $"保存{label}失败: {e.Message}";
                    OutputText = msg;
                    LogVM.AddLog(msg);
                }
            }
            else
            {
                string msg = $"取消保存{label}，记录已清空。";
                OutputText = msg;
                LogVM.AddLog(msg);
            }

            // 松开按钮即清空记录(无论保存成功、失败还是取消)
            records.Clear();
        }

        /// <summary>
        /// 构建标定导出数据(取值范围/精度、标定点、二维坐标系)。
        /// 供"导出坐标系"与自动保存共用。
        /// </summary>
        public CalibrationExportData BuildExportData()
        {
            var s = CurrentSettings;
            return new CalibrationExportData
            {
                Version = 1,
                Settings = new CalibrationSettingsData
                {
                    BoardWidthMm = s.BoardWidthMm,
                    BoardHeightMm = s.BoardHeightMm,
                    PrecisionMm = s.PrecisionMm
                },
                Points = Points.Select(p => new CalibrationPointData
                {
                    Name = p.Name,
                    X = p.X,
                    Y = p.Y,
                    Z = p.Z
                }).ToList(),
                Frame = _frame.ToData()
            };
        }

        /// <summary>
        /// 从自动保存的设置恢复二维坐标系(机器人IP、取值范围/精度、标定点、坐标系)。
        /// 启动时调用，下次打开可直接使用，无需重复采集标定点。
        /// </summary>
        public void RestoreSettings(string robotIp, CalibrationExportData? data)
        {
            _suppressSave = true;
            try
            {
                if (!string.IsNullOrWhiteSpace(robotIp))
                    RobotIp = robotIp;

                if (data == null) return;

                if (data.Settings != null)
                {
                    BoardWidthText = data.Settings.BoardWidthMm.ToString("0.###");
                    BoardHeightText = data.Settings.BoardHeightMm.ToString("0.###");
                    PrecisionText = data.Settings.PrecisionMm.ToString("0.###");
                }

                for (int i = 0; i < Points.Count; i++)
                {
                    var p = Points[i];
                    if (data.Points != null && i < data.Points.Count)
                    {
                        var pd = data.Points[i];
                        if (!string.IsNullOrEmpty(pd.Name)) p.Name = pd.Name;
                        p.X = pd.X;
                        p.Y = pd.Y;
                        p.Z = pd.Z;
                        p.IsRead = true;
                    }
                    else
                    {
                        p.IsRead = false;
                    }
                }

                if (data.Frame != null)
                    _frame.FromData(data.Frame);
                else
                    _frame = new CalibrationFrame();

                _calculateCommand.RaiseCanExecuteChanged();
                _convertCurrentCommand.RaiseCanExecuteChanged();
            }
            finally
            {
                _suppressSave = false;
            }
        }

        /// <summary>
        /// 将已建立的二维坐标系导出为 JSON 文件，下次可直接导入使用，无需重复采集标定点。
        /// </summary>
        private async void Export()
        {
            if (!_frame.IsValid)
            {
                string msg = "尚未建立坐标系，无法导出。请先执行第二步: 建立二维直角坐标系。";
                OutputText = msg;
                LogVM.AddLog(msg);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "导出标定坐标系",
                Filter = "标定文件 (*.calib.json)|*.calib.json|JSON 文件 (*.json)|*.json",
                DefaultExt = ".calib.json",
                FileName = $"Calibration_{DateTime.Now:yyyyMMdd_HHmmss}.calib.json"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var data = BuildExportData();

                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(dlg.FileName, json, new UTF8Encoding(false));
                string msg = $"坐标系已导出: {dlg.FileName}";
                OutputText = msg;
                LogVM.AddLog(msg);
            }
            catch (Exception e)
            {
                string msg = "导出失败: " + e.Message;
                OutputText = msg;
                LogVM.AddLog(msg);
            }
        }

        /// <summary>
        /// 从导出的 JSON 文件导入坐标系，恢复取值范围/精度、标定点与二维坐标系。
        /// </summary>
        private async void Import()
        {
            var dlg = new OpenFileDialog
            {
                Title = "导入标定坐标系",
                Filter = "标定文件 (*.calib.json)|*.calib.json|JSON 文件 (*.json)|*.json"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                string json = await File.ReadAllTextAsync(dlg.FileName, new UTF8Encoding(false));
                var data = JsonSerializer.Deserialize<CalibrationExportData>(json);
                if (data == null || data.Frame == null || !data.Frame.IsValid)
                    throw new InvalidOperationException("文件格式不正确或缺少有效的坐标系数据。");

                // 恢复取值范围/精度设置
                if (data.Settings != null)
                {
                    BoardWidthText = data.Settings.BoardWidthMm.ToString("0.###");
                    BoardHeightText = data.Settings.BoardHeightMm.ToString("0.###");
                    PrecisionText = data.Settings.PrecisionMm.ToString("0.###");
                }

                // 恢复三个标定点
                for (int i = 0; i < Points.Count; i++)
                {
                    var p = Points[i];
                    if (data.Points != null && i < data.Points.Count)
                    {
                        var pd = data.Points[i];
                        if (!string.IsNullOrEmpty(pd.Name)) p.Name = pd.Name;
                        p.X = pd.X;
                        p.Y = pd.Y;
                        p.Z = pd.Z;
                        p.IsRead = true;
                    }
                    else
                    {
                        p.IsRead = false;
                    }
                }

                // 恢复坐标系
                _frame.FromData(data.Frame);
                _calculateCommand.RaiseCanExecuteChanged();
                _convertCurrentCommand.RaiseCanExecuteChanged();

                var s = CurrentSettings;
                var sb = new StringBuilder();
                sb.AppendLine($"坐标系导入成功: {dlg.FileName}");
                sb.AppendLine($"原点: {_frame.OriginX:F3}, {_frame.OriginY:F3}, {_frame.OriginZ:F3}");
                sb.AppendLine($"X轴方向: ({_frame.XDirX:F3}, {_frame.XDirY:F3}, {_frame.XDirZ:F3})  标定距离: {_frame.XScale:F3} mm");
                sb.AppendLine($"Y轴方向: ({_frame.YDirX:F3}, {_frame.YDirY:F3}, {_frame.YDirZ:F3})  标定距离: {_frame.YScale:F3} mm");
                sb.AppendLine($"取值范围: X2∈[0, {s.BoardWidthMm:F1}] mm, Y2∈[0, {s.BoardHeightMm:F1}] mm  精度: {s.PrecisionMm:0.###} mm");
                OutputText = sb.ToString();
                LogVM.AddLog("坐标系导入成功，无需重复采集标定点。");
                App.Settings?.SaveAll();
            }
            catch (Exception e)
            {
                string msg = "导入失败: " + e.Message;
                OutputText = msg;
                LogVM.AddLog(msg);
            }
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