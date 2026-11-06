using CameraControl.Domain.Capture;
using CameraControl.Domain.Ports;

namespace CameraControl.Infrastructure.Triggers;

/// <summary>
/// 软件触发源（P0）。
/// 对已武装的相机会话下发 TriggerSoftware；未武装的会话触发失败可容忍——
/// 因为随后的 <c>CaptureSingleAsync</c> 会以"即取即用"方式取到帧。
/// </summary>
public sealed class SoftwareTriggerSource : ITriggerSource
{
    public TriggerSourceKind Kind => TriggerSourceKind.Software;

    public async Task FireAsync(
        TriggerSpec spec,
        IReadOnlyList<ICameraSession> targets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(targets);

        if (spec.Kind != TriggerSourceKind.Software)
        {
            throw new ArgumentException(
                $"软件触发源不接受 {spec.Kind} 类型。", nameof(spec));
        }

        if (spec.SoftwareDelayUs > 0)
        {
            await Task.Delay(
                TimeSpan.FromMicroseconds(spec.SoftwareDelayUs), cancellationToken).ConfigureAwait(false);
        }

        foreach (ICameraSession session in targets)
        {
            try
            {
                await session.TriggerSoftwareAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (CameraOperationException)
            {
                // 未武装的相机触发会失败——这是预期情况，帧由 CaptureSingleAsync 提供
            }
        }
    }
}

/// <summary>按触发类型分发触发源。未实现的类型返回 null，由调用方给出明确错误。</summary>
public sealed class TriggerSourceFactory : Application.Capture.ITriggerSourceFactory
{
    private readonly SoftwareTriggerSource _software;

    public TriggerSourceFactory(SoftwareTriggerSource software)
    {
        ArgumentNullException.ThrowIfNull(software);
        _software = software;
    }

    public ITriggerSource? TryGet(TriggerSourceKind kind) => kind switch
    {
        TriggerSourceKind.Software => _software,
        // P2：HardwareTriggerSource（PLC / 光源控制器脉冲）
        _ => null
    };
}
