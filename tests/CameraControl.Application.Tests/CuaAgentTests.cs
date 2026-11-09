using CameraControl.Application.Auditing;
using CameraControl.Application.Capabilities;
using CameraControl.Application.Commands;
using CameraControl.Application.Cua;
using CameraControl.Application.Diagnostics;
using CameraControl.Application.Tests.Fakes;
using Xunit;

namespace CameraControl.Application.Tests;

/// <summary>
/// AI 代理循环。
///
/// 重点验证的不是"模型聪明不聪明"，而是**安全与可控性**：
/// 工具必须走总线、物理操作必须被拒、失败必须如实回填、必须有轮数上限。
/// </summary>
public sealed class CuaAgentTests
{
    // ── 测试用能力 ──────────────────────────────────────────

    private sealed record EchoArgs(string Text);

    private sealed class EchoCommand : ICommand
    {
        public string CapabilityId => "test.echo";
    }

    private sealed class EchoHandler : ICapabilityHandler<EchoCommand>
    {
        public string? LastText { get; private set; }

        public CapabilityDescriptor Descriptor { get; } = new(
            Id: "test.echo",
            Title: "回声",
            Description: "把 text 原样返回，用于测试",
            Risk: RiskLevel.ReadOnly,
            CommandType: typeof(EchoCommand),
            ParametersJsonSchema: """{ "type":"object","properties":{"text":{"type":"string"}} }""");

        public Task<object?> HandleAsync(EchoCommand command, CommandContext context, CancellationToken cancellationToken)
        {
            LastText = context.Actor.Name;
            return Task.FromResult<object?>("echo-ok");
        }
    }

    private sealed class MoveCommand : ICommand
    {
        public string CapabilityId => "test.move";
    }

    private sealed class MoveHandler : ICapabilityHandler<MoveCommand>
    {
        public int Invocations { get; private set; }

        public CapabilityDescriptor Descriptor { get; } = new(
            Id: "test.move",
            Title: "运动",
            Description: "物理动作",
            Risk: RiskLevel.Physical,
            CommandType: typeof(MoveCommand));

        public Task<object?> HandleAsync(MoveCommand command, CommandContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult<object?>(null);
        }
    }

    /// <summary>测试用绑定器：把 test.echo 的 text 参数忽略掉（命令本身无参）。</summary>
    private sealed class TestBinder : ICommandBinder
    {
        public ICommand? Bind(string capabilityId, System.Text.Json.JsonElement arguments, out string? error)
        {
            error = null;
            return capabilityId switch
            {
                "test.echo" => new EchoCommand(),
                "test.move" => new MoveCommand(),
                CuaGetCapabilitiesCommand.Id => new CuaGetCapabilitiesCommand(),
                _ => Fail(capabilityId, out error)
            };
        }

        private static ICommand? Fail(string id, out string? error)
        {
            error = $"没有绑定器：{id}";
            return null;
        }
    }

    private static (CuaAgent Agent, ScriptedLlmProvider Llm, EchoHandler Echo, MoveHandler Move, InMemoryAuditLog Audit) Build()
    {
        var registry = new CapabilityRegistry();
        var echo = new EchoHandler();
        var move = new MoveHandler();
        registry.Register(echo).Register(move);
        registry.Register(new CuaGetCapabilitiesCapability(registry, new FakeClock()));

        var llm = new ScriptedLlmProvider();
        var audit = new InMemoryAuditLog();

        var bus = new CommandBus(
            registry,
            new DefaultRiskPolicy(),
            audit,
            new FakeClock(),
            new InMemoryMetrics(),
            new FakeAppLogger());

        var agent = new CuaAgent(
            llm,
            registry,
            new TestBinder(),
            bus,
            new InMemoryMetrics(),
            new FakeAppLogger());

        return (agent, llm, echo, move, audit);
    }

    // ── 正常路径 ────────────────────────────────────────────

