using CameraControl.Domain.Cameras;
using CameraControl.Domain.Robotics;

namespace CameraControl.Domain.Capture;

/// <summary>
/// 把一组原始帧组装成 <see cref="CaptureGroupResult"/>。
///
/// 纯领域逻辑：不碰硬件、不碰时间源，因此可以完全用单元测试锁定行为。
/// 组时间戳取所有帧中最早的那一帧；每帧偏移与偏斜由此推导。
/// </summary>
public static class CaptureGroupAssembler
{
    /// <summary>
    /// 组装采集组结果。
    /// </summary>
    /// <param name="groupId">组标识。</param>
    /// <param name="frames">原始帧（顺序无关，同一相机重复出现时取第一帧）。</param>
    /// <param name="requestedCameras">本次请求的相机；用于计算缺失列表。</param>
    /// <param name="quality">时间戳可信度。</param>
    /// <param name="readiness">就绪门结果。</param>
    /// <param name="robotPose">采集时刻机器人位姿（EyeInHand 时必填，EyeToHand 可为 null）。</param>
    /// <param name="calibrationVersion">标定版本号。</param>
    public static CaptureGroupResult Assemble(
        string groupId,
        IEnumerable<Frame> frames,
        IEnumerable<CameraId> requestedCameras,
        TimestampQuality quality,
        ReadinessResult? readiness = null,
        RobotPose? robotPose = null,
        string? calibrationVersion = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(requestedCameras);

        List<CameraId> requested = [.. requestedCameras.Distinct()];

        // 同一相机重复时只保留第一帧，并按 CameraId 稳定排序，保证结果可复现
        var byCamera = new Dictionary<string, Frame>(StringComparer.Ordinal);
        foreach (Frame frame in frames)
        {
            ArgumentNullException.ThrowIfNull(frame);
            string key = frame.CameraId.Value ?? string.Empty;
            if (!byCamera.ContainsKey(key))
                byCamera[key] = frame;
        }

        List<Frame> ordered = [.. byCamera.Values
            .OrderBy(f => f.CameraId.Value, StringComparer.Ordinal)];

        if (ordered.Count == 0)
        {
            return new CaptureGroupResult(
                groupId,
                TimestampUtc: DateTimeOffset.MinValue,
                quality,
                Skew: TimeSpan.Zero,
                Frames: [],
                MissingCameras: requested,
                readiness,
                robotPose,
                calibrationVersion);
        }

        DateTimeOffset groupTimestamp = ordered.Min(f => f.TimestampUtc);

        var withOffsets = new List<Frame>(ordered.Count);
        TimeSpan minOffset = TimeSpan.MaxValue;
        TimeSpan maxOffset = TimeSpan.MinValue;

        foreach (Frame frame in ordered)
        {
            TimeSpan offset = frame.TimestampUtc - groupTimestamp;
            if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;

            if (offset < minOffset) minOffset = offset;
            if (offset > maxOffset) maxOffset = offset;

            withOffsets.Add(frame with { OffsetFromGroup = offset });
        }

        TimeSpan skew = maxOffset - minOffset;

        var present = new HashSet<string>(
            withOffsets.Select(f => f.CameraId.Value ?? string.Empty),
            StringComparer.Ordinal);

        List<CameraId> missing = [.. requested.Where(c => !present.Contains(c.Value ?? string.Empty))];

        return new CaptureGroupResult(
            groupId,
            groupTimestamp,
            quality,
            skew,
            withOffsets,
            missing,
            readiness,
            robotPose,
            calibrationVersion);
    }
}
