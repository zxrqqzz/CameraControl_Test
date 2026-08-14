using System.Collections.Generic;

namespace CameraControl.Models
{
    /// <summary>
    /// 应用设置(自动保存/加载)
    /// 包含通讯管理设备、全局触发、机器人IP与二维坐标系标定数据。
    /// </summary>
    public class AppSettings
    {
        /// <summary>通讯管理设备列表</summary>
        public List<DeviceSettings> Devices { get; set; } = new List<DeviceSettings>();

        /// <summary>全局触发列表</summary>
        public List<TriggerSettings> Triggers { get; set; } = new List<TriggerSettings>();

        /// <summary>机器人IP</summary>
        public string RobotIp { get; set; } = "192.168.1.11";

        /// <summary>二维坐标系标定数据</summary>
        public CalibrationExportData Calibration { get; set; } = new CalibrationExportData();
    }

    /// <summary>通讯设备设置</summary>
    public class DeviceSettings
    {
        public string Name { get; set; } = "";
        public CommunicationType Type { get; set; }
        public string Ip { get; set; } = "";
        public int Port { get; set; }
    }

    /// <summary>全局触发设置</summary>
    public class TriggerSettings
    {
        public string TriggerText { get; set; } = "";
        public TriggerMatchMode MatchMode { get; set; } = TriggerMatchMode.Contains;
        public bool Enabled { get; set; } = true;
        public bool RecordRead { get; set; } = true;
        public bool RecordConvert { get; set; } = true;
        public bool ReadRobot { get; set; } = true;
        public bool Convert6Axis { get; set; } = true;
    }
}