using System;

namespace CameraControl.Models
{
    /// <summary>
    /// 设备的发现来源。决定这个设备是"真的能连上取流"，还是"只是在网络上被识别到"。
    /// </summary>
    public enum DeviceSource
    {
        /// <summary>手工添加(没有任何来源信息)</summary>
        Manual = 0,

        /// <summary>海康 MVS SDK 枚举(GigE Vision / USB3 Vision / GenTL 采集卡)</summary>
        HikSdk = 1,

        /// <summary>UVC / USB 摄像头(系统注册表枚举，笔记本自带摄像头属于这一类)</summary>
        Uvc = 2,

        /// <summary>局域网被动检测(读本机 ARP 邻居表 + 厂商 OUI 匹配，不产生任何网络流量)</summary>
        LanPassive = 3,

        /// <summary>局域网 ONVIF WS-Discovery 发现(发送一条标准组播发现报文，不认证、不取流、不取图)</summary>
        LanOnvif = 4
    }

    /// <summary>DeviceSource 的显示名与能力判定</summary>
    public static class DeviceSources
    {
        /// <summary>来源的界面显示名</summary>
        public static string Text(this DeviceSource source) => source switch
        {
            DeviceSource.Manual => "手工添加",
            DeviceSource.HikSdk => "海康SDK枚举",
            DeviceSource.Uvc => "UVC摄像头",
            DeviceSource.LanPassive => "局域网被动检测",
            DeviceSource.LanOnvif => "ONVIF发现",
            _ => "未知来源"
        };

        /// <summary>
        /// 是否只是"检测到"。局域网扫描出来的设备只用于识别，
        /// 不应该被本程序自动连接、认证或拉取图像(避免碰到不属于自己的相机)。
        /// </summary>
        public static bool DetectedOnly(this DeviceSource source) =>
            source == DeviceSource.LanPassive || source == DeviceSource.LanOnvif;

        /// <summary>该来源的设备是否允许在本程序里连接并取流</summary>
        public static bool Connectable(this DeviceSource source) => !source.DetectedOnly();
    }

    /// <summary>
    /// 设备去重键。同一个物理设备重复扫描时应落在同一个键上，避免设备列表被填满。
    /// 手工添加的设备不参与去重(返回空字符串)。
    /// </summary>
    public static class DeviceDedupe
    {
        /// <summary>构造去重键；无法构造时返回空字符串(表示不参与去重)</summary>
        public static string BuildKey(
            DeviceSource source, string? serialNumber, string? devicePath, string? mac, string? ip, string? name)
        {
            switch (source)
            {
                case DeviceSource.HikSdk:
                    // 序列号最稳定；其次是设备唯一标识；再退到 MAC / IP
                    return "hik:" + First(serialNumber, devicePath, NormalizeMac(mac), ip);

                case DeviceSource.Uvc:
                    // Windows 设备实例路径是稳定的，名称只作兜底
                    return "uvc:" + First(devicePath, name);

                case DeviceSource.LanPassive:
                case DeviceSource.LanOnvif:
                    // 同一条网线上的设备以 MAC 为准；没有 MAC 时退到 IP
                    return "lan:" + First(NormalizeMac(mac), ip);

                default:
                    // 手工设备不参与去重
                    return string.Empty;
            }
        }

        /// <summary>
        /// MAC 统一格式: 去掉空格、分隔符统一成冒号、转大写。
        /// 否则 "4C:BD:8F:..." 和 "4c-bd-8f-..." 会被当成两台设备。
        /// </summary>
        public static string NormalizeMac(string? mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return string.Empty;
            return mac.Trim()
                .Replace('-', ':')
                .Replace('.', ':')
                .Replace(" ", string.Empty)
                .ToUpperInvariant();
        }

        /// <summary>取第一个非空值(统一大写、去空格后比较)</summary>
        private static string First(params string?[] candidates)
        {
            foreach (string? c in candidates)
            {
                string v = Normalize(c);
                if (v.Length > 0) return v;
            }
            return string.Empty;
        }

        /// <summary>规范化: 去空格并转大写，避免大小写/空白造成重复</summary>
        public static string Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }
}
