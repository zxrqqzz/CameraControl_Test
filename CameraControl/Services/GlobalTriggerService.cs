using CameraControl.Models;
using CameraControl.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
            Triggers.Add(t);
            Reindex();
            App.LogVM.AddLog($"已添加全局触发: [{t.Index}] \"{t.TriggerText}\" ({t.MatchModeText})");
        }

        /// <summary>移除触发</summary>
        public void RemoveTrigger(StringTrigger trigger)
        {
            if (!Triggers.Contains(trigger)) return;
            Triggers.Remove(trigger);
            Reindex();
            App.LogVM.AddLog($"已删除全局触发: \"{trigger.TriggerText}\"");
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
        /// 1. 执行两个"保存记录坐标"按钮(记录读取坐标、记录转换坐标)
        /// 2. 依次执行"读取机器人位置"、"转换为6轴坐标"
        /// 返回格式: 机器人坐标:数据、转换后的6轴坐标：数据
        /// </summary>
        private async Task<List<string>> ExecuteSequenceAsync(StringTrigger trigger)
        {
            var responses = new List<string>();

            // 1. 两个用于保存记录坐标的按钮(开启记录)
            if (trigger.RecordRead) _calibration.IsRecordingRead = true;
            if (trigger.RecordConvert) _calibration.IsRecordingConvert = true;

            // 2. 依次执行读取机器人位置 → 转换为6轴坐标
            RobotModel? robot = null;
            RobotModel? converted = null;
            if (trigger.ReadRobot || trigger.Convert6Axis)
            {
                robot = await _calibration.ReadRobotAndRecordAsync();
                if (trigger.Convert6Axis)
                    converted = await _calibration.ConvertCurrentTo6AxisAsync(robot);
            }

            // 3. 依次回传数据
            if (trigger.ReadRobot && robot != null)
                responses.Add("机器人坐标:" + FormatRobot(robot));
            if (trigger.Convert6Axis && converted != null)
                responses.Add("转换后的6轴坐标：" + FormatConverted(converted));

            return responses;
        }

        private void SendBack(CommunicationDevice device, string text, object? channel)
        {
            try { _communication.Send(device, text, channel); }
            catch (Exception e) { App.LogVM.AddLog("回传数据失败: " + e.Message); }
        }

        private static string FormatRobot(RobotModel m)
        {
            return $"X={m.X:F3},Y={m.Y:F3},Z={m.Z:F3}," +
                   $"Rx={m.Cartesian[3]:F3},Ry={m.Cartesian[4]:F3},Rz={m.Cartesian[5]:F3}";
        }

        private static string FormatConverted(RobotModel m)
        {
            return $"S={m.Ax1:F3},L={m.Ax2:F3},U={m.Ax3:F3}," +
                   $"R={m.Ax4:F3},B={m.Ax5:F3},T={m.Ax6:F3}";
        }
    }
}