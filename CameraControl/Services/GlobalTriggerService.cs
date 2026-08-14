using CameraControl.Models;
using CameraControl.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace CameraControl.Services
{
    /// <summary>
    /// 全局触发服务
    /// 订阅通讯设备接收的数据，按触发列表匹配字符串；命中后执行触发配置
    /// (记录读取/转换坐标 → 读取机器人位置 → 转换为6轴坐标)，并通过接收设备回传数据。
    /// </summary>
    public class GlobalTriggerService
    {
        private readonly CommunicationService _communication;
        private readonly CalibrationViewModel _calibration;
        private bool _restoring;

        /// <summary>触发列表</summary>
        public ObservableCollection<StringTrigger> Triggers { get; } = new ObservableCollection<StringTrigger>();

        public GlobalTriggerService(CommunicationService communication, CalibrationViewModel calibration)
        {
            _communication = communication;
            _calibration = calibration;
            _communication.DataReceived += OnDataReceived;
        }

        /// <summary>添加触发</summary>
        public void AddTrigger(string triggerText, TriggerMatchMode matchMode)
        {
            var t = new StringTrigger { TriggerText = triggerText, MatchMode = matchMode };
            t.PropertyChanged += OnTriggerPropertyChanged;
            Triggers.Add(t);
            Reindex();
            App.LogVM.AddLog($"已添加全局触发: [{t.Index}] \"{t.TriggerText}\" ({t.MatchModeText})");
            TrySave();
        }

        /// <summary>移除触发</summary>
        public void RemoveTrigger(StringTrigger trigger)
        {
            if (!Triggers.Contains(trigger)) return;
            trigger.PropertyChanged -= OnTriggerPropertyChanged;
            Triggers.Remove(trigger);
            Reindex();
            App.LogVM.AddLog($"已删除全局触发: \"{trigger.TriggerText}\"");
            TrySave();
        }

        /// <summary>启动时从保存的设置恢复触发列表</summary>
        public void RestoreFromSettings(IEnumerable<TriggerSettings> triggers)
        {
            _restoring = true;
            try
            {
                foreach (var ts in triggers)
                {
                    if (string.IsNullOrWhiteSpace(ts.TriggerText)) continue;
                    var t = new StringTrigger
                    {
                        TriggerText = ts.TriggerText,
                        MatchMode = ts.MatchMode,
                        Enabled = ts.Enabled,
                        RecordRead = ts.RecordRead,
                        RecordConvert = ts.RecordConvert,
                        Record2D = ts.Record2D,
                        ReadRobot = ts.ReadRobot,
                        Convert2D = ts.Convert2D,
                        Convert6Axis = ts.Convert6Axis,
                        Continuous = ts.Continuous,
                        ExecCountText = Math.Max(1, ts.ExecCount).ToString()
                    };
                    t.PropertyChanged += OnTriggerPropertyChanged;
                    Triggers.Add(t);
                }
                Reindex();
            }
            finally { _restoring = false; }
        }

        /// <summary>触发配置变化时自动保存</summary>
        private void OnTriggerPropertyChanged(object? sender, PropertyChangedEventArgs e) => TrySave();

        /// <summary>自动保存当前设置</summary>
        private void TrySave()
        {
            if (!_restoring) App.Settings?.SaveAll();
        }

        /// <summary>手动测试触发(仅执行并记录结果，不通过网络发送)</summary>
        public async Task TestAsync(StringTrigger trigger)
        {
            try
            {
                App.LogVM.AddLog($"手动测试触发: [{trigger.Index}] \"{trigger.TriggerText}\"");
                var responses = await ExecuteSequenceAsync(trigger);
                foreach (var r in responses)
                    App.LogVM.AddLog("触发结果: " + r);
            }
            catch (Exception e)
            {
                App.LogVM.AddLog("手动触发失败: " + e.Message);
            }
        }

        private void Reindex()
        {
            int i = 1;
            foreach (var t in Triggers) t.Index = i++;
        }

        private void OnDataReceived(CommunicationDevice device, string data, object? channel)
        {
            if (Application.Current == null) return;
            Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await ProcessReceivedDataAsync(device, data, channel); }
                catch (Exception e) { App.LogVM.AddLog("全局触发处理异常: " + e.Message); }
            }));
        }

        private async Task ProcessReceivedDataAsync(CommunicationDevice device, string data, object? channel)
        {
            foreach (var t in Triggers.ToArray())
            {
                if (!t.Enabled) continue;
                if (!Matches(t, data)) continue;
                App.LogVM.AddLog($"全局触发命中: 设备={device.Name}, 触发字符=\"{t.TriggerText}\", " +
                                 $"匹配模式={t.MatchModeText}, 接收数据=\"{data}\"");
                await RunSequenceAsync(t, device, channel);
            }
        }

        private bool Matches(StringTrigger t, string data)
        {
            if (string.IsNullOrEmpty(t.TriggerText)) return false;
            switch (t.MatchMode)
            {
                case TriggerMatchMode.Contains: return data.Contains(t.TriggerText);
                case TriggerMatchMode.Equals: return data.Trim().Equals(t.TriggerText.Trim());
                case TriggerMatchMode.StartsWith: return data.TrimStart().StartsWith(t.TriggerText);
                case TriggerMatchMode.EndsWith: return data.TrimEnd().EndsWith(t.TriggerText);
                case TriggerMatchMode.Regex:
                    try { return Regex.IsMatch(data, t.TriggerText); }
                    catch { return false; }
                default: return false;
            }
        }

        /// <summary>触发时执行序列并依次回传数据</summary>
        private async Task RunSequenceAsync(StringTrigger trigger, CommunicationDevice device, object? channel)
        {
            try
            {
                var responses = await ExecuteSequenceAsync(trigger);
                foreach (var r in responses)
                    SendBack(device, r, channel);
            }
            catch (Exception e)
            {
                App.LogVM.AddLog("全局触发执行失败: " + e.Message);
                SendBack(device, "触发执行失败:" + e.Message, channel);
            }
        }

        /// <summary>
        /// 执行触发配置:
        /// 1. 执行三个"保存记录坐标"按钮(记录读取坐标、记录转换坐标、记录二维坐标)
        /// 2. 依次执行"读取机器人位置"、"转为二维坐标"、"转换为6轴坐标"
        /// 3. 若开启"连续执行"，按设定次数重复执行第2步
        /// 返回各步骤的统一格式数据(机器人坐标 / 二维坐标 / 转换后的6轴坐标)。
        /// </summary>
        private async Task<List<string>> ExecuteSequenceAsync(StringTrigger trigger)
        {
            var responses = new List<string>();

            // 1. 三个用于保存记录坐标的按钮(开启记录)
            if (trigger.RecordRead) _calibration.IsRecordingRead = true;
            if (trigger.RecordConvert) _calibration.IsRecordingConvert = true;
            if (trigger.Record2D) _calibration.IsRecording2D = true;

            // 2. 连续执行次数
            int count = trigger.Continuous ? trigger.ExecCount : 1;
            bool needRead = trigger.ReadRobot || trigger.Convert2D || trigger.Convert6Axis;

            for (int i = 0; i < count; i++)
            {
                // 读取机器人位置
                RobotModel? robot = null;
                if (needRead)
                    robot = await _calibration.ReadRobotAndRecordAsync();

                // 转为二维坐标
                if (trigger.Convert2D && robot != null)
                {
                    try
                    {
                        var (x2, y2) = _calibration.ConvertTo2D(robot);
                        responses.Add("二维坐标:" + Format2D(x2, y2));
                    }
                    catch (Exception e)
                    {
                        App.LogVM.AddLog("转为二维坐标失败: " + e.Message);
                    }
                }

                // 转换为6轴坐标
                RobotModel? converted = null;
                if (trigger.Convert6Axis && robot != null)
                    converted = await _calibration.ConvertCurrentTo6AxisAsync(robot);

                // 依次回传数据
                if (trigger.ReadRobot && robot != null)
                    responses.Add("机器人坐标:" + FormatPosition(robot));
                if (trigger.Convert6Axis && converted != null)
                    responses.Add("转换后的6轴坐标:" + FormatPosition(converted));
            }

            return responses;
        }

        private void SendBack(CommunicationDevice device, string text, object? channel)
        {
            try { _communication.Send(device, text, channel); }
            catch (Exception e) { App.LogVM.AddLog("回传数据失败: " + e.Message); }
        }

        /// <summary>统一的完整坐标格式: 直角坐标 + 六轴关节角(示教器可直接使用)</summary>
        private static string FormatPosition(RobotModel m)
        {
            return $"X={m.X:F3},Y={m.Y:F3},Z={m.Z:F3}," +
                   $"Rx={m.Cartesian[3]:F3},Ry={m.Cartesian[4]:F3},Rz={m.Cartesian[5]:F3}," +
                   $"S={m.Ax1:F3},L={m.Ax2:F3},U={m.Ax3:F3},R={m.Ax4:F3},B={m.Ax5:F3},T={m.Ax6:F3}";
        }

        /// <summary>二维坐标格式: X：数据，Y：数据</summary>
        private static string Format2D(double x2, double y2) => $"X：{x2:F3}，Y：{y2:F3}";
    }
}