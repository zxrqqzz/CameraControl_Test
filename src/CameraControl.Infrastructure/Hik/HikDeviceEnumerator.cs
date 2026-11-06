using CameraControl.Domain.Cameras;
using MvCameraControl;

namespace CameraControl.Infrastructure.Hik;

/// <summary>
/// 海康 SDK 设备枚举（只读，不打开设备）。
/// 需要调用方已持有 <see cref="HikSdkRuntime"/> 的使用权。
/// </summary>
public static class HikDeviceEnumerator
{
    /// <summary>参与枚举的接口类型：GigE / USB3 Vision / 各采集卡的 GenTL 设备。</summary>
    public const DeviceTLayerType AllLayers =
        DeviceTLayerType.MvGigEDevice
        | DeviceTLayerType.MvUsbDevice
        | DeviceTLayerType.MvGenTLGigEDevice
        | DeviceTLayerType.MvGenTLCameraLinkDevice
        | DeviceTLayerType.MvGenTLCXPDevice
        | DeviceTLayerType.MvGenTLXoFDevice;

    /// <summary>一条枚举结果（字段已解析成可展示/可去重的形式）。</summary>
    public sealed record Entry
    {
        public IDeviceInfo Info { get; init; } = null!;
        public string Id { get; init; } = string.Empty;
        public string Vendor { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public string SerialNumber { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Ip { get; init; } = string.Empty;
        public string Mac { get; init; } = string.Empty;
        public string InterfaceText { get; init; } = string.Empty;
        public CameraKind Kind { get; init; } = CameraKind.Unknown;
    }

    /// <summary>枚举设备。要求调用方已持有 SDK 使用权。</summary>
    public static IReadOnlyList<Entry> Enumerate()
    {
        int ret = DeviceEnumerator.EnumDevicesEx(AllLayers, string.Empty, out List<IDeviceInfo> infos);
        if (ret != MvError.MV_OK) return [];

        var entries = new List<Entry>(infos.Count);
        foreach (IDeviceInfo info in infos)
        {
            Entry? entry = Parse(info);
            if (entry is not null) entries.Add(entry);
        }
        return entries;
    }

    /// <summary>把枚举结果转成领域描述符。</summary>
    public static CameraDescriptor ToDescriptor(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new CameraDescriptor(
            Id: new CameraId(entry.Id),
            DisplayName: entry.Name,
            Vendor: entry.Vendor,
            Model: entry.Model,
            SerialNumber: string.IsNullOrWhiteSpace(entry.SerialNumber) ? null : entry.SerialNumber,
            Kind: entry.Kind,
            Mount: CameraMountKind.EyeInHand,
            IsInfraredOnly: false,
            SupportsHardwareTrigger: true);
    }

    private static Entry? Parse(IDeviceInfo info)
    {
        try
        {
            string ip = string.Empty;
            string mac = string.Empty;
            string guid = string.Empty;
            CameraKind kind = CameraKind.Unknown;

            if (info is IGigEDeviceInfo gige)
            {
                ip = IpToString(gige.CurrentIp);
                mac = MacToString(gige.MacAddrHigh, gige.MacAddrLow);
                kind = CameraKind.HikGigE;
            }
            else if (info is IUSBDeviceInfo usb)
            {
                guid = usb.DeviceGUID ?? string.Empty;
                kind = CameraKind.HikUsb3;
            }

            string serial = info.SerialNumber ?? string.Empty;
            string vendor = info.ManufacturerName ?? string.Empty;
            string model = info.ModelName ?? string.Empty;
            string userDefined = info.UserDefinedName ?? string.Empty;

            // 稳定 id：序列号 > USB GUID > MAC > IP
            string id = FirstNonEmpty(serial, guid, mac, ip);
            if (id.Length == 0) return null;

            string name = !string.IsNullOrWhiteSpace(userDefined)
                ? userDefined
                : !string.IsNullOrWhiteSpace(model) ? model : "海康相机";

            return new Entry
            {
                Info = info,
                Id = "hik:" + id,
                Vendor = vendor,
                Model = model,
                SerialNumber = serial,
                Name = name,
                Ip = ip,
                Mac = mac,
                InterfaceText = LayerText(info.TLayerType),
                Kind = kind
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>SDK 的 uint IP 转点分十进制（网络字节序取高位）。</summary>
    public static string IpToString(uint ip) =>
        ip == 0 ? string.Empty : $"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";

    /// <summary>SDK 的高/低 32 位拼 MAC（实际只用低 24 位）。</summary>
    public static string MacToString(uint high, uint low) =>
        high == 0 && low == 0
            ? string.Empty
            : $"{((high >> 16) & 0xFF):X2}:{((high >> 8) & 0xFF):X2}:{(high & 0xFF):X2}:" +
              $"{((low >> 16) & 0xFF):X2}:{((low >> 8) & 0xFF):X2}:{(low & 0xFF):X2}";

    /// <summary>接口类型显示名。</summary>
    public static string LayerText(DeviceTLayerType layer)
    {
        if (layer.HasFlag(DeviceTLayerType.MvGenTLCXPDevice)) return "CoaXPress";
        if (layer.HasFlag(DeviceTLayerType.MvGenTLXoFDevice)) return "XoFLink";
        if (layer.HasFlag(DeviceTLayerType.MvGenTLCameraLinkDevice)) return "Camera Link";
        if (layer.HasFlag(DeviceTLayerType.MvGenTLGigEDevice)) return "GenTL GigE";
        if (layer.HasFlag(DeviceTLayerType.MvGigEDevice)) return "GigE Vision";
        if (layer.HasFlag(DeviceTLayerType.MvUsbDevice)) return "USB3 Vision";
        return layer.ToString();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return string.Empty;
    }
}
