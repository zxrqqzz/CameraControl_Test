using CameraControl.Application.Capture;
using CameraControl.Application.Commands;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Capture;

namespace CameraControl.Application.Capabilities;

/// <summary>
/// capture.group 命令：同步分组采集。
/// 即使 P0 只有一台相机，也走组语义。
/// </summary>
/// <param name="CameraIds">参与采集的相机。</param>
/// <param name="Trigger">触发方式。</param>
/// <param name="Mode">采集模式，P0 仅 StaticSettled。</param>
/// <param name="TimeoutMs">整组超时（毫秒）。</param>
/// <param name="Readiness">就绪判据；为空表示调用方自行承担稳定性风险。</param>
/// <param name="ArmFirst">是否先武装再触发。</param>
public sealed record CaptureGroupCommand(
    IReadOnlyList<CameraId> CameraIds,
    TriggerSpec Trigger,
    CaptureMode Mode = CaptureMode.StaticSettled,
    int TimeoutMs = 3000,
    ReadinessCriteria? Readiness = null,
    bool ArmFirst = false) : ICommand
{
    public const string Id = "capture.group";

    public string CapabilityId => Id;
}

/// <summary>采集组结果的对外投影（不含像素数据，避免把大对象传给 AI）。</summary>
public sealed record CaptureGroupDto(
    string GroupId,
    DateTimeOffset TimestampUtc,
    string TimestampQuality,
    long SkewUs,
    IReadOnlyList<CaptureFrameDto> Frames,
    IReadOnlyList<string> MissingCameraIds,
    ReadinessDto? Readiness)
{
    public static CaptureGroupDto From(CaptureGroupResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CaptureGroupDto(
            result.GroupId,
            result.TimestampUtc,
            result.TimestampQuality.ToString(),
            (long)Math.Round(result.Skew.TotalMilliseconds * 1000.0),
            [.. result.Frames.Select(f => new CaptureFrameDto(
                f.FrameId,
                f.CameraId.Value,
                (long)Math.Round(f.OffsetFromGroup.TotalMilliseconds * 1000.0),
                f.Image.Width,
                f.Image.Height,
                f.Image.PixelFormat))],
            [.. result.MissingCameras.Select(c => c.Value)],
            result.Readiness is null
                ? null
                : new ReadinessDto(
                    result.Readiness.Ready,
                    result.Readiness.MeasuredAmplitude,
                    (long)Math.Round(result.Readiness.Waited.TotalMilliseconds),
                    result.Readiness.Source.ToString()));
    }
}

/// <summary>单帧投影。</summary>
public sealed record CaptureFrameDto(
    string FrameId,
    string CameraId,
    long OffsetFromGroupUs,
    int Width,
    int Height,
    string PixelFormat);

/// <summary>就绪门事实投影。</summary>
public sealed record ReadinessDto(
    bool Ready,
    double MeasuredAmplitude,
    long WaitedMs,
    string Source);

/// <summary>capture.group 能力实现。</summary>
public sealed class CaptureGroupCapability : ICapabilityHandler<CaptureGroupCommand>
{
    private readonly ICaptureGroupService _service;

    public CaptureGroupCapability(ICaptureGroupService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CaptureGroupCommand.Id,
        Title: "同步分组采集",
        Description: "按指定触发方式从一组相机采集一组同步单帧，返回帧列表与组级统一时间戳。" +
                     "P0 仅支持 software 触发且只有单相机；hardware 触发在 P2 提供。" +
                     "无论几台相机，调用方始终读取 frames 数组。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CaptureGroupCommand),
        ParametersJsonSchema: CaptureGroupSchema);

    /// <summary>与 docs/03-能力清单.md 中 capture.group 对齐的参数 schema（P0 精简版）。</summary>
    private const string CaptureGroupSchema = """
    {
      "type": "object",
      "properties": {
        "cameraIds":   { "type": "array", "items": { "type": "string" }, "minItems": 1 },
        "trigger":     { "type": "object", "required": ["kind"],
                         "properties": { "kind": { "enum": ["software", "hardware"] },
                                         "hardwareSourceId": { "type": "string" },
                                         "softwareDelayUs": { "type": "integer", "minimum": 0 } } },
        "mode":        { "enum": ["StaticSettled", "MotionFrozen"], "default": "StaticSettled" },
        "timeoutMs":   { "type": "integer", "minimum": 100, "maximum": 30000, "default": 3000 },
        "armFirst":    { "type": "boolean", "default": false }
      },
      "required": ["cameraIds", "trigger"]
    }
    """;

    public async Task<object?> HandleAsync(
        CaptureGroupCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var request = new CaptureGroupRequest(
            CameraIds: command.CameraIds,
            Trigger: command.Trigger,
            Mode: command.Mode,
            Timeout: TimeSpan.FromMilliseconds(command.TimeoutMs),
            Readiness: command.Readiness,
            Exposure: null,
            ArmFirst: command.ArmFirst);

        CaptureGroupResult result = await _service
            .CaptureAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return CaptureGroupDto.From(result);
    }
}
