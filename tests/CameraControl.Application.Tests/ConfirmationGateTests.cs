using CameraControl.Application.Auditing;
using CameraControl.Application.Commands;
using CameraControl.Application.Diagnostics;
using CameraControl.Application.Tests.Fakes;
using Xunit;

namespace CameraControl.Application.Tests;

/// <summary>
/// 人工确认门。
///
/// 这是 P4 解锁"AI 操作物理设备"的关键：AI 请求 → 弹窗问人 → 人批准才执行。
/// 核心要求：**默认关闭**（没装门就拒绝），且拒绝时处理器绝不执行。
/// </summary>
public sealed class ConfirmationGateTests
{
    private sealed class MoveCommand : ICommand
    {
        public string CapabilityId => "robot.moveTo";
    }

    private sealed class MoveHandler : ICapabilityHandler<MoveCommand>
    {
        public int Invocations { get; private set; }

        public CapabilityDescriptor Descriptor { get; } = new(
            Id: "robot.moveTo",
            Title: "机器人运动",
            Description: "物理动作",
            Risk: RiskLevel.Physical,
            CommandType: typeof(MoveCommand));

        public Task<object?> HandleAsync(MoveCommand command, CommandContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult<object?>(null);
        }
    }

    private sealed class StubGate : IConfirmationGate
    {
        private readonly bool _approve;

        public StubGate(bool approve) => _approve = approve;

        public int Requests { get; private set; }

        public CapabilityDescriptor? LastDescriptor { get; private set; }

        public Task<bool> RequestAsync(
            CapabilityDescriptor descriptor,
            Actor actor,
            string? summary,
            CancellationToken cancellationToken)
        {
            Requests++;
            LastDescriptor = descriptor;
            return Task.FromResult(_approve);
        }
    }

    private static (CommandBus Bus, MoveHandler Handler, StubGate Gate, InMemoryMetrics Metrics) Build(bool approve, bool installGate = true)
    {
        var registry = new CapabilityRegistry();
        var handler = new MoveHandler();
        registry.Register(handler);

        var gate = new StubGate(approve);
        var metrics = new InMemoryMetrics();

        var bus = new CommandBus(
            registry,
            new DefaultRiskPolicy(),
            new InMemoryAuditLog(),
            new FakeClock(),
            metrics,
            new FakeAppLogger(),
            installGate ? [gate] : []);

        return (bus, handler, gate, metrics);
    }

    [Fact]
    public async Task 人工批准后应执行()
    {
        (CommandBus bus, MoveHandler handler, StubGate gate, InMemoryMetrics metrics) = Build(approve: true);

        CommandResult result = await bus.ExecuteAsync(new MoveCommand(), Actor.Ai("agent"));

        Assert.True(result.Ok);
        Assert.Equal(1, handler.Invocations);
        Assert.Equal(1, gate.Requests);
        Assert.Equal("robot.moveTo", gate.LastDescriptor!.Id);

        Assert.Equal(1, metrics.Snapshot()["command.robot.moveTo.confirmation_requested"]);
        Assert.Equal(1, metrics.Snapshot()["command.robot.moveTo.confirmation_approved"]);
    }

    [Fact]
    public async Task 人工拒绝后绝不执行()
    {
        (CommandBus bus, MoveHandler handler, StubGate gate, InMemoryMetrics metrics) = Build(approve: false);

        CommandResult result = await bus.ExecuteAsync(new MoveCommand(), Actor.Ai("agent"));

        Assert.False(result.Ok);
        Assert.Equal(CommandErrorCodes.Cancelled, result.Error!.Code);

        // 关键：处理器一次都没被调用
        Assert.Equal(0, handler.Invocations);
        Assert.Equal(1, gate.Requests);
        Assert.Equal(1, metrics.Snapshot()["command.robot.moveTo.confirmation_denied"]);
    }

    [Fact]
    public async Task 未装确认门时保持拒绝()
    {
        (CommandBus bus, MoveHandler handler, _, _) = Build(approve: true, installGate: false);

        CommandResult result = await bus.ExecuteAsync(new MoveCommand(), Actor.Ai("agent"));

        Assert.False(result.Ok);
        Assert.Equal(CommandErrorCodes.ConfirmationRequired, result.Error!.Code);
        Assert.Equal(0, handler.Invocations);
    }

    [Fact]
    public async Task 人类直接调用物理操作不需要确认门()
    {
        (CommandBus bus, MoveHandler handler, StubGate gate, _) = Build(approve: true);

        CommandResult result = await bus.ExecuteAsync(new MoveCommand(), Actor.Human("operator"));

        Assert.True(result.Ok);
        Assert.Equal(1, handler.Invocations);

        // 人类点击本身即为意图表达，不弹窗
        Assert.Equal(0, gate.Requests);
    }
}
