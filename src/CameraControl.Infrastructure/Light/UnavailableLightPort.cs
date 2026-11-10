using CameraControl.Domain.Ports;

namespace CameraControl.Infrastructure.Light;

/// <summary>
/// 未接入光源时的实现。
///
/// 设计意图：**失败关闭**。没有光源控制器时，
/// 所有操作都抛带 <see cref="LightErrorCodes.NotConfigured"/> 的明确错误，
/// 而不是静默成功——静默成功会让"频闪+短曝光"看起来正常、实际全是环境光。
/// </summary>
public sealed class UnavailableLightPort : ILightPort
{
    public string AdapterName => "Unavailable";

    public bool IsAvailable => false;

    public Task OnAsync(int channel, CancellationToken cancellationToken) => throw NotConfigured();

    public Task OffAsync(int channel, CancellationToken cancellationToken) => throw NotConfigured();

    public Task SetBrightnessAsync(int channel, double percent, CancellationToken cancellationToken) =>
        throw NotConfigured();

    public Task StrobeAsync(int channel, int durationUs, CancellationToken cancellationToken) =>
        throw NotConfigured();

    public Task<LightState> GetStateAsync(int channel, CancellationToken cancellationToken) =>
        throw NotConfigured();

    private static CameraOperationException NotConfigured() => new(
        LightErrorCodes.NotConfigured,
        "尚未接入光源控制器。频闪/亮度控制不可用；" +
        "P2 的短曝光抓拍需要先接入光源驱动（厂商协议确定后在 Infrastructure/Light 下实现 ILightPort 并替换注册）。");
}

/// <summary>
/// 无通讯驱动的通道工厂。
///
/// 同样失败关闭：任何类型都返回 null，由上层给出
/// <see cref="CommErrorCodes.NotConfigured"/> 的明确错误。
/// </summary>
public sealed class UnavailableCommChannelFactory : ICommChannelFactory
{
    private static readonly CommKind[] None = [];

    public ICommChannel? TryCreate(CommEndpoint endpoint) => null;

    public IReadOnlyList<CommKind> SupportedKinds => None;
}
