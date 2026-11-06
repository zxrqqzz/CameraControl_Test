using CameraControl.Domain.Capture;

namespace CameraControl.Domain.Ports;

/// <summary>
/// 相机提供者。
/// 负责发现设备与创建会话，不负责会话内的取流细节。
/// </summary>
public interface ICameraProvider
{
    /// <summary>枚举当前可用相机。实现方只读取，不得打开设备。</summary>
    Task<IReadOnlyList<Cameras.CameraDescriptor>> DiscoverAsync(CancellationToken cancellationToken);

    /// <summary>打开指定相机会话。找不到或打开失败返回 <see cref="CameraNotFoundException"/>。</summary>
    Task<ICameraSession> OpenAsync(Cameras.CameraId id, CancellationToken cancellationToken);
}

/// <summary>
/// 一台相机的一次打开→取流→关闭生命周期。
/// 摄像头会话之间互相隔离：单台异常不得影响其它会话。
/// </summary>
public interface ICameraSession : IAsyncDisposable
{
    Cameras.CameraId Id { get; }

    Cameras.CameraDescriptor Descriptor { get; }

    bool IsOpen { get; }

    /// <summary>写入相机参数（曝光、增益、触发模式等）。</summary>
    Task SetParameterAsync(string name, string value, CancellationToken cancellationToken);

    /// <summary>读取相机参数，不存在时返回 null。</summary>
    Task<string?> GetParameterAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// 武装：进入等待触发状态。
    /// 软件触发时设置触发模式；硬件触发时进入等待外部脉冲的状态。
    /// </summary>
    Task ArmAsync(TriggerSpec trigger, CancellationToken cancellationToken);

    /// <summary>解除武装，恢复自由运行。</summary>
    Task DisarmAsync(CancellationToken cancellationToken);

    /// <summary>下发一次软件触发。仅在已武装且触发源为 Software 时有效。</summary>
    Task TriggerSoftwareAsync(CancellationToken cancellationToken);

    /// <summary>即取即用的一帧（用于预览/调试，无需武装）。</summary>
    Task<Frame> CaptureSingleAsync(CaptureOptions? options, CancellationToken cancellationToken);
}

/// <summary>相机未找到。</summary>
public sealed class CameraNotFoundException : Exception
{
    public CameraNotFoundException(string cameraId)
        : base($"未找到相机 '{cameraId}'。") => CameraId = cameraId;

    public string CameraId { get; }
}

/// <summary>相机操作失败（带结构化错误码）。</summary>
public sealed class CameraOperationException : Exception
{
    public CameraOperationException(string errorCode, string message, Exception? inner = null)
        : base(message, inner) => ErrorCode = errorCode;

    public string ErrorCode { get; }
}