    [Fact]
    public async Task 模型直接答复时应完成并返回答案()
    {
        (CuaAgent agent, ScriptedLlmProvider llm, _, _, _) = Build();
        llm.ThenAnswer("已经把相机打开了。");

        AiTaskResult result = await agent.RunAsync(
            new AiTaskRequest("打开相机"), Actor.Ai("tester"), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(AiStopReason.Completed, result.StopReason);
        Assert.Equal("已经把相机打开了。", result.Answer);
        Assert.Equal(0, result.ToolCallCount);
    }

    [Fact]
    public async Task 工具调用应经总线执行并把结果回填给模型()
    {
        (CuaAgent agent, ScriptedLlmProvider llm, _, _, InMemoryAuditLog audit) = Build();
        llm.ThenCallTool("call-1", "test_echo", """{"text":"hi"}""")
           .ThenAnswer("完成");

        AiTaskResult result = await agent.RunAsync(
            new AiTaskRequest("测试"), Actor.Ai("agent-1"), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, result.ToolCallCount);

        AiToolInvocation invocation = result.Steps[0].Invocations[0];
        Assert.Equal("test.echo", invocation.CapabilityId);
        Assert.True(invocation.Ok);

        // 工具结果作为 tool 消息回到了模型
        LlmChatRequest second = llm.Requests[1];
        Assert.Contains(second.Messages, m => m.Role == LlmRole.Tool && m.ToolCallId == "call-1");

        // 审计里记录了这次能力调用，且 actor 是 AI
        IReadOnlyList<AuditEntry> entries = audit.Recent(10);
        Assert.Contains(entries, e => e.CapabilityId == "test.echo" && e.Actor.Kind == ActorKind.Ai);
    }

    [Fact]
    public async Task 工具名应使用下划线形式暴露给模型()
    {
        (CuaAgent agent, ScriptedLlmProvider llm, _, _, _) = Build();
        llm.ThenAnswer("ok");

        await agent.RunAsync(new AiTaskRequest("看看有哪些能力"), Actor.Ai(), CancellationToken.None);

        LlmChatRequest first = llm.Requests[0];
        Assert.Contains(first.Tools, t => t.Name == "cua_getCapabilities");
        Assert.Contains(first.Tools, t => t.Name == "test_echo");

        // 不允许出现带点的名字（OpenAI 函数名规范）
        Assert.DoesNotContain(first.Tools, t => t.Name.Contains('.', StringComparison.Ordinal));
    }

    // ── 安全边界 ────────────────────────────────────────────

    [Fact]
    public async Task AI调用物理能力应被拒且处理器不执行()
    {
        (CuaAgent agent, ScriptedLlmProvider llm, _, MoveHandler move, _) = Build();
        llm.ThenCallTool("call-m", "test_move", "{}")
           .ThenAnswer("需要人工确认");

        AiTaskResult result = await agent.RunAsync(
            new AiTaskRequest("移动机器人"), Actor.Ai("agent"), CancellationToken.None);

        AiToolInvocation invocation = result.Steps[0].Invocations[0];
        Assert.False(invocation.Ok);
        Assert.Equal(CommandErrorCodes.ConfirmationRequired, invocation.ErrorCode);

        // 关键：物理动作根本没有下发
        Assert.Equal(0, move.Invocations);
    }

    // ── 容错 ────────────────────────────────────────────────

    [Fact]
    public async Task 未知工具应回填错误而不是崩溃()
    {
        (CuaAgent agent, ScriptedLlmProvider llm, _, _, _) = Build();
        llm.ThenCallTool("c", "does_not_exist", "{}")
           .ThenAnswer("用错工具了");

        AiTaskResult result = await agent.RunAsync(
            new AiTaskRequest("x"), Actor.Ai(), CancellationToken.None);

        AiToolInvocation invocation = result.Steps[0].Invocations[0];
        Assert.False(invocation.Ok);
        Assert.Equal(AiErrorCodes.UnknownTool, invocation.ErrorCode);
    }

    [Fact]
    public async Task 参数不是合法JSON时应回填错误()
    {
        (CuaAgent agent, ScriptedLlmProvider llm, _, _, _) = Build();
        llm.ThenCallTool("c", "test_echo", "{ 坏掉的 json")
           .ThenAnswer("参数错了");

        AiTaskResult result = await agent.RunAsync(
            new AiTaskRequest("x"), Actor.Ai(), CancellationToken.None);

        AiToolInvocation invocation = result.Steps[0].Invocations[0];
        Assert.False(invocation.Ok);
        Assert.Equal(AiErrorCodes.BadArguments, invocation.ErrorCode);
    }

    [Fact]
    public async Task 达到最大轮数应停止而非死循环()
    {
        (CuaAgent agent, ScriptedLlmProvider llm, _, _, _) = Build();

        // 连续 5 轮都在调工具，永不收尾
        for (int i = 0; i < 5; i++)
            llm.ThenCallTool($"c{i}", "test_echo", "{}");

        AiTaskResult result = await agent.RunAsync(
            new AiTaskRequest("永远做不完", MaxSteps: 3), Actor.Ai(), CancellationToken.None);

        Assert.Equal(AiStopReason.MaxSteps, result.StopReason);
        Assert.False(result.Completed);
        Assert.Equal(3, result.Steps.Count);
        Assert.Contains("最大轮数", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 模型调用失败应返回LlmError()
    {
        (CuaAgent agent, ScriptedLlmProvider llm, _, _, _) = Build();
        llm.ThenThrow(new LlmException(LlmErrorCodes.Unauthorized, "鉴权失败"));

        AiTaskResult result = await agent.RunAsync(
            new AiTaskRequest("x"), Actor.Ai(), CancellationToken.None);

        Assert.Equal(AiStopReason.LlmError, result.StopReason);
        Assert.Contains("鉴权失败", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 系统提示应包含能力清单()
    {
        (CuaAgent agent, ScriptedLlmProvider llm, _, _, _) = Build();
        llm.ThenAnswer("ok");

        await agent.RunAsync(new AiTaskRequest("x"), Actor.Ai(), CancellationToken.None);

        LlmMessage system = llm.Requests[0].Messages[0];
        Assert.Equal(LlmRole.System, system.Role);
        Assert.Contains("cua.getCapabilities", system.Content!, StringComparison.Ordinal);
        Assert.Contains("硬性规则", system.Content!, StringComparison.Ordinal);
    }
}
