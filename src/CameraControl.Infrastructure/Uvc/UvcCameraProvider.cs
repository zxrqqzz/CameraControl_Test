using CameraControl.Domain.Cameras;
using CameraControl.Domain.Ports;

namespace CameraControl.Infrastructure.Uvc;

/// <summary>
/// UVC 相机提供者（本机 USB 摄像头 / 笔记本内置摄像头）。
///
/// 隐私规则（继承自需求规格）：
/// - 红外 / Windows Hello 设备**只枚举、不打开**（<see cref="CameraDescriptor.IsInfraredOnly"/>）；
/// - 打开后画面只在内存，由 <see cref="UvcCameraSession"/> 保证不落盘。
/// </summary>
public sealed class UvcCameraProvider : ICameraProvider
{
    private readonly IClock _clock;

    public UvcCameraProvider(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public Task<IReadOnlyList<CameraDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UvcDeviceInfo> devices = UvcDeviceEnumerator.Enumerate();

        return Task.FromResult<IReadOnlyList<CameraDescriptor>>(
            [.. devices.Select(ToDescriptor)]);
    }

    public async Task<ICameraSession> OpenAsync(CameraId id, CancellationToken cancellationToken)
    {
        IReadOnlyList<UvcDeviceInfo> devices = UvcDeviceEnumerator.Enumerate();

        UvcDeviceInfo? match = devices
            .FirstOrDefault(d => string.Equals(d.InstanceId, id.Value, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            throw new CameraNotFoundException(id.Value);

        if (match.IsInfraredOnly)
        {
            throw new CameraOperationException(
                "UVC_INFRARED_REFUSED",
                $"'{match.DisplayName}' 是红外/Windows Hello 摄像头，按隐私策略不取流（仅列出）。");
        }

        CameraDescriptor descriptor = ToDescriptor(match);

        return await UvcCameraSession
            .OpenAsync(match.InstanceId, descriptor, _clock, cancellationToken)
            .ConfigureAwait(false);
    }

    private static CameraDescriptor ToDescriptor(UvcDeviceInfo device) => new(
        Id: new CameraId(device.InstanceId),
        DisplayName: device.DisplayName,
        Vendor: device.Vendor,
        Model: device.Model,
        SerialNumber: SerialFromInstance(device.InstanceId),
        Kind: CameraKind.Uvc,
        Mount: CameraMountKind.EyeInHand,
        IsInfraredOnly: device.IsInfraredOnly,
        SupportsHardwareTrigger: false,
        PixelSizeMm: null);

    private static string? SerialFromInstance(string instanceId)
    {
        int lastSlash = instanceId.LastIndexOf('\\');
        if (lastSlash < 0 || lastSlash == instanceId.Length - 1) return null;

        string tail = instanceId[(lastSlash + 1)..];
        return tail.Length > 0 ? tail : null;
    }
}
