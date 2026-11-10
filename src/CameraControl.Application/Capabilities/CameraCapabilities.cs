using CameraControl.Application.Capture;
using CameraControl.Application.Commands;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Capabilities;

// ── 命令 ────────────────────────────────────────────────

/// <summary>camera.open：打开相机会话。</summary>
public sealed record CameraOpenCommand(string CameraId) : ICommand
{
    public const string Id = "camera.open";

    public string CapabilityId => Id;
}

/// <summary>camera.close：关闭相机会话。</summary>
public sealed record CameraCloseCommand(string CameraId) : ICommand
{
    public const string Id = "camera.close";

    public string CapabilityId => Id;
}

/// <summary>camera.listOpen：列出已打开的会话。</summary>
public sealed record CameraListOpenCommand : ICommand
{
    public const string Id = "camera.listOpen";

    public string CapabilityId => Id;
}

/// <summary>camera.getStatus：查询某台相机的会话状态。</summary>
public sealed record CameraGetStatusCommand(string CameraId) : ICommand
{
    public const string Id = "camera.getStatus";

    public string CapabilityId => Id;
}

// ── DTO ─────────────────────────────────────────────────

/// <summary>相机会话状态投影。</summary>
public sealed record CameraSessionDto(
    string CameraId,
    string DisplayName,
    string Kind,
    bool IsOpen,
    bool IsInfrared,
    bool CanCapture);

/// <summary>已打开会话列表投影。</summary>
public sealed record CameraSessionListDto(
    int Count,
    IReadOnlyList<CameraSessionDto> Sessions);

// ── 实现 ────────────────────────────────────────────────

/// <summary>camera.open 实现。</summary>
public sealed class CameraOpenCapability : ICapabilityHandler<CameraOpenCommand>
{
    private readonly ICameraSessionRegistry _registry;
    private readonly ICameraProvider _provider;

    public CameraOpenCapability(ICameraSessionRegistry registry, ICameraProvider provider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(provider);
        _registry = registry;
        _provider = provider;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CameraOpenCommand.Id,
        Title: "打开相机",
        Description:
            "打开指定相机的会话，成功后可用 capture.* 取图。id 来自 device.list。" +
            "红外（Windows Hello）相机只列出、永不取流，打开会被拒绝。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CameraOpenCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": { "cameraId": { "type": "string", "description": "device.list 返回的相机 id" } },
          "required": ["cameraId"]
        }
        """);

    public async Task<object?> HandleAsync(
        CameraOpenCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        var id = new CameraId(command.CameraId);

        ICameraSession session = await _registry.OpenAsync(id, cancellationToken).ConfigureAwait(false);

        return ToDto(session);
    }

    internal static CameraSessionDto ToDto(ICameraSession session)
    {
        CameraDescriptor descriptor = session.Descriptor;

        return new CameraSessionDto(
            descriptor.Id.Value ?? string.Empty,
            descriptor.DisplayName,
            descriptor.Kind.ToString(),
            session.IsOpen,
            descriptor.IsInfraredOnly,
            descriptor.CanCapture);
    }
}

/// <summary>camera.close 实现。</summary>
public sealed class CameraCloseCapability : ICapabilityHandler<CameraCloseCommand>
{
    private readonly ICameraSessionRegistry _registry;

    public CameraCloseCapability(ICameraSessionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CameraCloseCommand.Id,
        Title: "关闭相机",
        Description: "关闭指定相机会话并释放设备。未打开时视为成功（幂等）。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CameraCloseCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": { "cameraId": { "type": "string" } },
          "required": ["cameraId"]
        }
        """);

    public async Task<object?> HandleAsync(
        CameraCloseCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        await _registry.CloseAsync(new CameraId(command.CameraId)).ConfigureAwait(false);

        return new { cameraId = command.CameraId, closed = true };
    }
}

/// <summary>camera.listOpen 实现。</summary>
public sealed class CameraListOpenCapability : ICapabilityHandler<CameraListOpenCommand>
{
    private readonly ICameraSessionRegistry _registry;

    public CameraListOpenCapability(ICameraSessionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CameraListOpenCommand.Id,
        Title: "列出已打开相机",
        Description: "返回当前已打开会话的相机列表。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CameraListOpenCommand),
        ParametersJsonSchema: """{ "type": "object", "properties": {} }""");

    public Task<object?> HandleAsync(
        CameraListOpenCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ICameraSession> sessions = _registry.OpenSessions;

        return Task.FromResult<object?>(new CameraSessionListDto(
            sessions.Count,
            [.. sessions.Select(CameraOpenCapability.ToDto)]));
    }
}

/// <summary>camera.getStatus 实现。</summary>
public sealed class CameraGetStatusCapability : ICapabilityHandler<CameraGetStatusCommand>
{
    private readonly ICameraSessionRegistry _registry;

    public CameraGetStatusCapability(ICameraSessionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CameraGetStatusCommand.Id,
        Title: "查询相机会话状态",
        Description: "查询一台相机是否已打开。未打开时返回 isOpen=false（不是错误）。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CameraGetStatusCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": { "cameraId": { "type": "string" } },
          "required": ["cameraId"]
        }
        """);

    public Task<object?> HandleAsync(
        CameraGetStatusCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        var id = new CameraId(command.CameraId);
        ICameraSession? session = _registry.Find(id);

        return Task.FromResult<object?>(new
        {
            cameraId = command.CameraId,
            isOpen = session is { IsOpen: true },
            descriptor = session is null ? null : CameraOpenCapability.ToDto(session)
        });
    }
}
