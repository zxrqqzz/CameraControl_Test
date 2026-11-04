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

        /// <summary>设备管理里的相机列表</summary>
        public List<CameraDeviceSettings> Cameras { get; set; } = new List<CameraDeviceSettings>();

        /// <summary>设备扫描的选项(勾选状态会记住)</summary>
        public ScanSettings Scan { get; set; } = new ScanSettings();

        /// <summary>全局触发列表</summary>
        public List<TriggerSettings> Triggers { get; set; } = new List<TriggerSettings>();

        /// <summary>机器人IP</summary>
        public string RobotIp { get; set; } = "192.168.1.11";

        /// <summary>用户坐标系编号(UF1~UF63)</summary>
        public int UserFrameNumber { get; set; } = 1;

        /// <summary>二维坐标系标定数据</summary>
        public CalibrationExportData Calibration { get; set; } = new CalibrationExportData();
    }

    /// <summary>设备管理里的相机设置(用于启动时恢复设备列表)</summary>
    public class CameraDeviceSettings
    {
        public string Name { get; set; } = "";
        public string Vendor { get; set; } = "";
        public string Model { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string Ip { get; set; } = "";
        public string Mac { get; set; } = "";
        public string DevicePath { get; set; } = "";
        public string InterfaceText { get; set; } = "";
        public DeviceSource Source { get; set; } = DeviceSource.Manual;
        public bool IsDetectedOnly { get; set; }

        /// <summary>是否红外摄像头(Windows Hello 专用，不取流)</summary>
        public bool IsInfraredOnly { get; set; }

        /// <summary>
        /// 是否允许把该设备的图像保存到磁盘。
        /// null = 跟随默认策略(UVC 默认禁止，见 ImagePersistenceGuard)。
        /// 必须记住用户的选择，否则每次启动都会悄悄恢复成"禁止"或"允许"。
        /// </summary>
        public bool? AllowImagePersist { get; set; }
    }

    /// <summary>设备扫描选项(记住上次勾选的扫描范围)</summary>
    public class ScanSettings
    {
        /// <summary>扫描海康 MVS SDK 设备(GigE / USB3 Vision / GenTL)</summary>
        public bool EnableHikSdk { get; set; } = true;

        /// <summary>扫描本机 UVC 摄像头(含笔记本自带摄像头)</summary>
        public bool EnableUvc { get; set; } = true;

        /// <summary>局域网被动检测(读 ARP 表，零网络流量)</summary>
        public bool EnableLanPassive { get; set; } = true;

        /// <summary>局域网 ONVIF 主动发现(会发送一条标准组播报文，默认关闭)</summary>
        public bool EnableLanOnvif { get; set; }

        /// <summary>ONVIF 发现的等待时长(毫秒)</summary>
        public int OnvifTimeoutMs { get; set; } = 3000;
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
        public bool Record2D { get; set; } = true;
        public bool ReadRobot { get; set; } = true;
        public bool Convert2D { get; set; } = true;
        public bool Convert6Axis { get; set; } = true;
        public bool Continuous { get; set; }
        public int ExecCount { get; set; } = 1;
    }
}