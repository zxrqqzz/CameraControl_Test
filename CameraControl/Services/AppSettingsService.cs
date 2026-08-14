using CameraControl.Models;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CameraControl.Services
{
    /// <summary>
    /// 应用设置自动保存/加载服务
    /// 保存到 %LocalAppData%\CameraControl\settings.json，下次启动自动恢复。
    /// </summary>
    public class AppSettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _filePath;
        private readonly object _sync = new object();

        public AppSettingsService()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CameraControl");
            try { Directory.CreateDirectory(dir); } catch { }
            _filePath = Path.Combine(dir, "settings.json");
        }

        public AppSettings Load()
        {
            try
            {
                if (!File.Exists(_filePath)) return new AppSettings();
                string json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch { return new AppSettings(); }
        }

        private void Save(AppSettings settings)
        {
            lock (_sync)
            {
                try
                {
                    string json = JsonSerializer.Serialize(settings, JsonOptions);
                    File.WriteAllText(_filePath, json);
                }
                catch { }
            }
        }

        /// <summary>汇总当前全局状态并保存(通讯设备、全局触发、机器人IP、二维坐标系)</summary>
        public void SaveAll()
        {
            var settings = new AppSettings
            {
                RobotIp = App.Calibration.RobotIp,
                Calibration = App.Calibration.BuildExportData(),
                Devices = App.Communication.Devices.Select(d => new DeviceSettings
                {
                    Name = d.Name,
                    Type = d.Type,
                    Ip = d.Ip,
                    Port = d.Port
                }).ToList(),
                Triggers = App.GlobalTrigger.Triggers.Select(t => new TriggerSettings
                {
                    TriggerText = t.TriggerText,
                    MatchMode = t.MatchMode,
                    Enabled = t.Enabled,
                    RecordRead = t.RecordRead,
                    RecordConvert = t.RecordConvert,
                    ReadRobot = t.ReadRobot,
                    Convert6Axis = t.Convert6Axis
                }).ToList()
            };
            Save(settings);
        }

        /// <summary>启动时加载设置并恢复到全局服务</summary>
        public void LoadAll()
        {
            var s = Load();
            App.Communication.RestoreFromSettings(s.Devices);
            App.GlobalTrigger.RestoreFromSettings(s.Triggers);
            App.Calibration.RestoreSettings(s.RobotIp, s.Calibration);
        }
    }
}