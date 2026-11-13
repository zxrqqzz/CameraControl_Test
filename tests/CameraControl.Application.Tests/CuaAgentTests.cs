using CameraControl.Application.Auditing;
using CameraControl.Application.Capabilities;
using CameraControl.Application.Commands;
using CameraControl.Application.Cua.Context;
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

    // ── 上下文接入 ──────────────────────────────────────────

    /// <summary>记录式上下文存储：只关心"有没有注入、有没有记录"。</summary>
    private sealed class RecordingContextStore : IContextStore
    {
        public string Prefix { get; set; } = string.Empty;

        public bool ThrowOnPrefix { get; set; }

        public List<(string User, string Assistant)> Turns { get; } = [];

        public bool IsEnabled => true;

        public ContextTier GetTier(string profileId) => ContextTier.InjectFull;

        public void SetTier(string profileId, ContextTier tier)
        {
        }

        public string BuildPrefix(string profileId) =>
            ThrowOnPrefix ? throw new InvalidOperationException("读不出来") : Prefix;

        public void AppendTurn(string profileId, string userText, string assistantText) =>
            Turns.Add((userText, assistantText));

        public string StartNewContext(string profileId) => "ctx-000001";

        public string ClearCurrent(string profileId) => "ctx-000002";

        public IReadOnlyList<ContextSnapshot> List(string profileId) => [];

        public bool Delete(string profileId, string contextId) => false;

        public string? GetActiveContextId(string profileId) => null;
    }

    private static (CuaAgent Agent, ScriptedLlmProvider Llm) BuildWithContext(IContextStore store)
    {
        var registry = new CapabilityRegistry();
        registry.Register(new EchoHandler());
        registry.Register(new CuaGetCapabilitiesCapability(registry, new FakeClock()));

        var llm = new ScriptedLlmProvider();

        var bus = new CommandBus(
            registry,
            new DefaultRiskPolicy(),
            new InMemoryAuditLog(),
            new FakeClock(),
            new InMemoryMetrics(),
            new FakeAppLogger());

        var agent = new CuaAgent(
            llm,
            registry,
            new TestBinder(),
            bus,
            new InMemoryMetrics(),
            new FakeAppLogger(),
            store);

        return (agent, llm);
    }

    [Fact]
    public async Task 上下文前缀应注入到系统消息()
    {
        var store = new RecordingContextStore { Prefix = "用户：上一轮问题\n助手：上一轮回答\n" };
        (CuaAgent agent, ScriptedLlmProvider llm) = BuildWithContext(store);
        llm.ThenAnswer("好的");

        await agent.RunAsync(
            new AiTaskRequest("新问题", ContextProfileId: "p1"),
            Actor.Ai("tester"),
            CancellationToken.None);

        string system = llm.Requests[0].Messages[0].Content!;

        Assert.Contains("上一轮问题", system, StringComparison.Ordinal);
        Assert.Contains("上一轮回答", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 完成一轮后应把问答写入上下文()
    {
        var store = new RecordingContextStore();
        (CuaAgent agent, ScriptedLlmProvider llm) = BuildWithContext(store);
        llm.ThenAnswer("答复内容");

        await agent.RunAsync(
            new AiTaskRequest("问题内容", ContextProfileId: "p1"),
            Actor.Ai("tester"),
            CancellationToken.None);

        Assert.Single(store.Turns);
        Assert.Equal("问题内容", store.Turns[0].User);
        Assert.Equal("答复内容", store.Turns[0].Assistant);
    }

    [Fact]
    public async Task 未指定上下文配置时不读写上下文()
    {
        var store = new RecordingContextStore { Prefix = "不该出现的内容" };
        (CuaAgent agent, ScriptedLlmProvider llm) = BuildWithContext(store);
        llm.ThenAnswer("好");

        await agent.RunAsync(new AiTaskRequest("问题"), Actor.Ai("tester"), CancellationToken.None);

        Assert.DoesNotContain("不该出现的内容", llm.Requests[0].Messages[0].Content!, StringComparison.Ordinal);
        Assert.Empty(store.Turns);
    }

    [Fact]
    public async Task 上下文读取失败不应影响任务完成()
    {
        var store = new RecordingContextStore { ThrowOnPrefix = true };
        (CuaAgent agent, ScriptedLlmProvider llm) = BuildWithContext(store);
        llm.ThenAnswer("仍然完成");

        AiTaskResult result = await agent.RunAsync(
            new AiTaskRequest("问题", ContextProfileId: "p1"),
            Actor.Ai("tester"),
            CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal("仍然完成", result.Answer);
    }

    // ── 流式执行 ────────────────────────────────────────────

    /// <summary>假流式提供方：验证代理确实走了流式通道。</summary>
    private sealed class FakeStreamingProvider : ILlmProvider, IStreamingLlmProvider
    {
        public string Name => "流式假模型";

        public bool UsedStream { get; private set; }

        public Task<LlmChatResponse> ChatAsync(LlmChatRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("应当走流式，而不是非流式。");

        public async IAsyncEnumerable<LlmStreamEvent> ChatStreamAsync(
            LlmChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            UsedStream = true;

            yield return new LlmStreamEvent(LlmStreamKind.Reasoning, "先想一下");
            yield return new LlmStreamEvent(LlmStreamKind.Reasoning, "，再看设备");

            await Task.Yield();

            yield return new LlmStreamEvent(LlmStreamKind.Content, "共 2 台");
            yield return new LlmStreamEvent(LlmStreamKind.Content, "相机");

            yield return new LlmStreamEvent(
                LlmStreamKind.Completed,
                Response: new LlmChatResponse(
                    LlmMessage.Assistant("共 2 台相机", null, "先想一下，再看设备"),
                    new LlmUsage(10, 5),
                    "stop",
                    "req-1"));
        }
    }

    private sealed class CollectingProgress : IProgress<AiProgressEvent>
    {
        public List<AiProgressEvent> Events { get; } = [];

        public void Report(AiProgressEvent value) => Events.Add(value);
    }

    private static CuaAgent BuildWithProvider(ILlmProvider provider)
    {
        var registry = new CapabilityRegistry();
        registry.Register(new CuaGetCapabilitiesCapability(registry, new FakeClock()));

        var bus = new CommandBus(
            registry,
            new DefaultRiskPolicy(),
            new InMemoryAuditLog(),
            new FakeClock(),
            new InMemoryMetrics(),
            new FakeAppLogger());

        return new CuaAgent(
            provider,
            registry,
            new TestBinder(),
            bus,
            new InMemoryMetrics(),
            new FakeAppLogger());
    }

    [Fact]
    public async Task 应当走流式并上报思考与答复增量()
    {
        var provider = new FakeStreamingProvider();
        CuaAgent agent = BuildWithProvider(provider);
        var progress = new CollectingProgress();

        AiTaskResult result = await agent.RunAsync(
            new AiTaskRequest("列出相机"),
            Actor.Ai("tester"),
            CancellationToken.None,
            progress);

        Assert.True(provider.UsedStream);
        Assert.Contains(progress.Events, e => e.Kind == AiProgressKind.ReasoningDelta);
        Assert.Contains(progress.Events, e => e.Kind == AiProgressKind.ContentDelta);
        Assert.Contains(progress.Events, e => e.Kind == AiProgressKind.TurnStarted);

        string reasoning = string.Concat(
            progress.Events.Where(e => e.Kind == AiProgressKind.ReasoningDelta).Select(e => e.Text));

        Assert.Equal("先想一下，再看设备", reasoning);

        Assert.True(result.Completed);
        Assert.Equal("共 2 台相机", result.Answer);
    }

    [Fact]
    public async Task 非流式提供方应自动降级且不报错()
    {
        var scripted = new ScriptedLlmProvider().ThenAnswer("非流式答复");
        CuaAgent agent = BuildWithProvider(scripted);
        var progress = new CollectingProgress();

        AiTaskResult result = await agent.RunAsync(
            new AiTaskRequest("随便"),
            Actor.Ai("tester"),
            CancellationToken.None,
            progress);

        Assert.True(result.Completed);
        Assert.Equal("非流式答复", result.Answer);
        Assert.DoesNotContain(progress.Events, e => e.Kind == AiProgressKind.ContentDelta);
    }
}
