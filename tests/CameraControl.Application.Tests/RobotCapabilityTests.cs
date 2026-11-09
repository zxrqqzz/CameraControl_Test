using CameraControl.Application.Auditing;
using CameraControl.Application.Capabilities;
using CameraControl.Application.Commands;
using CameraControl.Application.Diagnostics;
using CameraControl.Application.Tests.Fakes;
using CameraControl.Domain.Robotics;
using Xunit;

namespace CameraControl.Application.Tests;

/// <summary>
/// 机器人能力（P5 骨架）。
///
/// 当前控制器未接入，用 <see cref="UnavailableRobot"/> 占位。
/// 这里验证的是**安全语义**而不是硬件行为：
/// 状态查询要给出明确答案、急停不能被拦、运动必须走人工确认。
/// </summary>
public sealed class RobotCapabilityTests
{
    private static (CommandBus Bus, CapabilityRegistry Registry) Build(bool withConfirmation = false)
    {
        var clock = new FakeClock();
        var robot = new UnavailableRobot(clock);

        var registry = new CapabilityRegistry()
            .Register(new RobotGetStateCapability(robot))
            .Register(new RobotStopCapability(robot))
            .Register(new RobotMoveToCapability(robot));

        IConfirmationGate[] gates = withConfirmation ? [new AlwaysApproveGate()] : [];

        var bus = new CommandBus(
            registry,
            new DefaultRiskPolicy(),
            new InMemoryAuditLog(),
            clock,
            new InMemoryMetrics(),
            new FakeAppLogger(),
            gates);

        return (bus, registry);
    }

    private sealed class AlwaysApproveGate : IConfirmationGate
    {
        public Task<bool> RequestAsync(
            CapabilityDescriptor descriptor,
            Actor actor,
            string? summary,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    [Fact]
    public async Task 未接入时应返回未连接而不是异常()
    {
        (CommandBus bus, _) = Build();

        CommandResult result = await bus.ExecuteAsync(new RobotGetStateCommand(), Actor.Human("op"));

        Assert.True(result.Ok);
        var dto = Assert.IsType<RobotStateDto>(result.Data);
        Assert.Equal("Disconnected", dto.State);
        Assert.Null(dto.X);
        Assert.Contains("未配置机器人", dto.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 急停对AI也应放行()
    {
        (CommandBus bus, _) = Build();

        // robot.stop 是 Reversible：安全动作不该因为确认流程被耽误
        CommandResult result = await bus.ExecuteAsync(new RobotStopCommand(), Actor.Ai("agent"));

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task AI直接运动应被拒绝且不触达设备()
    {
        (CommandBus bus, _) = Build();

        var target = new RobotPose(100, 200, 300, 0, 90, 0);
        CommandResult result = await bus.ExecuteAsync(new RobotMoveToCommand(target), Actor.Ai("agent"));

        Assert.False(result.Ok);
        Assert.Equal(CommandErrorCodes.ConfirmationRequired, result.Error!.Code);
    }

    [Fact]
    public async Task 人工确认后AI可运动但设备未接入时仍报未连接()
    {
        (CommandBus bus, _) = Build(withConfirmation: true);

        var target = new RobotPose(100, 200, 300, 0, 90, 0);
        CommandResult result = await bus.ExecuteAsync(new RobotMoveToCommand(target), Actor.Ai("agent"));

        // 确认门放行了，但真实设备没接——必须是明确错误，不能假装成功
        Assert.False(result.Ok);
        Assert.Equal(RobotErrorCodes.NotConnected, result.Error!.Code);
    }

    [Fact]
    public void 能力清单应包含机器人三项且风险等级正确()
    {
        (_, CapabilityRegistry registry) = Build();

        CapabilityDescriptor move = registry.All.Single(d => d.Id == RobotMoveToCommand.Id);
        CapabilityDescriptor stop = registry.All.Single(d => d.Id == RobotStopCommand.Id);
        CapabilityDescriptor state = registry.All.Single(d => d.Id == RobotGetStateCommand.Id);

        Assert.Equal(RiskLevel.Physical, move.Risk);
        Assert.True(move.RequiresConfirmation);

        // 急停必须免确认
        Assert.Equal(RiskLevel.Reversible, stop.Risk);
        Assert.False(stop.RequiresConfirmation);

        Assert.Equal(RiskLevel.ReadOnly, state.Risk);
        Assert.False(state.RequiresConfirmation);
    }
}
