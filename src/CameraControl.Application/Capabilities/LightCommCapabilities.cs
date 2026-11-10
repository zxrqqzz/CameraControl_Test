using CameraControl.Application.Commands;
using CameraControl.Application.Comm;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Capabilities;

// ── 光源 ────────────────────────────────────────────────

/// <summary>light.on：点亮。</summary>
public sealed record LightOnCommand(int Channel = 1) : ICommand
{
    public const string Id = "light.on";

    public string CapabilityId => Id;
}

/// <summary>light.off：熄灭。</summary>
public sealed record LightOffCommand(int Channel = 1) : ICommand
{
    public const string Id = "light.off";

    public string CapabilityId => Id;
}

/// <summary>light.setBrightness：设置亮度。</summary>
public sealed record LightSetBrightnessCommand(int Channel = 1, double Percent = 50) : ICommand
{
    public const string Id = "light.setBrightness";

    public string CapabilityId => Id;
}

/// <summary>light.pulse：频闪一次。</summary>
public sealed record LightPulseCommand(int Channel = 1, int DurationUs = 1000) : ICommand
{
    public const string Id = "light.pulse";

    public string CapabilityId => Id;
}

/// <summary>light.strobe：连续频闪（由调用方决定何时停止，这里只发一次设定）。</summary>
public sealed record LightStrobeCommand(int Channel = 1, int DurationUs = 1000, int Count = 1) : ICommand
{
    public const string Id = "light.strobe";

    public string CapabilityId => Id;
}

/// <summary>light.getState：读取状态。</summary>
public sealed record LightGetStateCommand(int Channel = 1) : ICommand
{
    public const string Id = "light.getState";

    public string CapabilityId => Id;
}

/// <summary>light.list：列出光源适配器信息。</summary>
public sealed record LightListCommand : ICommand
{
    public const string Id = "light.list";

    public string CapabilityId => Id;
}

internal static class LightCapabilityHelper
{
    public static void ValidateChannel(int channel)
    {
        if (channel < 1 || channel > 16)
            throw new CameraOperationException(LightErrorCodes.InvalidParameter, "通道号必须在 1–16 之间。");
    }
}

/// <summary>light.on 实现。</summary>
public sealed class LightOnCapability : ICapabilityHandler<LightOnCommand>
{
    private readonly ILightPort _light;

    public LightOnCapability(ILightPort light)
    {
        ArgumentNullException.ThrowIfNull(light);
        _light = light;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: LightOnCommand.Id,
        Title: "点亮光源",
        Description: "点亮指定通道。未接入光源控制器时返回 LIGHT_NOT_CONFIGURED。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(LightOnCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "channel": { "type": "integer", "default": 1, "minimum": 1, "maximum": 16 } } }
        """);

    public async Task<object?> HandleAsync(
        LightOnCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        LightCapabilityHelper.ValidateChannel(command.Channel);
        await _light.OnAsync(command.Channel, cancellationToken).ConfigureAwait(false);

        return new { channel = command.Channel, isOn = true };
    }
}

/// <summary>light.off 实现。</summary>
public sealed class LightOffCapability : ICapabilityHandler<LightOffCommand>
{
    private readonly ILightPort _light;

    public LightOffCapability(ILightPort light)
    {
        ArgumentNullException.ThrowIfNull(light);
        _light = light;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: LightOffCommand.Id,
        Title: "熄灭光源",
        Description: "熄灭指定通道。未接入光源时返回 LIGHT_NOT_CONFIGURED。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(LightOffCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "channel": { "type": "integer", "default": 1 } } }
        """);

    public async Task<object?> HandleAsync(
        LightOffCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        LightCapabilityHelper.ValidateChannel(command.Channel);
        await _light.OffAsync(command.Channel, cancellationToken).ConfigureAwait(false);

        return new { channel = command.Channel, isOn = false };
    }
}

/// <summary>light.setBrightness 实现。</summary>
public sealed class LightSetBrightnessCapability : ICapabilityHandler<LightSetBrightnessCommand>
{
    private readonly ILightPort _light;

