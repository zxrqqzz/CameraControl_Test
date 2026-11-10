using CameraControl.Application.Capture;
using CameraControl.Application.Commands;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Capture;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Capabilities;

// ── 命令 ────────────────────────────────────────────────

/// <summary>capture.single：取一张（即取即用，无需武装）。</summary>
public sealed record CaptureSingleCommand(
    string CameraId,
    int? ExposureUs = null,
    int? Gain = null,
    bool IncludeImage = false) : ICommand
{
    public const string Id = "capture.single";

    public string CapabilityId => Id;
}

/// <summary>capture.arm：进入等待触发状态。</summary>
public sealed record CaptureArmCommand(
    string CameraId,
    string TriggerKind = "software",
    int SoftwareDelayUs = 0) : ICommand
{
    public const string Id = "capture.arm";

    public string CapabilityId => Id;
}

/// <summary>capture.disarm：解除武装。</summary>
public sealed record CaptureDisarmCommand(string CameraId) : ICommand
{
    public const string Id = "capture.disarm";

    public string CapabilityId => Id;
}

/// <summary>capture.trigger：下发一次软件触发。</summary>
public sealed record CaptureTriggerCommand(string CameraId) : ICommand
{
    public const string Id = "capture.trigger";

    public string CapabilityId => Id;
}

/// <summary>capture.waitSettled：等待系统进入可采集状态。</summary>
public sealed record CaptureWaitSettledCommand(
    int TimeoutMs = 2000,
    int FixedDelayMs = 0) : ICommand
{
    public const string Id = "capture.waitSettled";

    public string CapabilityId => Id;
}

/// <summary>capture.getStats：读取某台相机的采集统计。</summary>
public sealed record CaptureGetStatsCommand(string CameraId) : ICommand
{
    public const string Id = "capture.getStats";

    public string CapabilityId => Id;
}

/// <summary>capture.startContinuous：开始连续预览。</summary>
public sealed record CaptureStartContinuousCommand(string CameraId, int Fps = 10) : ICommand
{
    public const string Id = "capture.startContinuous";

    public string CapabilityId => Id;
}

/// <summary>capture.stopContinuous：停止连续预览。</summary>
public sealed record CaptureStopContinuousCommand(string CameraId) : ICommand
{
    public const string Id = "capture.stopContinuous";

    public string CapabilityId => Id;
}

/// <summary>capture.getFrame：取连续预览的最新一帧。</summary>
public sealed record CaptureGetFrameCommand(string CameraId, bool IncludeImage = true) : ICommand
{
    public const string Id = "capture.getFrame";

    public string CapabilityId => Id;
}

// ── DTO ─────────────────────────────────────────────────

/// <summary>
/// 帧投影。
///
/// 默认**不包含像素**：帧数据只应短暂存在于内存，
/// 需要看图时显式指定 includeImage=true，返回 base64 PNG。
/// </summary>
public sealed record FrameDto(
    string CameraId,
    string FrameId,
    DateTimeOffset TimestampUtc,
    int Width,
    int Height,
    string PixelFormat,
    long ByteLength,
    int? ExposureUs,
    string? PngBase64);

// ── 实现 ────────────────────────────────────────────────

/// <summary>capture.single 实现。</summary>
public sealed class CaptureSingleCapability : ICapabilityHandler<CaptureSingleCommand>
{
    private readonly ICameraSessionRegistry _registry;
    private readonly ICaptureStatistics _statistics;
    private readonly IFrameEncoder _encoder;

