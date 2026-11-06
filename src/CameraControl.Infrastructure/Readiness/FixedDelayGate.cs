using CameraControl.Domain.Capture;
using CameraControl.Domain.Ports;

namespace CameraControl.Infrastructure.Readiness;

/// <summary>
/// P0 占位就绪门：固定延时。
///
/// **诚实占位**：这不是振动判定，只是"等一会儿再拍"。
/// 真正的视觉振动门（连拍测帧间位移）属于 P2，
/// 需要先用 <c>tools/BurstAnalyzer</c> 在真实工位验证可行性与定参。
/// </summary>
public sealed class FixedDelayGate : IReadinessGate
{
    private readonly IClock _clock;

    public FixedDelayGate(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public async Task<ReadinessResult> WaitAsync(
        ReadinessCriteria criteria,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        if (criteria.Source != ReadinessSource.FixedDelay)
        {
            throw new CameraOperationException(
                "READINESS_SOURCE_NOT_IMPLEMENTED",
                $"就绪判据来源 {criteria.Source} 尚未实现（P2 提供 VisionBased）。" +
                "当前版本只能使用 ReadinessSource.FixedDelay 占位。");
        }

        TimeSpan delay = criteria.FixedDelay ?? TimeSpan.Zero;
        DateTimeOffset startedAt = _clock.UtcNow;

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

        TimeSpan waited = _clock.UtcNow - startedAt;

        if (waited > criteria.Timeout)
        {
            return ReadinessResult.NotReady(criteria.Timeout, ReadinessSource.FixedDelay);
        }

        return new ReadinessResult(
            Ready: true,
            MeasuredAmplitude: double.NaN,
            Waited: waited,
            Source: ReadinessSource.FixedDelay);
    }
}