    public LightSetBrightnessCapability(ILightPort light)
    {
        ArgumentNullException.ThrowIfNull(light);
        _light = light;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: LightSetBrightnessCommand.Id,
        Title: "设置亮度",
        Description: "设置指定通道亮度（0–100%）。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(LightSetBrightnessCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "channel": { "type": "integer", "default": 1 },
            "percent": { "type": "number", "default": 50, "minimum": 0, "maximum": 100 }
          }
        }
        """);

    public async Task<object?> HandleAsync(
        LightSetBrightnessCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        LightCapabilityHelper.ValidateChannel(command.Channel);

        if (command.Percent is < 0 or > 100)
            throw new CameraOperationException(LightErrorCodes.InvalidParameter, "亮度必须在 0–100 之间。");

        await _light.SetBrightnessAsync(command.Channel, command.Percent, cancellationToken).ConfigureAwait(false);

        return new { channel = command.Channel, percent = command.Percent };
    }
}

/// <summary>light.pulse 实现。</summary>
public sealed class LightPulseCapability : ICapabilityHandler<LightPulseCommand>
{
    private readonly ILightPort _light;

    public LightPulseCapability(ILightPort light)
    {
        ArgumentNullException.ThrowIfNull(light);
        _light = light;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: LightPulseCommand.Id,
        Title: "频闪一次",
        Description:
            "在指定时长内点亮，用于配合短曝光。需要与相机曝光时间对齐（P2 多相机同一脉冲）。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(LightPulseCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "channel": { "type": "integer", "default": 1 },
            "durationUs": { "type": "integer", "default": 1000, "minimum": 1 }
          }
        }
        """);

    public async Task<object?> HandleAsync(
        LightPulseCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        LightCapabilityHelper.ValidateChannel(command.Channel);

        if (command.DurationUs <= 0)
            throw new CameraOperationException(LightErrorCodes.InvalidParameter, "频闪时长必须大于 0 µs。");

        await _light.StrobeAsync(command.Channel, command.DurationUs, cancellationToken).ConfigureAwait(false);

        return new { channel = command.Channel, durationUs = command.DurationUs };
    }
}

/// <summary>light.strobe 实现（多次频闪）。</summary>
public sealed class LightStrobeCapability : ICapabilityHandler<LightStrobeCommand>
{
    private readonly ILightPort _light;

    public LightStrobeCapability(ILightPort light)
    {
        ArgumentNullException.ThrowIfNull(light);
        _light = light;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: LightStrobeCommand.Id,
        Title: "连续频闪",
        Description: "连续频闪 N 次（每次点亮 durationUs）。用于多帧抓拍。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(LightStrobeCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "channel": { "type": "integer", "default": 1 },
            "durationUs": { "type": "integer", "default": 1000, "minimum": 1 },
            "count": { "type": "integer", "default": 1, "minimum": 1, "maximum": 100 }
          }
        }
        """);

    public async Task<object?> HandleAsync(
        LightStrobeCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        LightCapabilityHelper.ValidateChannel(command.Channel);

        if (command.DurationUs <= 0)
            throw new CameraOperationException(LightErrorCodes.InvalidParameter, "频闪时长必须大于 0 µs。");

        if (command.Count is < 1 or > 100)
            throw new CameraOperationException(LightErrorCodes.InvalidParameter, "频闪次数必须在 1–100 之间。");

        for (int i = 0; i < command.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _light.StrobeAsync(command.Channel, command.DurationUs, cancellationToken).ConfigureAwait(false);
        }

        return new { channel = command.Channel, durationUs = command.DurationUs, count = command.Count };
    }
}

/// <summary>light.getState 实现。</summary>
public sealed class LightGetStateCapability : ICapabilityHandler<LightGetStateCommand>
{
    private readonly ILightPort _light;

    public LightGetStateCapability(ILightPort light)
    {
        ArgumentNullException.ThrowIfNull(light);
        _light = light;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: LightGetStateCommand.Id,
        Title: "读取光源状态",
        Description: "返回指定通道的开关与亮度。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(LightGetStateCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "channel": { "type": "integer", "default": 1 } } }
        """);

    public async Task<object?> HandleAsync(
        LightGetStateCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        LightCapabilityHelper.ValidateChannel(command.Channel);

        LightState state = await _light.GetStateAsync(command.Channel, cancellationToken).ConfigureAwait(false);

        return state;
    }
}

