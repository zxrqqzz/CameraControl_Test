using CameraControl.Domain.Cameras;
using CameraControl.Domain.Robotics;

namespace CameraControl.Domain.Capture;

/// <summary>
/// 同步分组采集结果。
///
/// **契约铁律**：调用方永远读取 <see cref="Frames"/> 列表。
/// P0 单相机时长度为 1；未来加相机不改调用方代码。
/// </summary>
/// <param name="GroupId">本组唯一标识。</param>
/// <param name="TimestampUtc">组级统一时间戳（多视角融合的对齐基准）。</param>
/// <param name="TimestampQuality">时间戳可信度。</param>
/// <param name="Skew">实测帧间最大偏斜。</param>
/// <param name="Frames">帧列表，按 CameraId 升序排列。</param>
/// <param name="MissingCameras">本次未取到帧的相机；非空时上层必须显式处理，禁止静默部分成功。</param>
/// <param name="Readiness">就绪门实测结果。</param>
/// <param name="RobotPoseAtExposure">EyeInHand 时采集时刻的机器人位姿。</param>
/// <param name="CalibrationVersion">本次使用的标定数据版本。</param>
public sealed record CaptureGroupResult(
    string GroupId,
    DateTimeOffset TimestampUtc,
    TimestampQuality TimestampQuality,
    TimeSpan Skew,
    IReadOnlyList<Frame> Frames,
    IReadOnlyList<CameraId> MissingCameras,
    ReadinessResult? Readiness = null,
    RobotPose? RobotPoseAtExposure = null,
    string? CalibrationVersion = null)
{
    /// <summary>是否完整（没有任何相机缺失）。</summary>
    public bool IsComplete => MissingCameras.Count == 0;

    /// <summary>是否为"软件近似"时间戳（融合层不应把它当硬同步使用）。</summary>
    public bool IsSoftwareTimestamp =>
        TimestampQuality == CameraControl.Domain.Capture.TimestampQuality.SoftwareApprox;
}