    public CaptureSingleCapability(
        ICameraSessionRegistry registry,
        ICaptureStatistics statistics,
        IFrameEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(encoder);

        _registry = registry;
        _statistics = statistics;
        _encoder = encoder;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CaptureSingleCommand.Id,
        Title: "采集单帧",
        Description:
            "从已打开的相机取一帧。默认只返回元数据；includeImage=true 时附带 base64 PNG 供查看。" +
            "帧数据只在内存中，不落盘。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CaptureSingleCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "cameraId": { "type": "string" },
            "exposureUs": { "type": "integer", "minimum": 1, "description": "曝光（µs），省略则用相机当前设置" },
            "gain": { "type": "number", "description": "增益，省略则用相机当前设置" },
            "includeImage": { "type": "boolean", "default": false, "description": "是否返回 base64 PNG" }
          },
          "required": ["cameraId"]
        }
        """);

    public async Task<object?> HandleAsync(
        CaptureSingleCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        var id = new CameraId(command.CameraId);

        ICameraSession session = _registry.Find(id)
            ?? throw new CameraOperationException(
                CaptureErrorCodes.CameraNotOpen,
                $"相机 {command.CameraId} 未打开，请先调用 camera.open。");

        var options = new CaptureOptions(command.ExposureUs, command.Gain is null ? null : (int?)command.Gain);

        try
        {
            Frame frame = await session.CaptureSingleAsync(options, cancellationToken).ConfigureAwait(false);
            _statistics.RecordSuccess(id, frame);

            return CaptureFrameCapability.ToDto(frame, command.ExposureUs, command.IncludeImage, _encoder);
        }
        catch (Exception ex) when (ex is not CameraOperationException)
        {
            _statistics.RecordFailure(id, ex.Message);
            throw;
        }
    }
}

/// <summary>capture.getFrame 实现（取预览最新帧）。</summary>
public sealed class CaptureFrameCapability : ICapabilityHandler<CaptureGetFrameCommand>
{
    private readonly ICapturePreviewService _preview;
    private readonly IFrameEncoder _encoder;

    public CaptureFrameCapability(ICapturePreviewService preview, IFrameEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(encoder);

        _preview = preview;
        _encoder = encoder;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CaptureGetFrameCommand.Id,
        Title: "取预览最新帧",
        Description:
            "返回连续预览的最新一帧。未在预览或尚无帧时返回 frame=null（不是错误）。" +
            "includeImage=true 时附带 base64 PNG。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CaptureGetFrameCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "cameraId": { "type": "string" },
            "includeImage": { "type": "boolean", "default": true }
          },
          "required": ["cameraId"]
        }
        """);

    public Task<object?> HandleAsync(
        CaptureGetFrameCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        Frame? frame = _preview.Latest(new CameraId(command.CameraId));

        return Task.FromResult<object?>(new
        {
            cameraId = command.CameraId,
            previewing = _preview.Active.Contains(command.CameraId),
            frame = frame is null ? null : ToDto(frame, null, command.IncludeImage, _encoder)
        });
    }

    internal static FrameDto ToDto(Frame frame, int? exposureUs, bool includeImage, IFrameEncoder encoder) =>
        new(
            frame.CameraId.Value ?? string.Empty,
            frame.FrameId,
            frame.TimestampUtc,
            frame.Image.Width,
            frame.Image.Height,
            frame.Image.PixelFormat,
            frame.Image.ByteLength,
            exposureUs,
            includeImage ? Convert.ToBase64String(encoder.EncodePng(frame.Image)) : null);
}

/// <summary>capture.arm 实现。</summary>
public sealed class CaptureArmCapability : ICapabilityHandler<CaptureArmCommand>
{
    private readonly ICameraSessionRegistry _registry;

    public CaptureArmCapability(ICameraSessionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CaptureArmCommand.Id,
        Title: "武装相机",
        Description:
            "让相机进入等待触发状态。triggerKind=software 可用；hardware 需要 PLC/光源控制器（当前返回未实现）。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CaptureArmCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "cameraId": { "type": "string" },
            "triggerKind": { "type": "string", "enum": ["software", "hardware"], "default": "software" },
            "softwareDelayUs": { "type": "integer", "default": 0 }
          },
          "required": ["cameraId"]
        }
        """);

    public async Task<object?> HandleAsync(
        CaptureArmCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        var id = new CameraId(command.CameraId);

        ICameraSession session = _registry.Find(id)
            ?? throw new CameraOperationException(
                CaptureErrorCodes.CameraNotOpen,
                $"相机 {command.CameraId} 未打开，请先调用 camera.open。");

        TriggerSpec trigger = string.Equals(command.TriggerKind, "hardware", StringComparison.OrdinalIgnoreCase)
            ? TriggerSpec.Hardware(command.CameraId)
            : TriggerSpec.Software(command.SoftwareDelayUs);

        await session.ArmAsync(trigger, cancellationToken).ConfigureAwait(false);

        return new { cameraId = command.CameraId, armed = true, triggerKind = command.TriggerKind };
    }
}

/// <summary>capture.disarm 实现。</summary>
public sealed class CaptureDisarmCapability : ICapabilityHandler<CaptureDisarmCommand>
{
    private readonly ICameraSessionRegistry _registry;

    public CaptureDisarmCapability(ICameraSessionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CaptureDisarmCommand.Id,
        Title: "解除武装",
        Description: "解除等待触发状态，恢复自由运行。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CaptureDisarmCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "cameraId": { "type": "string" } }, "required": ["cameraId"] }
        """);

    public async Task<object?> HandleAsync(
        CaptureDisarmCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        var id = new CameraId(command.CameraId);

        ICameraSession session = _registry.Find(id)
            ?? throw new CameraOperationException(
                CaptureErrorCodes.CameraNotOpen,
                $"相机 {command.CameraId} 未打开。");

        await session.DisarmAsync(cancellationToken).ConfigureAwait(false);

        return new { cameraId = command.CameraId, armed = false };
    }
}

/// <summary>capture.trigger 实现。</summary>
public sealed class CaptureTriggerCapability : ICapabilityHandler<CaptureTriggerCommand>
{
    private readonly ICameraSessionRegistry _registry;