/// <summary>light.list 实现。</summary>
public sealed class LightListCapability : ICapabilityHandler<LightListCommand>
{
    private readonly ILightPort _light;

    public LightListCapability(ILightPort light)
    {
        ArgumentNullException.ThrowIfNull(light);
        _light = light;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: LightListCommand.Id,
        Title: "光源适配器信息",
        Description: "返回当前光源适配器名称与可用性。未接入时 available=false（不是错误）。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(LightListCommand),
        ParametersJsonSchema: """{ "type": "object", "properties": {} }""");

    public Task<object?> HandleAsync(
        LightListCommand command,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>(new
        {
            adapter = _light.AdapterName,
            available = _light.IsAvailable
        });
}

// ── 通讯 ────────────────────────────────────────────────

/// <summary>comm.add：添加连接。</summary>
public sealed record CommAddCommand(string ConnectionId, string Kind, string Endpoint) : ICommand
{
    public const string Id = "comm.add";

    public string CapabilityId => Id;
}

/// <summary>comm.remove：移除连接。</summary>
public sealed record CommRemoveCommand(string ConnectionId) : ICommand
{
    public const string Id = "comm.remove";

    public string CapabilityId => Id;
}

/// <summary>comm.list：列出连接。</summary>
public sealed record CommListCommand : ICommand
{
    public const string Id = "comm.list";

    public string CapabilityId => Id;
}

/// <summary>comm.open：打开连接。</summary>
public sealed record CommOpenCommand(string ConnectionId) : ICommand
{
    public const string Id = "comm.open";

    public string CapabilityId => Id;
}

/// <summary>comm.close：关闭连接。</summary>
public sealed record CommCloseCommand(string ConnectionId) : ICommand
{
    public const string Id = "comm.close";

    public string CapabilityId => Id;
}

/// <summary>comm.send：发送十六进制数据。</summary>
public sealed record CommSendCommand(string ConnectionId, string Hex) : ICommand
{
    public const string Id = "comm.send";

    public string CapabilityId => Id;
}

/// <summary>comm.getLog：读取通讯日志。</summary>
public sealed record CommGetLogCommand(string ConnectionId, int MaxEntries = 50) : ICommand
{
    public const string Id = "comm.getLog";

    public string CapabilityId => Id;
}

/// <summary>comm.add 实现。</summary>
public sealed class CommAddCapability : ICapabilityHandler<CommAddCommand>
{
    private readonly ICommManager _manager;

    public CommAddCapability(ICommManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CommAddCommand.Id,
        Title: "添加通讯连接",
        Description:
            "登记一条 PLC/串口连接。暂未接入任何驱动，会返回 COMM_NOT_CONFIGURED 并说明支持的类型。" +
            "这是明确答案，不要重试同一参数。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CommAddCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "kind": { "type": "string", "enum": ["ModbusTcp", "Serial", "Tcp", "Unknown"] },
            "endpoint": { "type": "string", "description": "如 192.168.0.10:502 或 COM3:9600" }
          },
          "required": ["id", "kind", "endpoint"]
        }
        """);

    public Task<object?> HandleAsync(
        CommAddCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse(command.Kind, ignoreCase: true, out CommKind kind))
            throw new CameraOperationException(CommErrorCodes.NotConfigured, $"未知连接类型 {command.Kind}。");

        ICommChannel channel = _manager.Add(new CommEndpoint(command.ConnectionId, kind, command.Endpoint));

        return Task.FromResult<object?>(new
        {
            id = channel.Endpoint.Id,
            kind = channel.Endpoint.Kind.ToString(),
            endpoint = channel.Endpoint.Endpoint,
            isOpen = channel.IsOpen
        });
    }
}

/// <summary>comm.remove 实现。</summary>
public sealed class CommRemoveCapability : ICapabilityHandler<CommRemoveCommand>
{
    private readonly ICommManager _manager;

    public CommRemoveCapability(ICommManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CommRemoveCommand.Id,
        Title: "移除通讯连接",
        Description: "移除并关闭连接。不存在时视为成功（幂等）。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CommRemoveCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] }
        """);

    public async Task<object?> HandleAsync(
        CommRemoveCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        await _manager.RemoveAsync(command.ConnectionId).ConfigureAwait(false);

        return new { id = command.ConnectionId, removed = true };
    }
}

