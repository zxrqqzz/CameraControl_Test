using CameraControl.Application.Commands;
using CameraControl.Application.Diagnostics;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Capabilities;

/// <summary>system.selfCheck 命令：执行启动自检。</summary>
public sealed record SystemSelfCheckCommand : ICommand
{
    public const string Id = "system.selfCheck";

    public string CapabilityId => Id;
}

/// <summary>system.getMetrics 命令：读取进程内指标快照。</summary>
public sealed record SystemGetMetricsCommand : ICommand
{
    public const string Id = "system.getMetrics";

    public string CapabilityId => Id;
}

/// <summary>system.getAuditLog 命令：读取最近审计记录。</summary>
/// <param name="Count">条数（1–500）。</param>
public sealed record SystemGetAuditLogCommand(int Count = 50) : ICommand
{
    public const string Id = "system.getAuditLog";

    public string CapabilityId => Id;
}

/// <summary>自检结果投影。</summary>
public sealed record SelfCheckDto(
    DateTimeOffset At,
    bool OverallOk,
    int FailCount,
    int WarnCount,
    IReadOnlyList<HealthItemDto> Items);

/// <summary>单项检查投影。</summary>
public sealed record HealthItemDto(
    string Name,
    string Status,
    string Message,
    IReadOnlyDictionary<string, string>? Details);

/// <summary>审计记录投影。</summary>
public sealed record AuditEntryDto(
    DateTimeOffset At,
    string ActorKind,
    string? ActorName,
    string CapabilityId,
    string Risk,
    bool Ok,
    string? ErrorCode,
    double ElapsedMs);

/// <summary>系统自检能力（只读）。</summary>
public sealed class SystemSelfCheckCapability : ICapabilityHandler<SystemSelfCheckCommand>
{
    private readonly ISelfCheck _selfCheck;

    public SystemSelfCheckCapability(ISelfCheck selfCheck)
    {
        ArgumentNullException.ThrowIfNull(selfCheck);
        _selfCheck = selfCheck;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: SystemSelfCheckCommand.Id,
        Title: "启动自检",
        Description: "检查运行环境：进程权限（管理员会导致摄像头不可用）、操作系统、SDK 可用性、" +
                     "存储可写性、磁盘空间与相机数量。返回每项状态与说明。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(SystemSelfCheckCommand),
        ParametersJsonSchema: """{ "type": "object", "properties": {} }""");

    public async Task<object?> HandleAsync(
        SystemSelfCheckCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        SelfCheckReport report = await _selfCheck.RunAsync(cancellationToken).ConfigureAwait(false);

        return new SelfCheckDto(
            report.At,
            report.OverallOk,
            report.FailCount,
            report.WarnCount,
            [.. report.Items.Select(i => new HealthItemDto(
                i.Name,
                i.Status.ToString(),
                i.Message,
                i.Details))]);
    }
}

/// <summary>指标读取能力（只读）。</summary>
public sealed class SystemGetMetricsCapability : ICapabilityHandler<SystemGetMetricsCommand>
{
    private readonly IMetrics _metrics;

    public SystemGetMetricsCapability(IMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: SystemGetMetricsCommand.Id,
        Title: "读取指标",
        Description: "返回进程内指标快照（命令次数、失败数、耗时统计）。用于诊断与性能观察。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(SystemGetMetricsCommand),
        ParametersJsonSchema: """{ "type": "object", "properties": {} }""");

    public Task<object?> HandleAsync(
        SystemGetMetricsCommand command,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>(_metrics.Snapshot());
}

/// <summary>审计日志读取能力（只读）。</summary>
public sealed class SystemGetAuditLogCapability : ICapabilityHandler<SystemGetAuditLogCommand>
{
    private readonly Auditing.IAuditLog _auditLog;

    public SystemGetAuditLogCapability(Auditing.IAuditLog auditLog)
    {
        ArgumentNullException.ThrowIfNull(auditLog);
        _auditLog = auditLog;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: SystemGetAuditLogCommand.Id,
        Title: "读取审计日志",
        Description: "返回最近的命令执行审计记录（谁、何时、什么能力、是否成功、耗时）。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(SystemGetAuditLogCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": { "count": { "type": "integer", "minimum": 1, "maximum": 500, "default": 50 } }
        }
        """);

    public Task<object?> HandleAsync(
        SystemGetAuditLogCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        int count = Math.Clamp(command.Count, 1, 500);

        IReadOnlyList<Auditing.AuditEntry> entries = _auditLog.Recent(count);

        IReadOnlyList<AuditEntryDto> dto = [.. entries.Select(e => new AuditEntryDto(
            e.At,
            e.Actor.Kind.ToString(),
            e.Actor.Name,
            e.CapabilityId,
            e.Risk.ToString(),
            e.Ok,
            e.ErrorCode,
            Math.Round(e.Elapsed.TotalMilliseconds, 2)))];

        return Task.FromResult<object?>(dto);
    }
}
