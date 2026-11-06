using CameraControl.Application.Auditing;
using CameraControl.Application.Commands;
using CameraControl.Application.Tests.Fakes;
using CameraControl.Domain.Capture;
using Xunit;

namespace CameraControl.Application.Tests;

/// <summary>
/// 命令总线：白名单、风险分级、审计。
/// 这是 CUA 安全模型的核心，必须被测试锁死。
/// </summary>
public sealed class CommandBusTests
{
    private sealed class EchoCommand : ICommand
    {
        public string CapabilityId => "test.echo";
    }

    private sealed class EchoHandler : ICapabilityHandler<EchoCommand>
    {
        public int Invocations { get; private set; }

        public CapabilityDescriptor Descriptor { get; } = new(
            Id: "test.echo",
            Title: "回声",
            Description: "测试用",
            Risk: RiskLevel.ReadOnly,
            CommandType: typeof(EchoCommand));

        public Task<object?> HandleAsync(EchoCommand command, CommandContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult<object?>("ok");
        }
    }

    private sealed class RobotMoveCommand : ICommand
    {
        public string CapabilityId => "robot.moveTo";
    }

    private sealed class RobotMoveHandler : ICapabilityHandler<RobotMoveCommand>
    {
        public int Invocations { get; private set; }

        public CapabilityDescriptor Descriptor { get; } = new(
            Id: "robot.moveTo",
            Title: "机器人运动",
            Description: "物理动作",
            Risk: RiskLevel.Physical,
            CommandType: typeof(RobotMoveCommand));

        public Task<object?> HandleAsync(RobotMoveCommand command, CommandContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult<object?>(null);
        }
    }

    private static (CommandBus Bus, InMemoryAuditLog Audit, EchoHandler Echo, RobotMoveHandler Robot) Build()
    {
        var registry = new CapabilityRegistry();
        var echo = new EchoHandler();
        var robot = new RobotMoveHandler();
        registry.Register(echo).Register(robot);

        var audit = new InMemoryAuditLog();
        var bus = new CommandBus(registry, new DefaultRiskPolicy(), audit, new FakeClock());
        return (bus, audit, echo, robot);
    }

    [Fact]
    public async Task 未知能力应被白名单拒绝()
    {
        (CommandBus bus, InMemoryAuditLog audit, _, _) = Build();

        CommandResult result = await bus.ExecuteAsync(new UnknownCommand(), Actor.Human("tester"));

        Assert.False(result.Ok);
        Assert.Equal(CommandErrorCodes.UnknownCapability, result.Error!.Code);

        // 被拒绝也要留痕
        IReadOnlyList<AuditEntry> entries = audit.Recent(10);
        Assert.Single(entries);
        Assert.False(entries[0].Ok);
    }

    private sealed class UnknownCommand : ICommand
    {
        public string CapabilityId => "does.not.exist";
    }

    [Fact]
    public async Task AI不能直接执行物理级能力()
    {
        (CommandBus bus, InMemoryAuditLog audit, _, RobotMoveHandler robot) = Build();

        CommandResult result = await bus.ExecuteAsync(new RobotMoveCommand(), Actor.Ai("gpt", "session-1"));

        Assert.False(result.Ok);
        Assert.Equal(CommandErrorCodes.ConfirmationRequired, result.Error!.Code);

        // 关键：处理器根本没有被调用
        Assert.Equal(0, robot.Invocations);

        AuditEntry entry = audit.Recent(1)[0];
        Assert.Equal(ActorKind.Ai, entry.Actor.Kind);
        Assert.False(entry.Ok);
    }

    [Fact]
    public async Task 人类可以执行物理级能力()
    {
        (CommandBus bus, _, _, RobotMoveHandler robot) = Build();

        CommandResult result = await bus.ExecuteAsync(new RobotMoveCommand(), Actor.Human("operator"));

        Assert.True(result.Ok);
        Assert.Equal(1, robot.Invocations);
    }

    [Fact]
    public async Task 只读能力AI可直接执行并留痕()
    {
        (CommandBus bus, InMemoryAuditLog audit, EchoHandler echo, _) = Build();

        CommandResult result = await bus.ExecuteAsync(new EchoCommand(), Actor.Ai("gpt"));

        Assert.True(result.Ok);
        Assert.Equal(1, echo.Invocations);

        AuditEntry entry = audit.Recent(1)[0];
        Assert.True(entry.Ok);
        Assert.Equal("test.echo", entry.CapabilityId);
        Assert.Equal(RiskLevel.ReadOnly, entry.Risk);
    }

    [Fact]
    public void 重复注册同一能力应抛异常()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new EchoHandler());

        Assert.Throws<InvalidOperationException>(() => registry.Register(new EchoHandler()));
    }
}
