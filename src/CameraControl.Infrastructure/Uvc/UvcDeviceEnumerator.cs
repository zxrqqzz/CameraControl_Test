using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CameraControl.Infrastructure.Uvc;

/// <summary>本机 UVC 设备信息（来自注册表枚举）。</summary>
/// <param name="InstanceId">设备实例路径，如 <c>USB\VID_30C9&amp;PID_0050\...</c>。</param>
/// <param name="DisplayName">显示名。</param>
/// <param name="Vendor">厂商名（未知时显示 VID）。</param>
/// <param name="Model">型号（显示名去掉厂商前缀）。</param>
/// <param name="Vid">USB 厂商号（4 位大写）。</param>
/// <param name="Pid">USB 产品号（4 位大写）。</param>
/// <param name="IsInfraredOnly">是否为红外/Windows Hello 摄像头。</param>
public sealed record UvcDeviceInfo(
    string InstanceId,
    string DisplayName,
    string Vendor,
    string Model,
    string Vid,
    string Pid,
    bool IsInfraredOnly);

/// <summary>
/// 本机 UVC（USB Video Class）设备枚举 —— 笔记本自带摄像头的绝大多数属于这一类。
///
/// 实现方式：直接读注册表 <c>HKLM\SYSTEM\CurrentControlSet\Enum\USB</c>，
/// 只接受 <c>Service == usbvideo</c> 的节点。
///
/// **为什么必须卡 Service**：Windows 的"相机"设备类里混着音频、蓝牙、打印机，
/// 早期版本只按设备类 GUID 扫，结果把麦克风和打印机也当成了相机。
/// </summary>
public static class UvcDeviceEnumerator
{
    private const string UsbEnumPath = @"SYSTEM\CurrentControlSet\Enum\USB";
    private const string UvcServiceName = "usbvideo";

    private static readonly Regex VidPidPattern =
        new(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);

    /// <summary>已知 VID 的厂商名（只在确切知道时填，否则如实显示编号）。</summary>
    private static readonly Dictionary<string, string> KnownVendorIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["30C9"] = "SunplusIT",
        ["0BDA"] = "Realtek",
        ["05AC"] = "Apple",
        ["046D"] = "Logitech",
        ["0402"] = "ALi",
        ["1BCF"] = "Sunplus",
        ["04F2"] = "Chicony",
        ["5986"] = "Bison",
        ["13D3"] = "IMC Networks",
        ["0C45"] = "Sonix"
    };

    /// <summary>枚举所有 UVC 设备（不代表有访问权限）。</summary>
    public static IReadOnlyList<UvcDeviceInfo> Enumerate()
    {
        var result = new List<UvcDeviceInfo>();

        try
        {
            using RegistryKey? usbRoot = Registry.LocalMachine.OpenSubKey(UsbEnumPath);
            if (usbRoot is null) return result;

            foreach (string vidPidKey in usbRoot.GetSubKeyNames())
            {
                // 只看 VID_xxxx&PID_xxxx 形态的节点
                if (!VidPidPattern.IsMatch(vidPidKey)) continue;

                using RegistryKey? vidPid = usbRoot.OpenSubKey(vidPidKey);
                if (vidPid is null) continue;

                foreach (string instance in vidPid.GetSubKeyNames())
                {
                    using RegistryKey? device = vidPid.OpenSubKey(instance);
                    if (device is null) continue;

                    string service = device.GetValue("Service") as string ?? string.Empty;

                    // 关键过滤：只有 usbvideo 驱动才是视频采集设备
                    if (!string.Equals(service, UvcServiceName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string instanceId = $@"USB\{vidPidKey}\{instance}";

                    Match match = VidPidPattern.Match(vidPidKey);
                    string vid = match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
                    string pid = match.Success ? match.Groups[2].Value.ToUpperInvariant() : string.Empty;

                    string displayName = ReadName(device, instanceId);
                    string vendor = KnownVendorIds.TryGetValue(vid, out string? known)
                        ? known
                        : vid.Length == 4 ? $"VID {vid}" : "未知厂商";

                    result.Add(new UvcDeviceInfo(
                        instanceId,
                        displayName,
                        vendor,
                        displayName,
                        vid,
                        pid,
                        IsInfrared(displayName)));
                }
            }
        }
        catch (Exception)
        {
            // 注册表不可读（权限/策略限制）时返回空表，由上层给出"未发现设备"的结论
            return result;
        }

        return result;
    }

    /// <summary>
    /// 读设备名。优先 FriendlyName，其次 DeviceDesc（需去掉 <c>@inf,%...%;</c> 前缀）。
    /// </summary>
    private static string ReadName(RegistryKey device, string fallback)
    {
        if (device.GetValue("FriendlyName") is string friendly && friendly.Trim().Length > 0)
            return friendly.Trim();

        if (device.GetValue("DeviceDesc") is string description)
        {
            string text = description.Trim();

            // 形如 "@usbcamera.inf,%usbvideo.devicedesc%;Integrated Camera"
            int semicolon = text.LastIndexOf(';');
            if (semicolon >= 0 && semicolon < text.Length - 1)
                text = text[(semicolon + 1)..];

            if (text.Trim().Length > 0) return text.Trim();
        }

        // 实在拿不到名字就用实例路径末段，绝不编造
        int lastSlash = fallback.LastIndexOf('\\');
        return lastSlash >= 0 ? fallback[(lastSlash + 1)..] : fallback;
    }

    /// <summary>
    /// 判断是否为红外/Windows Hello 摄像头。
    ///
    /// 这类设备只用于人脸解锁，按既定隐私策略**只列出、不取流**。
    /// 用词元匹配而不是子串匹配，避免 "Virtual" 之类的误判。
    /// </summary>
    private static bool IsInfrared(string name)
    {
        if (name.Contains("红外", StringComparison.Ordinal)) return true;

        foreach (string token in name.Split([' ', '-', '_', '(', ')', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("IR", StringComparison.OrdinalIgnoreCase)) return true;
            if (token.Equals("Infrared", StringComparison.OrdinalIgnoreCase)) return true;
            if (token.Equals("Hello", StringComparison.OrdinalIgnoreCase)) return true;
            if (token.Equals("Depth", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