    public CaptureTriggerCapability(ICameraSessionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CaptureTriggerCommand.Id,
        Title: "软件触发",
        Description: "向已武装的相机下发一次软件触发。需先 capture.arm 且触发源为 software。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CaptureTriggerCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "cameraId": { "type": "string" } }, "required": ["cameraId"] }
        """);

    public async Task<object?> HandleAsync(
        CaptureTriggerCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        var id = new CameraId(command.CameraId);

        ICameraSession session = _registry.Find(id)
            ?? throw new CameraOperationException(
                CaptureErrorCodes.CameraNotOpen,
                $"相机 {command.CameraId} 未打开。");

        await session.TriggerSoftwareAsync(cancellationToken).ConfigureAwait(false);

        return new { cameraId = command.CameraId, triggered = true };
    }
}

/// <summary>capture.waitSettled 实现。</summary>
public sealed class CaptureWaitSettledCapability : ICapabilityHandler<CaptureWaitSettledCommand>
{
    private readonly IReadinessGate _gate;

    public CaptureWaitSettledCapability(IReadinessGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _gate = gate;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CaptureWaitSettledCommand.Id,
        Title: "等待就绪",
        Description:
            "等待系统稳定到可采集。当前只有固定延时判据（占位），" +
            "视觉振动判据尚未实现——返回的 source=FixedDelay 是明确标注，不要当成真实振动判定。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CaptureWaitSettledCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "timeoutMs": { "type": "integer", "default": 2000, "minimum": 1 },
            "fixedDelayMs": { "type": "integer", "default": 0, "minimum": 0 }
          }
        }
        """);

    public async Task<object?> HandleAsync(
        CaptureWaitSettledCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        ReadinessCriteria criteria = ReadinessCriteria.FixedDelayPlaceholder(
            TimeSpan.FromMilliseconds(Math.Max(0, command.FixedDelayMs)),
            TimeSpan.FromMilliseconds(Math.Max(1, command.TimeoutMs)));

        ReadinessResult result = await _gate.WaitAsync(criteria, cancellationToken).ConfigureAwait(false);

        return new
        {
            ready = result.Ready,
            waitedMs = (int)result.Waited.TotalMilliseconds,
            source = result.Source.ToString(),
            amplitude = double.IsNaN(result.MeasuredAmplitude) ? (double?)null : result.MeasuredAmplitude
        };
    }
}

/// <summary>capture.getStats 实现。</summary>
public sealed class CaptureGetStatsCapability : ICapabilityHandler<CaptureGetStatsCommand>
{
    private readonly ICaptureStatistics _statistics;

    public CaptureGetStatsCapability(ICaptureStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        _statistics = statistics;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CaptureGetStatsCommand.Id,
        Title: "采集统计",
        Description: "返回某台相机的累计成功/失败帧数与最后一帧信息。只统计本进程内的采集。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CaptureGetStatsCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "cameraId": { "type": "string" } }, "required": ["cameraId"] }
        """);

    public Task<object?> HandleAsync(
        CaptureGetStatsCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        CaptureStatistics stats = _statistics.Get(new CameraId(command.CameraId));

        return Task.FromResult<object?>(stats);
    }
}

/// <summary>capture.startContinuous 实现。</summary>
public sealed class CaptureStartContinuousCapability : ICapabilityHandler<CaptureStartContinuousCommand>
{
    private readonly ICapturePreviewService _preview;

    public CaptureStartContinuousCapability(ICapturePreviewService preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        _preview = preview;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CaptureStartContinuousCommand.Id,
        Title: "开始连续预览",
        Description:
            "后台按指定帧率持续取帧，只保留最新一帧（内存）。用于取景/调参；" +
            "像素不落盘。用 capture.getFrame 读取，capture.stopContinuous 停止。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CaptureStartContinuousCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "cameraId": { "type": "string" },
            "fps": { "type": "integer", "default": 10, "minimum": 1, "maximum": 60 }
          },
          "required": ["cameraId"]
        }
        """);

    public async Task<object?> HandleAsync(
        CaptureStartContinuousCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        await _preview.StartAsync(new CameraId(command.CameraId), command.Fps, cancellationToken)
            .ConfigureAwait(false);

        return new { cameraId = command.CameraId, previewing = true, fps = command.Fps };
    }
}

/// <summary>capture.stopContinuous 实现。</summary>
public sealed class CaptureStopContinuousCapability : ICapabilityHandler<CaptureStopContinuousCommand>
{
    private readonly ICapturePreviewService _preview;

    public CaptureStopContinuousCapability(ICapturePreviewService preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        _preview = preview;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CaptureStopContinuousCommand.Id,
        Title: "停止连续预览",
        Description: "停止后台取帧并丢弃缓存帧。未在预览时视为成功（幂等）。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CaptureStopContinuousCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "cameraId": { "type": "string" } }, "required": ["cameraId"] }
        """);

    public async Task<object?> HandleAsync(
        CaptureStopContinuousCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        await _preview.StopAsync(new CameraId(command.CameraId)).ConfigureAwait(false);

        return new { cameraId = command.CameraId, previewing = false };
    }
}
