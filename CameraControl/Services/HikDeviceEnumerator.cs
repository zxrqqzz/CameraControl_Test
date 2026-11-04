using CameraControl.Models;
using MvCameraControl;
using System;
using System.Collections.Generic;

namespace CameraControl.Services
{
    /// <summary>
    /// 海康 MVS SDK 的设备枚举实现(扫描器与采集服务共用同一套字段解析)。
    /// 本类只负责"读", 不打开设备、不取流，所以对正在使用中的相机零影响。
    /// 注意: 调用前必须持有 <see cref="HikSdkRuntime"/> 的使用权。
    /// </summary>
    public static class HikDeviceEnumerator
    {
        /// <summary>参与枚举的接口类型: GigE Vision / USB3 Vision / 各采集卡下的 GenTL 设备</summary>
        public const DeviceTLayerType AllLayers =
            DeviceTLayerType.MvGigEDevice
            | DeviceTLayerType.MvUsbDevice
            | DeviceTLayerType.MvGenTLGigEDevice
            | DeviceTLayerType.MvGenTLCameraLinkDevice
            | DeviceTLayerType.MvGenTLCXPDevice
            | DeviceTLayerType.MvGenTLXoFDevice;

        /// <summary>一条 SDK 枚举结果(字段已解析成可展示/可去重的形式)</summary>
        public sealed class Entry
        {
            public IDeviceInfo Info { get; set; } = null!;
            public DeviceTLayerType LayerType { get; set; }
            public string Vendor { get; set; } = string.Empty;
            public string Model { get; set; } = string.Empty;
            public string SerialNumber { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Ip { get; set; } = string.Empty;
            public string Mac { get; set; } = string.Empty;
            /// <summary>接入标识: USB 用 SDK 的 DeviceGUID，GigE 用序列号或 IP</summary>
            public string DevicePath { get; set; } = string.Empty;
            public string InterfaceText { get; set; } = string.Empty;
            public bool IsVirtual { get; set; }
            public bool IsGenTL { get; set; }
        }

        /// <summary>
        /// 枚举当前所有海康设备。要求调用方已持有 SDK 使用权。
        /// </summary>
        public static List<Entry> Enumerate(Action<string>? log = null)
        {
            var entries = new List<Entry>();

            int ret = DeviceEnumerator.EnumDevicesEx(AllLayers, string.Empty, out List<IDeviceInfo> infos);
            if (ret != MvError.MV_OK)
            {
                log?.Invoke($"海康 SDK 枚举失败: 0x{ret:X}");
                return entries;
            }

            foreach (IDeviceInfo info in infos)
            {
                Entry? entry = Parse(info);
                if (entry != null) entries.Add(entry);
            }
            return entries;
        }

        /// <summary>把一个 SDK 设备信息解析成 <see cref="Entry"/></summary>
        private static Entry? Parse(IDeviceInfo info)
        {
            try
            {
                string ip = string.Empty;
                string mac = string.Empty;
                string devicePath = string.Empty;
                bool isVirtual = false;
                bool isGenTL = false;

                if (info is IGigEDeviceInfo gige)
                {
                    ip = IpToString(gige.CurrentIp);
                    mac = MacToString(gige.MacAddrHigh, gige.MacAddrLow);
                    isVirtual = gige.VirtualDevice;
                    isGenTL = gige.GenTLDevice;
                }
                else if (info is IUSBDeviceInfo usb)
                {
                    // USB 设备的 GUID 是它在系统里的稳定标识
                    devicePath = usb.DeviceGUID ?? string.Empty;
                    isVirtual = usb.VirtualDevice;
                    if (string.IsNullOrWhiteSpace(devicePath))
                        devicePath = $"USB{usb.VendorID:X4}:{usb.ProductID:X4}:{usb.DeviceNumber}";
                }

                string serial = info.SerialNumber ?? string.Empty;
                string vendor = info.ManufacturerName ?? string.Empty;
                string model = info.ModelName ?? string.Empty;
                string userDefined = info.UserDefinedName ?? string.Empty;

                if (string.IsNullOrWhiteSpace(devicePath))
                    devicePath = serial;

                string name = !string.IsNullOrWhiteSpace(userDefined)
                    ? userDefined
                    : !string.IsNullOrWhiteSpace(model) ? model
                    : "海康相机";

                return new Entry
                {
                    Info = info,
                    LayerType = info.TLayerType,
                    Vendor = vendor,
                    Model = model,
                    SerialNumber = serial,
                    Name = name,
                    Ip = ip,
                    Mac = mac,
                    DevicePath = devicePath,
                    InterfaceText = LayerText(info.TLayerType),
                    IsVirtual = isVirtual,
                    IsGenTL = isGenTL
                };
            }
            catch (Exception)
            {
                // 单个设备读字段失败不影响其它设备
                return null;
            }
        }

