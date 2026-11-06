using CameraControl.Domain.Cameras;

namespace CameraControl.Domain.Capture;

/// <summary>
/// 同步分组采集请求。
/// 即使 P0 只有一台相机，接口也按 N 台设计——未来加相机不改调用方。
/// </summary>
/// <param name="CameraIds">参与本次采集的相机；为空表示"当前任务的相机组"。</param>
/// <param name="Trigger">触发方式。</param>
/// <param name="Mode">采集模式（静止 / 运动冻结）。</param>
/// <param name="Timeout">整组采集超时。</param>
/// <param name="Readiness">就绪判据；<see cref="CaptureMode.StaticSettled"/> 时必填。</param>
/// <param name="Exposure">曝光/频闪预算；<see cref="CaptureMode.MotionFrozen"/> 时必填。</param>
/// <param name="ArmFirst">是否先武装相机再触发（硬触发场景应为 true）。</param>
public sealed record CaptureGroupRequest(
    IReadOnlyList<CameraId> CameraIds,
    TriggerSpec Trigger,
    CaptureMode Mode = CaptureMode.StaticSettled,
    TimeSpan? Timeout = null,
    ReadinessCriteria? Readiness = null,
    ExposureProfile? Exposure = null,
    bool ArmFirst = false)
{
    public const int DefaultTimeoutMs = 3000;

    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMilliseconds(DefaultTimeoutMs);

    /// <summary>创建一个最小可用请求（P0 单相机 + 软件触发）。</summary>
    public static CaptureGroupRequest ForSingleCamera(
        CameraId cameraId,
        TriggerSpec? trigger = null,
        ReadinessCriteria? readiness = null) =>
        new(
            CameraIds: [cameraId],
            Trigger: trigger ?? TriggerSpec.Software(),
            Mode: CaptureMode.StaticSettled,
            Readiness: readiness);
}
