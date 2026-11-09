using System.Text.Json;
using CameraControl.Application.Auditing;
using CameraControl.Application.Capabilities;
using CameraControl.Application.Commands;
using CameraControl.Application.Cua;
using CameraControl.Application.Tests.Fakes;
using Xunit;

namespace CameraControl.Application.Tests;

/// <summary>
/// CUA 能力清单导出。这是"AI 能看到什么、能做什么"的白名单真相源，
/// 必须做到：与实现同源、可复现、schema 坏了要暴露而不是静默丢弃。
/// </summary>
public sealed class CapabilityCatalogTests
{
    private sealed class AlphaCommand : ICommand
    {
        public string CapabilityId => "zeta.alpha";
    }

    private sealed class AlphaHandler : ICapabilityHandler<AlphaCommand>
    {
        public CapabilityDescriptor Descriptor { get; } = new(
            Id: "zeta.alpha",
            Title: "阿尔法",
            Description: "第一个测试能力",
            Risk: RiskLevel.ReadOnly,
            CommandType: typeof(AlphaCommand),
            ParametersJsonSchema: """{ "type": "object", "properties": { "n": { "type": "integer" } } }""");

        public Task<object?> HandleAsync(AlphaCommand command, CommandContext context, CancellationToken cancellationToken) =>
            Task.FromResult<object?>(null);
    }

    private sealed class BetaCommand : ICommand
    {
        public string CapabilityId => "alpha.beta";
    }

    private sealed class BetaHandler : ICapabilityHandler<BetaCommand>
    {
        public CapabilityDescriptor Descriptor { get; } = new(
            Id: "alpha.beta",
            Title: "贝塔",
            Description: "需要确认的物理能力",
            Risk: RiskLevel.Physical,
            CommandType: typeof(BetaCommand),
            ParametersJsonSchema: "{ 这不是合法 JSON }");

        public Task<object?> HandleAsync(BetaCommand command, CommandContext context, CancellationToken cancellationToken) =>
            Task.FromResult<object?>(null);
    }

    private static CapabilityRegistry BuildRegistry() =>
        new CapabilityRegistry()
            .Register(new AlphaHandler())
            .Register(new BetaHandler());

    [Fact]
    public void 导出应按id升序且数量一致()
    {
        CapabilityCatalogExport export = CapabilityCatalogExporter.Export(
            BuildRegistry().All,
            new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, export.Count);
        Assert.Equal("alpha.beta", export.Capabilities[0].Id);
        Assert.Equal("zeta.alpha", export.Capabilities[1].Id);
    }

    [Fact]
    public void 合法schema应被解析为对象()
    {
        CapabilityCatalogExport export = CapabilityCatalogExporter.Export(
            BuildRegistry().All,
            DateTimeOffset.UtcNow);

        ExportedCapability alpha = export.Capabilities.Single(c => c.Id == "zeta.alpha");

        Assert.Null(alpha.SchemaError);
        Assert.NotNull(alpha.Parameters);
        Assert.Equal(JsonValueKind.Object, alpha.Parameters!.Value.ValueKind);
        Assert.True(alpha.Parameters.Value.TryGetProperty("properties", out _));
    }

    [Fact]
    public void 非法schema不应静默丢弃而应暴露原因()
    {
        CapabilityCatalogExport export = CapabilityCatalogExporter.Export(
            BuildRegistry().All,
            DateTimeOffset.UtcNow);

        ExportedCapability beta = export.Capabilities.Single(c => c.Id == "alpha.beta");

        Assert.Null(beta.Parameters);
        Assert.False(string.IsNullOrWhiteSpace(beta.SchemaError));
    }

    [Fact]
    public void 风险等级与确认标记应正确导出()
    {
        CapabilityCatalogExport export = CapabilityCatalogExporter.Export(
            BuildRegistry().All,
            DateTimeOffset.UtcNow);

        ExportedCapability alpha = export.Capabilities.Single(c => c.Id == "zeta.alpha");
        ExportedCapability beta = export.Capabilities.Single(c => c.Id == "alpha.beta");

        Assert.Equal("ReadOnly", alpha.Risk);
        Assert.False(alpha.RequiresConfirmation);

        Assert.Equal("Physical", beta.Risk);
        Assert.True(beta.RequiresConfirmation);
    }

    [Fact]
    public void Markdown应包含所有id与确认标记()
    {
        CapabilityCatalogExport export = CapabilityCatalogExporter.Export(
            BuildRegistry().All,
            DateTimeOffset.UtcNow);

        string markdown = CapabilityCatalogExporter.ToMarkdown(export);

        Assert.Contains("zeta.alpha", markdown, StringComparison.Ordinal);
        Assert.Contains("alpha.beta", markdown, StringComparison.Ordinal);
        Assert.Contains("no-confirmation", markdown, StringComparison.Ordinal);
        Assert.Contains("requires-confirmation", markdown, StringComparison.Ordinal);
        Assert.Contains("Capabilities (2)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 通过总线调用应导出全部已注册能力()
    {
        CapabilityRegistry registry = BuildRegistry();
        registry.Register(new CuaGetCapabilitiesCapability(registry, new FakeClock()));

        var bus = new CommandBus(
            registry,
            new DefaultRiskPolicy(),
            new InMemoryAuditLog(),
            new FakeClock(),
            new CameraControl.Application.Diagnostics.InMemoryMetrics(),
            new FakeAppLogger());

        CommandResult result = await bus.ExecuteAsync(
            new CuaGetCapabilitiesCommand(CapabilityCatalogFormat.Json),
            Actor.Ai("test-agent", "s1"));

        Assert.True(result.Ok);

        var export = Assert.IsType<CapabilityCatalogExport>(result.Data);
        Assert.Equal(3, export.Count);
        Assert.Contains(export.Capabilities, c => c.Id == CuaGetCapabilitiesCommand.Id);

        // 导出行为本身也应被审计
        Assert.Contains("cua.getCapabilities", export.Capabilities.Select(c => c.Id));
    }

    [Fact]
    public async Task Markdown格式应返回字符串()
    {
        CapabilityRegistry registry = BuildRegistry();
        registry.Register(new CuaGetCapabilitiesCapability(registry, new FakeClock()));

        var bus = new CommandBus(
            registry,
            new DefaultRiskPolicy(),
            new InMemoryAuditLog(),
            new FakeClock(),
            new CameraControl.Application.Diagnostics.InMemoryMetrics(),
            new FakeAppLogger());

        CommandResult result = await bus.ExecuteAsync(
            new CuaGetCapabilitiesCommand(CapabilityCatalogFormat.Markdown),
            Actor.Ai());

        Assert.True(result.Ok);
        var markdown = Assert.IsType<string>(result.Data);
        Assert.Contains("# Capabilities", markdown, StringComparison.Ordinal);
    }
}
