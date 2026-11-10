namespace CameraControl.Domain.Ports;

/// <summary>光源相关错误码（稳定，供 AI 自我纠正）。</summary>
public static class LightErrorCodes
{
    /// <summary>未接入光源控制器。</summary>
    public const string NotConfigured = "LIGHT_NOT_CONFIGURED";

    /// <summary>参数非法。</summary>
    public const string InvalidParameter = "LIGHT_INVALID_PARAMETER";

    /// <summary>操作失败（驱动/通讯层）。</summary>
    public const string OperationFailed = "LIGHT_OPERATION_FAILED";
}

/// <summary>光源当前状态。</summary>
/// <param name="IsOn">是否点亮。</param>
/// <param name="BrightnessPercent">亮度百分比（0–100）。</param>
/// <param name="Channel">通道号（多通道光源时有效）。</param>
public sealed record LightState(
    bool IsOn,
    double BrightnessPercent,
    int Channel = 1);

/// <summary>
/// 光源端口。
///
/// 为什么现在才定：P2 的"频闪 + 短曝光"必须有可控光源，
/// 但具体品牌/协议未定，所以先固化**能力边界**，
/// 由基础设施层提供实现（未配置时给出明确错误而不是静默成功）。
/// </summary>
public interface ILightPort
{
    /// <summary>实现名称，用于诊断。</summary>
    string AdapterName { get; }

    /// <summary>是否已接入可用光源。</summary>
    bool IsAvailable { get; }

    /// <summary>点亮。</summary>
    Task OnAsync(int channel, CancellationToken cancellationToken);

    /// <summary>熄灭。</summary>
    Task OffAsync(int channel, CancellationToken cancellationToken);

    /// <summary>设置亮度（0–100）。</summary>
    Task SetBrightnessAsync(int channel, double percent, CancellationToken cancellationToken);

    /// <summary>
    /// 频闪：在指定时长内点亮，用于配合短曝光抓拍。
    /// </summary>
    /// <param name="channel">通道。</param>
    /// <param name="durationUs">点亮时长（µs）。</param>
    /// <param name="cancellationToken">取消。</param>
    Task StrobeAsync(int channel, int durationUs, CancellationToken cancellationToken);

    /// <summary>读取当前状态。</summary>
    Task<LightState> GetStateAsync(int channel, CancellationToken cancellationToken);
}
