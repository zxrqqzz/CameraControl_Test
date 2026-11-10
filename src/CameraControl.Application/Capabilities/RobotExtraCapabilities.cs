using CameraControl.Application.Commands;
using CameraControl.Domain.Ports;
using CameraControl.Domain.Robotics;

namespace CameraControl.Application.Capabilities;

// ── 命令 ────────────────────────────────────────────────

/// <summary>robot.getPose：只读位姿。</summary>
public sealed record RobotGetPoseCommand : ICommand
{
    public const string Id = "robot.getPose";

    public string CapabilityId => Id;
}

/// <summary>robot.moveRelative：相对当前位姿移动。</summary>
public sealed record RobotMoveRelativeCommand(
    double Dx, double Dy, double Dz,
    double Drx = 0, double Dry = 0, double Drz = 0,
    int TimeoutMs = 30000) : ICommand
{
    public const string Id = "robot.moveRelative";

    public string CapabilityId => Id;
}

/// <summary>robot.checkSoftLimit：检查位姿是否在软限位内。</summary>
public sealed record RobotCheckSoftLimitCommand(
    double? X = null, double? Y = null, double? Z = null,
    double? Rx = null, double? Ry = null, double? Rz = null) : ICommand
{
    public const string Id = "robot.checkSoftLimit";

    public string CapabilityId => Id;
}

/// <summary>robot.getUserFrame：读取用户坐标系。</summary>
public sealed record RobotGetUserFrameCommand(int Index = 1) : ICommand
{
    public const string Id = "robot.getUserFrame";

    public string CapabilityId => Id;
}

/// <summary>robot.setUserFrame：设置用户坐标系。</summary>
public sealed record RobotSetUserFrameCommand(
    int Index,
    double X, double Y, double Z,
    double Rx, double Ry, double Rz) : ICommand
{
    public const string Id = "robot.setUserFrame";

    public string CapabilityId => Id;
}

/// <summary>运动结果投影。</summary>
public sealed record RobotMotionProjection(
    bool Ok,
    RobotPose? Pose,
    string? ErrorCode,
    string? Message);

// ── 实现 ────────────────────────────────────────────────

/// <summary>robot.getPose 实现。</summary>
public sealed class RobotGetPoseCapability : ICapabilityHandler<RobotGetPoseCommand>
{
    private readonly IRobot _robot;

    public RobotGetPoseCapability(IRobot robot)
    {
        ArgumentNullException.ThrowIfNull(robot);
        _robot = robot;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: RobotGetPoseCommand.Id,
        Title: "读取机器人位姿",
        Description: "返回当前笛卡尔位姿（mm / 度）。未连接时返回 pose=null 并说明原因。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(RobotGetPoseCommand),
        ParametersJsonSchema: """{ "type": "object", "properties": {} }""");

    public async Task<object?> HandleAsync(
        RobotGetPoseCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        RobotState state = await _robot.GetStateAsync(cancellationToken).ConfigureAwait(false);

        return new
        {
            connected = state.State == RobotConnectionState.Connected,
            pose = state.Pose is null ? null : new
            {
                x = state.Pose.X,
                y = state.Pose.Y,
                z = state.Pose.Z,
                rx = state.Pose.Rx,
                ry = state.Pose.Ry,
                rz = state.Pose.Rz
            },
            at = state.At,
            message = state.Message
        };
    }
}

/// <summary>robot.moveRelative 实现（物理动作，需确认）。</summary>
public sealed class RobotMoveRelativeCapability : ICapabilityHandler<RobotMoveRelativeCommand>
{
    private readonly IRobot _robot;
    private readonly IRobotSafetyLimits _limits;

