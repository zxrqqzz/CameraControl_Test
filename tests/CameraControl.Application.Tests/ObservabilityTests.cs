using CameraControl.Application.Auditing;
using CameraControl.Application.Commands;
using CameraControl.Application.Diagnostics;
using CameraControl.Application.Tests.Fakes;
using Xunit;

namespace CameraControl.Application.Tests;

/// <summary>P1：指标、日志、启动自检。</summary>
public sealed class ObservabilityTests
{
    // ── 指标 ────────────────────────────────────────────────

    [Fact]
    public void 计数器应累加()
    {
        var metrics = new InMemoryMetrics();

        metrics.Increment("a");
        metrics.Increment("a");
        metrics.Increment("a", 3);

        Assert.Equal(5, metrics.Snapshot()["a"]);
    }

    [Fact]
    public void 直方图应给出count_sum_min_max_avg()
    {
        var metrics = new InMemoryMetrics();

        metrics.Observe("d", 10);
        metrics.Observe("d", 20);
        metrics.Observe("d", 30);

        IReadOnlyDictionary<string, double> snap = metrics.Snapshot();

        Assert.Equal(3, snap["d.count"]);
        Assert.Equal(60, snap["d.sum"]);
        Assert.Equal(10, snap["d.min"]);
        Assert.Equal(30, snap["d.max"]);
        Assert.Equal(20, snap["d.avg"]);
    }

    // ── 自检 ────────────────────────────────────────────────

    [Fact]
    public async Task 自检应汇总各项结果并判断总体状态()
    {
        var probes = new IHealthProbe[]
        {
            new FakeHealthProbe("a", () => new HealthItem("a", HealthStatus.Ok, "ok")),
            new FakeHealthProbe("b", () => new HealthItem("b", HealthStatus.Warn, "warn"))
        };

        var service = new SelfCheckService(probes, new FakeClock());
        SelfCheckReport report = await service.RunAsync(CancellationToken.None);

        Assert.Equal(2, report.Items.Count);
        Assert.False(report.OverallOk);
        Assert.Equal(1, report.WarnCount);
        Assert.Equal(0, report.FailCount);
    }

    [Fact]
    public async Task 探针抛异常应记为Fail且不影响其它项()
    {
        var probes = new IHealthProbe[]
        {
            FakeHealthProbe.Throwing("boom"),
            new FakeHealthProbe("ok", () => new HealthItem("ok", HealthStatus.Ok, "fine"))
        };

        var service = new SelfCheckService(probes, new FakeClock());
        SelfCheckReport report = await service.RunAsync(CancellationToken.None);

        Assert.Equal(2, report.Items.Count);
        Assert.Equal(1, report.FailCount);

        HealthItem failed = report.Items.Single(i => i.Name == "boom");
        Assert.Equal(HealthStatus.Fail, failed.Status);
        Assert.Contains("探针炸了", failed.Message);
    }

    [Fact]
    public async Task 全部Ok时总体应为Ok()
    {
        var probes = new IHealthProbe[]
        {
            new FakeHealthProbe("x", () => new HealthItem("x", HealthStatus.Ok, "ok")),
            new FakeHealthProbe("y", () => new HealthItem("y", HealthStatus.Ok, "ok"))
        };

        SelfCheckReport report = await new SelfCheckService(probes, new FakeClock())
            .RunAsync(CancellationToken.None);

        Assert.True(report.OverallOk);
    }

    // ── 总线与指标/日志的联动 ───────────────────────────────

    private sealed class PingCommand : ICommand
    {
        public string CapabilityId => "test.ping";
    }

    private sealed class PingHandler : ICapabilityHandler<PingCommand>
    {
        public CapabilityDescriptor Descriptor { get; } = new(
            Id: "test.ping",
            Title: "ping",
            Description: "test",
            Risk: RiskLevel.ReadOnly,
            CommandType: typeof(PingCommand));

        public Task<object?> HandleAsync(PingCommand command, CommandContext context, CancellationToken cancellationToken) =>
            Task.FromResult<object?>("pong");
    }

    [Fact]
    public async Task 命令执行应写入指标与日志()
    {
        var registry = new CapabilityRegistry().Register(new PingHandler());
        var metrics = new InMemoryMetrics();
        var logger = new FakeAppLogger();

        var bus = new CommandBus(
            registry,
            new DefaultRiskPolicy(),
            new InMemoryAuditLog(),
            new FakeClock(),
            metrics,
            logger);

        await bus.ExecuteAsync(new PingCommand(), Actor.Human("tester"));
        await bus.ExecuteAsync(new PingCommand(), Actor.Human("tester"));

        IReadOnlyDictionary<string, double> snap = metrics.Snapshot();
        Assert.Equal(2, snap["command.test.ping.total"]);
        Assert.Equal(2, snap["command.test.ping.duration_ms.count"]);
        Assert.DoesNotContain("command.test.ping.failed", snap.Keys);

        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, e => Assert.Equal(LogLevel.Information, e.Level));
    }

    private sealed class UnknownProbeCommand : ICommand
    {
        public string CapabilityId => "does.not.exist";
    }

    [Fact]
    public async Task 命令失败应增加failed计数并记Warning日志()
    {
        var registry = new CapabilityRegistry().Register(new PingHandler());
        var metrics = new InMemoryMetrics();
        var logger = new FakeAppLogger();

        var bus = new CommandBus(
            registry,
            new DefaultRiskPolicy(),
            new InMemoryAuditLog(),
            new FakeClock(),
            metrics,
            logger);

        // 未知能力 → 失败
        var result = await bus.ExecuteAsync(new UnknownProbeCommand(), Actor.Ai());

        Assert.False(result.Ok);
        Assert.Equal(1, metrics.Snapshot()["command.does.not.exist.failed"]);
        Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, logger.Entries[0].Level);
    }
}
