namespace CameraControl.Domain.Capture;

/// <summary>就绪判据来源。</summary>
public enum ReadinessSource
{
    /// <summary>机器人控制器报告"到位"。**仅粗判据**，不能单独作为采集条件。</summary>
    RobotReported = 0,

    /// <summary>视觉判据：用相机自身做振动传感器，连拍测帧间位移。推荐。</summary>
    VisionBased = 1,

    /// <summary>外部振动传感器 / 加速度计。</summary>
    ExternalSensor = 2,

    /// <summary>固定延时占位（P0 使用，诚实标注：这不是真正的振动判定）。</summary>
    FixedDelay = 3
}

/// <summary>
/// 就绪判据。
/// 触发条件是"振动衰减到阈值以下并持续一段时间"，而不是"发完停止指令立即拍"。
/// </summary>
/// <param name="MaxAmplitude">振幅阈值。单位由 <paramref name="Source"/> 决定（VisionBased 时为像素）。</param>
/// <param name="StableDuration">必须持续低于阈值的时长。</param>
/// <param name="Timeout">等待上限，超过则判定未就绪。</param>
/// <param name="Source">判据来源。</param>
/// <param name="FixedDelay">当 <paramref name="Source"/> 为 <see cref="ReadinessSource.FixedDelay"/> 时使用的固定延时。</param>
public sealed record ReadinessCriteria(
    double MaxAmplitude,
    TimeSpan StableDuration,
    TimeSpan Timeout,
    ReadinessSource Source = ReadinessSource.VisionBased,
    TimeSpan? FixedDelay = null)
{
    /// <summary>P0 占位：固定延时门。诚实占位，不伪装成振动判定。</summary>
    public static ReadinessCriteria FixedDelayPlaceholder(TimeSpan delay, TimeSpan? timeout = null) =>
        new(
            MaxAmplitude: double.NaN,
            StableDuration: TimeSpan.Zero,
            Timeout: timeout ?? TimeSpan.FromSeconds(2),
            Source: ReadinessSource.FixedDelay,
            FixedDelay: delay);
}

/// <summary>就绪判定结果。</summary>
/// <param name="Ready">是否就绪。</param>
/// <param name="MeasuredAmplitude">实测振幅（VisionBased 时为像素）。</param>
/// <param name="Waited">实际等待时长。</param>
/// <param name="Source">实际使用的判据来源。</param>
public sealed record ReadinessResult(
    bool Ready,
    double MeasuredAmplitude,
    TimeSpan Waited,
    ReadinessSource Source)
{
    public static ReadinessResult NotReady(TimeSpan waited, ReadinessSource source) =>
        new(false, double.NaN, waited, source);
}
