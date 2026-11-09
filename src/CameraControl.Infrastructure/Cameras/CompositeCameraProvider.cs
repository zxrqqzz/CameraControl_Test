using CameraControl.Domain.Cameras;
using CameraControl.Domain.Ports;

namespace CameraControl.Infrastructure.Cameras;

/// <summary>
/// 组合相机提供者：把海康（GigE/USB3 Vision）与 UVC（本机摄像头）合并到同一个列表。
///
/// 设计要点：
/// - **单路失败不影响另一路**：没装 MVS 运行库时，UVC 摄像头仍应能被列出；
/// - 打开时按上次枚举结果分派；找不到映射时依次尝试各提供者；
/// - 合并结果按"可采集优先、名称排序"，让用户先看到能用的设备。
/// </summary>
public sealed class CompositeCameraProvider : ICameraProvider
{
    private readonly ICameraProvider _hik;
    private readonly ICameraProvider _uvc;
    private readonly Dictionary<string, ICameraProvider> _route = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public CompositeCameraProvider(ICameraProvider hik, ICameraProvider uvc)
    {
        ArgumentNullException.ThrowIfNull(hik);
        ArgumentNullException.ThrowIfNull(uvc);

        _hik = hik;
        _uvc = uvc;
    }

    public async Task<IReadOnlyList<CameraDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var devices = new List<CameraDescriptor>();

        // 两路各自独立 try：一路挂了不影响另一路
        try
        {
            IReadOnlyList<CameraDescriptor> hikDevices = await _hik
                .DiscoverAsync(cancellationToken)
                .ConfigureAwait(false);

            devices.AddRange(hikDevices);
        }
        catch (Exception)
        {
            // 未安装 MVS / SDK 初始化失败：忽略，继续枚举 UVC
        }

        try
        {
            IReadOnlyList<CameraDescriptor> uvcDevices = await _uvc
                .DiscoverAsync(cancellationToken)
                .ConfigureAwait(false);

            devices.AddRange(uvcDevices);
        }
        catch (Exception)
        {
            // 注册表不可读等情况：忽略
        }

        lock (_sync)
        {
            _route.Clear();

            foreach (CameraDescriptor device in devices)
            {
                ICameraProvider owner = IsUvc(device.Id.Value) ? _uvc : _hik;
                _route[device.Id.Value] = owner;
            }
        }

        return [.. devices
            .OrderByDescending(d => d.CanCapture)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    public Task<ICameraSession> OpenAsync(CameraId id, CancellationToken cancellationToken)
    {
        ICameraProvider? owner;

        lock (_sync)
        {
            _route.TryGetValue(id.Value, out owner);
        }

        if (owner is not null)
            return owner.OpenAsync(id, cancellationToken);

        // 没枚举过/映射丢失：按 id 形态猜测，再不行逐个尝试
        ICameraProvider first = IsUvc(id.Value) ? _uvc : _hik;
        ICameraProvider second = ReferenceEquals(first, _uvc) ? _hik : _uvc;

        return OpenSequentiallyAsync(first, second, id, cancellationToken);
    }

    private static async Task<ICameraSession> OpenSequentiallyAsync(
        ICameraProvider first,
        ICameraProvider second,
        CameraId id,
        CancellationToken cancellationToken)
    {
        try
        {
            return await first.OpenAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (CameraNotFoundException)
        {
            return await second.OpenAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>UVC 的 id 是注册表实例路径，形如 <c>USB\VID_...&amp;PID_...\...</c>。</summary>
    private static bool IsUvc(string id) =>
        id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
}
