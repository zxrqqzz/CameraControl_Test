using CameraControl.Application.Capture;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Ports;
using MvCameraControl;

namespace CameraControl.Infrastructure.Hik;

/// <summary>
/// 海康相机提供者。
/// 枚举时短暂持有 SDK，打开会话时把 SDK 使用权移交会话（会话释放时归还）。
/// </summary>
public sealed class HikCameraProvider : ICameraProvider
{
    private readonly IClock _clock;

    public HikCameraProvider(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public Task<IReadOnlyList<CameraDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        int ret = HikSdkRuntime.Acquire();
        if (ret != MvError.MV_OK)
        {
            throw new CameraOperationException(
                "HIK_SDK_INIT_FAILED",
                $"海康 MVS SDK 初始化失败：0x{ret:X}。请确认已安装 MVS 运行库。");
        }

        try
        {
            IReadOnlyList<HikDeviceEnumerator.Entry> entries = HikDeviceEnumerator.Enumerate();
            return Task.FromResult<IReadOnlyList<CameraDescriptor>>(
                [.. entries.Select(HikDeviceEnumerator.ToDescriptor)]);
        }
        finally
        {
            HikSdkRuntime.Release();
        }
    }

    public Task<ICameraSession> OpenAsync(CameraId id, CancellationToken cancellationToken)
    {
        int ret = HikSdkRuntime.Acquire();
        if (ret != MvError.MV_OK)
        {
            throw new CameraOperationException(
                "HIK_SDK_INIT_FAILED",
                $"海康 MVS SDK 初始化失败：0x{ret:X}。请确认已安装 MVS 运行库。");
        }

        bool ownershipTransferred = false;
        try
        {
            IReadOnlyList<HikDeviceEnumerator.Entry> entries = HikDeviceEnumerator.Enumerate();
            HikDeviceEnumerator.Entry? match = entries
                .FirstOrDefault(e => string.Equals(e.Id, id.Value, StringComparison.Ordinal));

            if (match is null)
                throw new CameraNotFoundException(id.Value);

            IDevice device = DeviceFactory.CreateDevice(match.Info);
            int openRet = device.Open();
            if (openRet != MvError.MV_OK)
            {
                try { device.Dispose(); } catch { }
                throw new CameraOperationException(
                    "CAMERA_OPEN_FAILED",
                    $"打开相机 '{id}' 失败：0x{openRet:X}。");
            }

            var session = new HikCameraSession(
                device,
                HikDeviceEnumerator.ToDescriptor(match),
                _clock,
                HikSdkRuntime.Release);

            ownershipTransferred = true;
            return Task.FromResult<ICameraSession>(session);
        }
        finally
        {
            // 只有成功把使用权交给会话时才不在这里归还
            if (!ownershipTransferred) HikSdkRuntime.Release();
        }
    }
}