        /// <summary>转成扫描结果行</summary>
        public static ScanCandidate ToCandidate(Entry entry)
        {
            return new ScanCandidate
            {
                Name = entry.Name,
                Vendor = entry.Vendor,
                Model = entry.Model,
                SerialNumber = entry.SerialNumber,
                Ip = entry.Ip,
                Mac = entry.Mac,
                DevicePath = entry.DevicePath,
                InterfaceText = entry.InterfaceText,
                Source = DeviceSource.HikSdk
            };
        }

        /// <summary>
        /// 在一批枚举结果里找回设备列表中的某个相机。
        /// 匹配优先级: 序列号 → 接入标识(DeviceGUID/IP) → IP → 名称。
        /// </summary>
        public static Entry? Match(IEnumerable<Entry> entries, CameraDevice device)
        {
            string serial = DeviceDedupe.Normalize(device.SerialNumber);
            string path = DeviceDedupe.Normalize(device.DevicePath);
            string ip = DeviceDedupe.Normalize(device.Ip);
            string name = DeviceDedupe.Normalize(device.Name);

            Entry? byPath = null, byIp = null, byName = null;

            foreach (Entry e in entries)
            {
                if (serial.Length > 0 && DeviceDedupe.Normalize(e.SerialNumber) == serial)
                    return e;

                if (path.Length > 0 && byPath == null && DeviceDedupe.Normalize(e.DevicePath) == path)
                    byPath = e;

                if (ip.Length > 0 && byIp == null && DeviceDedupe.Normalize(e.Ip) == ip)
                    byIp = e;

                if (name.Length > 0 && byName == null && DeviceDedupe.Normalize(e.Name) == name)
                    byName = e;
            }

            return byPath ?? byIp ?? byName;
        }

        /// <summary>SDK 的 uint 型 IP 转点分十进制(按网络字节序取高位)</summary>
        public static string IpToString(uint ip)
        {
            if (ip == 0) return string.Empty;
            return $"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";
        }

        /// <summary>SDK 的高/低 32 位拼成 MAC(MAC 实际只用低 24 位)</summary>
        public static string MacToString(uint high, uint low)
        {
            if (high == 0 && low == 0) return string.Empty;
            return string.Format(
                "{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                (high >> 16) & 0xFF, (high >> 8) & 0xFF, high & 0xFF,
                (low >> 16) & 0xFF, (low >> 8) & 0xFF, low & 0xFF);
        }

        /// <summary>接口类型显示名</summary>
        public static string LayerText(DeviceTLayerType layer)
        {
            if (layer.HasFlag(DeviceTLayerType.MvGenTLCXPDevice)) return "CoaXPress";
            if (layer.HasFlag(DeviceTLayerType.MvGenTLXoFDevice)) return "XoFLink";
            if (layer.HasFlag(DeviceTLayerType.MvGenTLCameraLinkDevice)) return "Camera Link";
            if (layer.HasFlag(DeviceTLayerType.MvGenTLGigEDevice)) return "GenTL GigE";
            if (layer.HasFlag(DeviceTLayerType.MvGigEDevice)) return "GigE Vision";
            if (layer.HasFlag(DeviceTLayerType.MvUsbDevice)) return "USB3 Vision";
            if (layer.HasFlag(DeviceTLayerType.MvVirGigEDevice)) return "虚拟GigE";
            if (layer.HasFlag(DeviceTLayerType.MvVirUsbDevice)) return "虚拟USB";
            if (layer.HasFlag(DeviceTLayerType.MvCameraLinkDevice)) return "Camera Link";
            return layer.ToString();
        }
    }
}