/// <summary>comm.list 实现。</summary>
public sealed class CommListCapability : ICapabilityHandler<CommListCommand>
{
    private readonly ICommManager _manager;

    public CommListCapability(ICommManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CommListCommand.Id,
        Title: "列出通讯连接",
        Description: "返回已登记的连接及支持的驱动类型。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CommListCommand),
        ParametersJsonSchema: """{ "type": "object", "properties": {} }""");

    public Task<object?> HandleAsync(
        CommListCommand command,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>(new
        {
            supportedKinds = _manager.SupportedKinds.Select(k => k.ToString()).ToArray(),
            connections = _manager.List().Select(c => new
            {
                id = c.Endpoint.Id,
                kind = c.Endpoint.Kind.ToString(),
                endpoint = c.Endpoint.Endpoint,
                isOpen = c.IsOpen
            }).ToArray()
        });
}

/// <summary>comm.open 实现。</summary>
public sealed class CommOpenCapability : ICapabilityHandler<CommOpenCommand>
{
    private readonly ICommManager _manager;

    public CommOpenCapability(ICommManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CommOpenCommand.Id,
        Title: "打开通讯连接",
        Description: "打开已登记的连接。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CommOpenCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] }
        """);

    public async Task<object?> HandleAsync(
        CommOpenCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        await _manager.OpenAsync(command.ConnectionId, cancellationToken).ConfigureAwait(false);

        return new { id = command.ConnectionId, isOpen = true };
    }
}

/// <summary>comm.close 实现。</summary>
public sealed class CommCloseCapability : ICapabilityHandler<CommCloseCommand>
{
    private readonly ICommManager _manager;

    public CommCloseCapability(ICommManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CommCloseCommand.Id,
        Title: "关闭通讯连接",
        Description: "关闭连接（保留登记）。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CommCloseCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] }
        """);

    public async Task<object?> HandleAsync(
        CommCloseCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        await _manager.CloseAsync(command.ConnectionId, cancellationToken).ConfigureAwait(false);

        return new { id = command.ConnectionId, isOpen = false };
    }
}

/// <summary>comm.send 实现。</summary>
public sealed class CommSendCapability : ICapabilityHandler<CommSendCommand>
{
    private readonly ICommManager _manager;

    public CommSendCapability(ICommManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CommSendCommand.Id,
        Title: "发送数据",
        Description: "以十六进制串发送数据（如 Modbus 帧 01 03 00 00 00 02 C4 0B）。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CommSendCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": { "id": { "type": "string" }, "hex": { "type": "string" } },
          "required": ["id", "hex"]
        }
        """);

    public async Task<object?> HandleAsync(
        CommSendCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        await _manager.SendHexAsync(command.ConnectionId, command.Hex, cancellationToken).ConfigureAwait(false);

        return new { id = command.ConnectionId, sent = true };
    }
}

/// <summary>comm.getLog 实现。</summary>
public sealed class CommGetLogCapability : ICapabilityHandler<CommGetLogCommand>
{
    private readonly ICommManager _manager;

    public CommGetLogCapability(ICommManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CommGetLogCommand.Id,
        Title: "通讯日志",
        Description: "返回最近的收发记录（内存态，进程退出即消失）。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CommGetLogCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "maxEntries": { "type": "integer", "default": 50, "minimum": 1, "maximum": 200 }
          },
          "required": ["id"]
        }
        """);

    public Task<object?> HandleAsync(
        CommGetLogCommand command,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>(_manager.GetLog(command.ConnectionId, command.MaxEntries));
}
