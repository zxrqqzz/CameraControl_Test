using CameraControl.Application.Commands;
using CameraControl.Domain.Robotics;

namespace CameraControl.Application.Capabilities;

/// <summary>robot.getState 命令：读取机器人状态。</summary>
public sealed record RobotGetStateCommand : ICommand
{
    public const string Id = "robot.getState";

    public string CapabilityId => Id;
}

/// <summary>robot.stop 命令：立即停止（安全动作）。</summary>
public sealed record RobotStopCommand : ICommand
{
    public const string Id = "robot.stop";

    public string CapabilityId => Id;
}

/// <summary>robot.moveTo 命令：绝对位姿运动（物理动作）。</summary>
/// <param name="Target">目标位姿。</param>
/// <param name="TimeoutMs">超时（毫秒）。</param>
public sealed record RobotMoveToCommand(RobotPose Target, int TimeoutMs = 30000) : ICommand
{
    public const string Id = "robot.moveTo";

    public string CapabilityId => Id;
}

/// <summary>机器人状态投影。</summary>
public sealed record RobotStateDto(
    string State,
    bool IsMoving,
    double? X,
    double? Y,
    double? Z,
    double? Rx,
    double? Ry,
    double? Rz,
    int? UserFrame,
    DateTimeOffset At,
    string? Message);

/// <summary>robot.getState 实现。</summary>
public sealed class RobotGetStateCapability : ICapabilityHandler<RobotGetStateCommand>
{
    private readonly IRobot _robot;

    public RobotGetStateCapability(IRobot robot)
    {
        ArgumentNullException.ThrowIfNull(robot);
        _robot = robot;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: RobotGetStateCommand.Id,
        Title: "读取机器人状态",
        Description: "返回机器人连接状态与当前位姿。未接入控制器时 state=Disconnected，这是明确答案而非错误。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(RobotGetStateCommand),
        ParametersJsonSchema: """{ "type": "object", "properties": {} }""");

    public async Task<object?> HandleAsync(
        RobotGetStateCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        RobotState state = await _robot.GetStateAsync(cancellationToken).ConfigureAwait(false);

        return new RobotStateDto(
            state.State.ToString(),
            state.IsMoving,
            state.Pose?.X,
            state.Pose?.Y,
            state.Pose?.Z,
            state.Pose?.Rx,
            state.Pose?.Ry,
            state.Pose?.Rz,
            state.Pose?.UserFrame,
            state.At,
            state.Message);
    }
}

/// <summary>
/// robot.stop 实现。
///
/// **安全动作**：风险等级定为 Reversible，不要求确认——
/// 危险时刻不该因为"要不要弹窗"而耽误急停。
/// </summary>
public sealed class RobotStopCapability : ICapabilityHandler<RobotStopCommand>
{
    private readonly IRobot _robot;

    public RobotStopCapability(IRobot robot)
    {
        ArgumentNullException.ThrowIfNull(robot);
        _robot = robot;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: RobotStopCommand.Id,
        Title: "停止机器人",
        Description: "立即停止机器人当前运动。安全动作，不需要确认，AI 也可以直接调用。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(RobotStopCommand),
        ParametersJsonSchema: """{ "type": "object", "properties": {} }""");

    public async Task<object?> HandleAsync(
        RobotStopCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        await _robot.StopAsync(cancellationToken).ConfigureAwait(false);
        return new { stopped = true, adapter = _robot.AdapterName };
    }
}

/// <summary>robot.moveTo 实现（物理动作，需人工确认）。</summary>
public sealed class RobotMoveToCapability : ICapabilityHandler<RobotMoveToCommand>
{
    private readonly IRobot _robot;

    public RobotMoveToCapability(IRobot robot)
    {
        ArgumentNullException.ThrowIfNull(robot);
        _robot = robot;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: RobotMoveToCommand.Id,
        Title: "机器人运动到指定位姿",
        Description: "让机器人移动到绝对位姿（mm / 度）。属于物理动作，必须人工确认后才能执行。",
        Risk: RiskLevel.Physical,
        CommandType: typeof(RobotMoveToCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "x": { "type": "number", "description": "X（mm）" },
            "y": { "type": "number", "description": "Y（mm）" },
            "z": { "type": "number", "description": "Z（mm）" },
            "rx": { "type": "number", "description": "绕 X 旋转（度）" },
            "ry": { "type": "number", "description": "绕 Y 旋转（度）" },
            "rz": { "type": "number", "description": "绕 Z 旋转（度）" },
            "userFrame": { "type": "integer", "minimum": 1, "maximum": 63, "default": 1 },
            "timeoutMs": { "type": "integer", "minimum": 100, "maximum": 600000, "default": 30000 }
          },
          "required": ["x", "y", "z", "rx", "ry", "rz"]
        }
        """);

    public async Task<object?> HandleAsync(
        RobotMoveToCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        RobotMotionResult result = await _robot
            .MoveToAsync(command.Target, TimeSpan.FromMilliseconds(command.TimeoutMs), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Ok)
        {
            throw new Domain.Ports.CameraOperationException(
                result.ErrorCode ?? RobotErrorCodes.Rejected,
                result.Message ?? "机器人运动失败。");
        }

        return new
        {
            ok = true,
            x = result.Pose?.X,
            y = result.Pose?.Y,
            z = result.Pose?.Z
        };
    }
}