    public RobotMoveRelativeCapability(IRobot robot, IRobotSafetyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(robot);
        ArgumentNullException.ThrowIfNull(limits);
        _robot = robot;
        _limits = limits;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: RobotMoveRelativeCommand.Id,
        Title: "相对移动",
        Description:
            "按增量移动（mm / 度）。需要当前位姿作为基准；越界会被软限位拒绝（若已配置限位）。",
        Risk: RiskLevel.Physical,
        CommandType: typeof(RobotMoveRelativeCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "dx": { "type": "number" }, "dy": { "type": "number" }, "dz": { "type": "number" },
            "drx": { "type": "number", "default": 0 },
            "dry": { "type": "number", "default": 0 },
            "drz": { "type": "number", "default": 0 },
            "timeoutMs": { "type": "integer", "default": 30000 }
          },
          "required": ["dx", "dy", "dz"]
        }
        """);

    public async Task<object?> HandleAsync(
        RobotMoveRelativeCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        RobotState state = await _robot.GetStateAsync(cancellationToken).ConfigureAwait(false);

        if (state.State != RobotConnectionState.Connected || state.Pose is null)
        {
            return new RobotMotionProjection(
                false,
                null,
                RobotErrorCodes.NotConnected,
                $"无法读取当前位姿，拒绝相对移动：{state.Message}");
        }

        var target = new RobotPose(
            state.Pose.X + command.Dx,
            state.Pose.Y + command.Dy,
            state.Pose.Z + command.Dz,
            state.Pose.Rx + command.Drx,
            state.Pose.Ry + command.Dry,
            state.Pose.Rz + command.Drz);

        IReadOnlyList<string> violations = _limits.Validate(target);

        if (violations.Count > 0)
        {
            return new RobotMotionProjection(
                false,
                null,
                RobotErrorCodes.Rejected,
                "目标位姿越界：" + string.Join("；", violations));
        }

        RobotMotionResult result = await _robot
            .MoveToAsync(target, TimeSpan.FromMilliseconds(Math.Max(1, command.TimeoutMs)), cancellationToken)
            .ConfigureAwait(false);

        return new RobotMotionProjection(result.Ok, result.Pose, result.ErrorCode, result.Message);
    }
}

/// <summary>robot.checkSoftLimit 实现。</summary>
public sealed class RobotCheckSoftLimitCapability : ICapabilityHandler<RobotCheckSoftLimitCommand>
{
    private readonly IRobot _robot;
    private readonly IRobotSafetyLimits _limits;

    public RobotCheckSoftLimitCapability(IRobot robot, IRobotSafetyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(robot);
        ArgumentNullException.ThrowIfNull(limits);
        _robot = robot;
        _limits = limits;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: RobotCheckSoftLimitCommand.Id,
        Title: "检查软限位",
        Description:
            "检查给定位姿是否在软限位内；未提供坐标时检查当前位姿。" +
            "未配置限位时 configured=false（明确说明，不假装已检查）。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(RobotCheckSoftLimitCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "x": { "type": "number" }, "y": { "type": "number" }, "z": { "type": "number" },
            "rx": { "type": "number" }, "ry": { "type": "number" }, "rz": { "type": "number" }
          }
        }
        """);

    public async Task<object?> HandleAsync(
        RobotCheckSoftLimitCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        RobotPose pose;

        if (command.X is not null && command.Y is not null && command.Z is not null)
        {
            pose = new RobotPose(
                command.X.Value,
                command.Y.Value,
                command.Z.Value,
                command.Rx ?? 0,
                command.Ry ?? 0,
                command.Rz ?? 0);
        }
        else
        {
            RobotState state = await _robot.GetStateAsync(cancellationToken).ConfigureAwait(false);

            if (state.Pose is null)
            {
                return new
                {
                    configured = _limits.HasLimits,
                    checkedPose = false,
                    safe = (bool?)null,
                    violations = Array.Empty<string>(),
                    message = $"未提供坐标且无法读取当前位姿：{state.Message}"
                };
            }

            pose = state.Pose;
        }

        IReadOnlyList<string> violations = _limits.Validate(pose);

        return new
        {
            configured = _limits.HasLimits,
            checkedPose = true,
            safe = violations.Count == 0,
            violations,
            message = _limits.HasLimits
                ? (violations.Count == 0 ? "在软限位内" : "越界")
                : "未配置软限位（settings 中 robot.softLimits 全为空），本次结果不代表安全。"
        };
    }
}

/// <summary>robot.getUserFrame 实现（当前未实现）。</summary>
public sealed class RobotGetUserFrameCapability : ICapabilityHandler<RobotGetUserFrameCommand>
{
    public CapabilityDescriptor Descriptor { get; } = new(
        Id: RobotGetUserFrameCommand.Id,
        Title: "读取用户坐标系",
        Description: "读取 UF 坐标系。当前 YMConnect 适配器尚未映射该接口，返回 ROBOT_MODE_NOT_IMPLEMENTED。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(RobotGetUserFrameCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "index": { "type": "integer", "default": 1, "minimum": 1, "maximum": 63 } } }
        """);

    public Task<object?> HandleAsync(
        RobotGetUserFrameCommand command,
        CommandContext context,
        CancellationToken cancellationToken) =>
        throw new CameraOperationException(
            RobotErrorCodes.NotImplemented,
            "用户坐标系读取尚未接入（YMConnect 该接口的托管映射未确认）。" +
            "需要时用 robot.getUserFrame 之外的途径从控制器读取 UF 参数。");
}

/// <summary>robot.setUserFrame 实现（当前未实现）。</summary>
public sealed class RobotSetUserFrameCapability : ICapabilityHandler<RobotSetUserFrameCommand>
{
    public CapabilityDescriptor Descriptor { get; } = new(
        Id: RobotSetUserFrameCommand.Id,
        Title: "设置用户坐标系",
        Description: "写 UF 坐标系。当前未接入，返回 ROBOT_MODE_NOT_IMPLEMENTED。",
        Risk: RiskLevel.Physical,
        CommandType: typeof(RobotSetUserFrameCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "index": { "type": "integer", "minimum": 1, "maximum": 63 },
            "x": { "type": "number" }, "y": { "type": "number" }, "z": { "type": "number" },
            "rx": { "type": "number" }, "ry": { "type": "number" }, "rz": { "type": "number" }
          },
          "required": ["index", "x", "y", "z", "rx", "ry", "rz"]
        }
        """);

    public Task<object?> HandleAsync(
        RobotSetUserFrameCommand command,
        CommandContext context,
        CancellationToken cancellationToken) =>
        throw new CameraOperationException(
            RobotErrorCodes.NotImplemented,
            "用户坐标系写入尚未接入（避免误改控制器参数）。确认接口映射后再实现。");
}
