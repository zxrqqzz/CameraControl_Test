using CameraControl.Models;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CameraControl.Services
{
    /// <summary>
    /// 枚举本机的 UVC(USB Video Class)摄像头 —— 笔记本自带摄像头的绝大多数实现属于这一类。
    ///
    /// 实现方式: 直接读注册表 <c>HKLM\SYSTEM\CurrentControlSet\Enum\USB</c> 下 <c>Service=usbvideo</c>
    /// 的节点，再到 <c>HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses</c> 补一遍非 USB 总线的摄像头。
    /// 全程不打开设备、不取流、不安装任何第三方依赖，所以对正在出图的摄像头也没有影响。
    /// </summary>
    public static class UvcDeviceScanner
    {
        /// <summary>UVC 视频驱动在注册表里的服务名</summary>
        private const string UvcServiceName = "usbvideo";

        /// <summary>
        /// Windows 的"相机"设备类 GUID，仅用于补扫不在 USB 总线上的设备。
        ///
        /// 注意: 只能用它，不能用下面这两个——实测会混入大量非摄像头设备:
        /// - {65e8773d-...} 是 KSCATEGORY_CAPTURE，<b>音频采集也算</b>(麦克风/蓝牙音频/MIDI 全进来)；
        /// - {6bdd1fc6-...} 是 Image 设备类，<b>包含扫描仪和打印机</b>(Canon 打印机就混进来了)。
        /// 并且补扫结果还要再过一道 "Service == usbvideo" 的校验，否则仍会误判。
        /// </summary>
        private static readonly string[] CameraDeviceClassIds =
        {
            "{e5323777-f976-4f5b-9b55-b94699c46e44}"  // Camera (Windows 10+)
        };

        /// <summary>常见摄像头芯片/整机厂商的 USB Vendor ID 猜测表(仅用于补一个更友好的厂商名)</summary>
        private static readonly Dictionary<string, string> KnownVendorIds =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["0BDA"] = "Realtek",
                ["30C9"] = "SunplusIT",   // 联想/惠普等笔记本常见内置摄像头模组(含 IR 接口)
                ["046D"] = "Logitech",
                ["04F2"] = "Chicony",
                ["5986"] = "Bison",
                ["0C45"] = "Sonix",
                ["1BCF"] = "Sunplus",
                ["28DA"] = "SunplusIT",
                ["13D3"] = "IMC Networks",
                ["8086"] = "Intel",
                ["04CA"] = "Lite-On",
                ["0AC8"] = "Vimicro",
                ["045E"] = "Microsoft",
                ["1B3B"] = "AVerMedia",
                ["1E4E"] = "PixArt",
                ["05A9"] = "OmniVision",
                ["090C"] = "Silicon Motion",
                ["0B97"] = "O2 Micro"
            };

        private static readonly Regex VidPidPattern =
            new Regex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);

        /// <summary>扫描本机 UVC 摄像头</summary>
        public static List<ScanCandidate> Scan(Action<string>? log = null)
        {
            var results = new List<ScanCandidate>();
            var seenInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ScanUsbEnum(results, seenInstances, log);
            ScanDeviceClasses(results, seenInstances, log);

            log?.Invoke($"本机 UVC 摄像头: {results.Count} 个");
            return results;
        }

        /// <summary>主扫描: Enum\USB 下 Service=usbvideo 的节点</summary>
        private static void ScanUsbEnum(
            List<ScanCandidate> results, HashSet<string> seenInstances, Action<string>? log)
        {
            try
            {
                using RegistryKey? usbRoot =
                    Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
                if (usbRoot == null) return;

                foreach (string vidPidKey in usbRoot.GetSubKeyNames())
                {
                    using RegistryKey? vidPid = usbRoot.OpenSubKey(vidPidKey);
                    if (vidPid == null) continue;

                    foreach (string instance in vidPid.GetSubKeyNames())
                    {
                        using RegistryKey? device = vidPid.OpenSubKey(instance);
                        if (device == null) continue;

                        string service = device.GetValue("Service") as string ?? string.Empty;
                        if (!string.Equals(service, UvcServiceName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string instanceId = $@"USB\{vidPidKey}\{instance}";
                        if (!seenInstances.Add(instanceId)) continue;

                        ScanCandidate? candidate = BuildCandidate(instanceId, vidPidKey, device);
                        if (candidate != null) results.Add(candidate);
                    }
                }
            }
            catch (Exception e)
            {
                log?.Invoke("读取 USB 设备注册表失败: " + e.Message);
            }
        }

        /// <summary>
        /// 补扫: 相机设备类里不在 USB 总线上的设备(例如某些笔记本的 MIPI/ACPI 摄像头)。
        ///
        /// 这里必须做二次校验: 只有 <c>Service == usbvideo</c> 的节点才算视频采集设备。
        /// 少了这一步，"相机"设备类里混进来的音频/打印/扫描设备都会被误当成摄像头。
        /// </summary>
        private static void ScanDeviceClasses(
            List<ScanCandidate> results, HashSet<string> seenInstances, Action<string>? log)
        {
            foreach (string classId in CameraDeviceClassIds)
            {
                try
                {
                    using RegistryKey? classRoot = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{classId}");
                    if (classRoot == null) continue;

                    foreach (string symbolicLink in classRoot.GetSubKeyNames())
                    {
                        using RegistryKey? link = classRoot.OpenSubKey(symbolicLink);
                        if (link == null) continue;

                        string instanceId = link.GetValue("DeviceInstance") as string ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(instanceId))
                            instanceId = InstanceIdFromSymbolicLink(symbolicLink);
                        if (string.IsNullOrWhiteSpace(instanceId)) continue;

                        // 已经在 USB 主扫描里加过的，这里跳过
                        if (seenInstances.Contains(instanceId)) continue;

                        using RegistryKey? device =
                            Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId);

                        // 关键校验: 不是 UVC 视频驱动，一律不当摄像头
                        if (!IsVideoCaptureDevice(instanceId, device)) continue;

                        if (!seenInstances.Add(instanceId)) continue;

                        string vidPid = ExtractVidPid(instanceId);
                        ScanCandidate? candidate = BuildCandidate(instanceId, vidPid, device);
                        if (candidate != null) results.Add(candidate);
                    }
                }
                catch (Exception e)
                {
                    log?.Invoke($"读取设备类 {classId} 失败(无权限时属正常，不影响 USB 摄像头扫描): " + e.Message);
                }
            }
        }

        /// <summary>
        /// 判定一个设备节点是不是视频采集设备。
        /// 依据: 驱动服务名必须是 usbvideo，且实例路径不是已知的音频/蓝牙/打印总线。
        /// </summary>
        private static bool IsVideoCaptureDevice(string instanceId, RegistryKey? device)
        {
            // 明确的非视频总线/节点，直接排除
            if (instanceId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)) return false;
            if (instanceId.StartsWith("INTELAUDIO", StringComparison.OrdinalIgnoreCase)) return false;
            if (instanceId.StartsWith(@"SWD\MMDEVAPI", StringComparison.OrdinalIgnoreCase)) return false;
            if (instanceId.StartsWith(@"SWD\PRINTENUM", StringComparison.OrdinalIgnoreCase)) return false;
            if (instanceId.StartsWith(@"SWD\ESCLUSB", StringComparison.OrdinalIgnoreCase)) return false;
            if (instanceId.StartsWith(@"SWD\KSENUM", StringComparison.OrdinalIgnoreCase)) return false;

            if (device == null) return false;

            string service = device.GetValue("Service") as string ?? string.Empty;
            return string.Equals(service, UvcServiceName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把一条注册表设备节点转成扫描结果</summary>
        private static ScanCandidate? BuildCandidate(string instanceId, string vidPid, RegistryKey? device)
        {
            string friendlyName = device != null ? ReadString(device, "FriendlyName") : string.Empty;
            string deviceDesc = device != null ? ReadString(device, "DeviceDesc") : string.Empty;
            string manufacturer = device != null ? ReadString(device, "Manufacturer") : string.Empty;

            string name = FirstNonEmpty(friendlyName, deviceDesc, "USB 摄像头");
            if (IsNotUsefulName(name)) name = "USB 摄像头";

            string vid = string.Empty, pid = string.Empty;
            Match m = VidPidPattern.Match(vidPid);
            if (m.Success)
            {
                vid = m.Groups[1].Value.ToUpperInvariant();
                pid = m.Groups[2].Value.ToUpperInvariant();
            }

            // 注册表里的 Manufacturer 常常是 "Generic Mfg." 或直接为空，
            // 这种情况下用 VID 猜测表；再不行就如实展示 VID 编号，不编造公司名。
            string vendor = manufacturer;
            if (vid.Length == 4 && KnownVendorIds.TryGetValue(vid, out string? known))
                vendor = known;
            else if (IsNotUsefulName(vendor))
                vendor = vid.Length == 4 ? "VID " + vid : string.Empty;

            string model = name;
            string[] parts = instanceId.Split('\\');
            string serial = parts.Length >= 3 ? parts[parts.Length - 1] : string.Empty;
            // 有些设备的"序列号"其实是用 & 连接的端口号，不是唯一标识时就不展示
            if (serial.IndexOf('&') >= 0 || serial.Length > 40) serial = string.Empty;

            return new ScanCandidate
            {
                Name = name,
                Vendor = vendor,
                Model = model,
                SerialNumber = serial,
                DevicePath = instanceId,
                InterfaceText = BuildInterfaceText(vid, pid, IsInfraredName(name)),
                Source = DeviceSource.Uvc,
                IsInfraredOnly = IsInfraredName(name)
            };
        }

        /// <summary>
        /// 判定是不是红外(IR)摄像头。
        /// 这类设备是笔记本做 Windows Hello 人脸解锁用的，输出红外灰度图，
        /// 通常还被系统独占，当普通摄像头用没有意义。
        /// </summary>
        private static bool IsInfraredName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string upper = name.ToUpperInvariant();
            return upper.Contains("IR CAMERA")
                || upper.Contains("INFRARED")
                || upper.Contains("IR_CAMERA")
                || upper.Contains("IR WEBCAM");
        }

        private static string BuildInterfaceText(string vid, string pid, bool isInfrared)
        {
            string baseText = vid.Length == 4 ? $"UVC (VID_{vid}/PID_{pid})" : "UVC";
            return isInfrared ? baseText + " · 红外" : baseText;
        }

        /// <summary>读注册表字符串值，并解析 "&#64;xxx.inf,%name%;可用名称" 这种间接引用</summary>
        private static string ReadString(RegistryKey key, string valueName)
        {
            try
            {
                return CleanIndirectString(key.GetValue(valueName) as string);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 注册表里很多描述是间接字符串: "@%SystemRoot%\inf\usbvideo.inf,%desc%;USB Video Device"。
        /// 取分号后的直接可读文本；没有分号就说明只能查资源文件，这里返回空。
        /// </summary>
        private static string CleanIndirectString(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string text = raw.Trim();

            if (text.StartsWith("@", StringComparison.Ordinal))
            {
                int semicolon = text.IndexOf(';');
                if (semicolon < 0 || semicolon >= text.Length - 1) return string.Empty;
                text = text.Substring(semicolon + 1);
            }

            return Sanitize(text);
        }

        /// <summary>
        /// 清理显示文本: 去掉换行/控制字符、合并空白。
        /// 注册表里偶尔会残留 "%1 ...%0" 这种资源占位符，这种值没有可读性，直接丢掉。
        /// </summary>
        private static string Sanitize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            string text = raw;
            // 去掉残留的资源占位符
            if (text.IndexOf("%0", StringComparison.Ordinal) >= 0) return string.Empty;

            var builder = new System.Text.StringBuilder(text.Length);
            bool lastWasSpace = false;
            foreach (char c in text)
            {
                char ch = char.IsControl(c) ? ' ' : c;
                if (ch == ' ')
                {
                    if (lastWasSpace) continue;
                    lastWasSpace = true;
                }
                else
                {
                    lastWasSpace = false;
                }
                builder.Append(ch);
            }

            return builder.ToString().Trim();
        }

        /// <summary>过滤掉 "Generic Mfg."、"USB Video Device" 这类没有信息量的值</summary>
        private static bool IsNotUsefulName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            string v = value.Trim();
            return v.Equals("Generic Mfg.", StringComparison.OrdinalIgnoreCase)
                || v.Equals("Generic", StringComparison.OrdinalIgnoreCase)
                || v.Equals("USB Video Device", StringComparison.OrdinalIgnoreCase)
                || v.Equals("Camera", StringComparison.OrdinalIgnoreCase)
                || v.StartsWith("@", StringComparison.Ordinal);
        }

        /// <summary>从 "##?#USB#VID_1234&PID_5678#5&1f2e&0&1#{guid}" 里还原出设备实例路径</summary>
        private static string InstanceIdFromSymbolicLink(string symbolicLink)
        {
            string text = symbolicLink;
            int brace = text.IndexOf("#{", StringComparison.Ordinal);
            if (brace > 0) text = text.Substring(0, brace);

            if (text.StartsWith(@"\??\", StringComparison.Ordinal)) text = text.Substring(4);
            else if (text.StartsWith("##?#", StringComparison.Ordinal)) text = text.Substring(4);
            else if (text.StartsWith("\\??\\", StringComparison.Ordinal)) text = text.Substring(4);

            return text.Replace('#', '\\').Trim('\\');
        }

        /// <summary>从设备实例路径里取出 "VID_xxxx&PID_yyyy" 片段</summary>
        private static string ExtractVidPid(string instanceId)
        {
            Match m = VidPidPattern.Match(instanceId);
            return m.Success ? m.Value : string.Empty;
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (string? v in values)
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            return string.Empty;
        }
    }
}
