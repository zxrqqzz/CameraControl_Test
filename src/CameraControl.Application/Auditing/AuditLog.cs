using CameraControl.Domain.Ports;

namespace CameraControl.Application.Auditing;

/// <summary>一条审计记录。</summary>
/// <param name="At">发生时间。</param>
/// <param name="Actor">发起者。</param>
/// <param name="CapabilityId">能力 id。</param>
/// <param name="Risk">风险等级。</param>
/// <param name="Ok">是否成功。</param>
/// <param name="ErrorCode">失败错误码。</param>
/// <param name="Elapsed">耗时。</param>
/// <param name="Summary">参数摘要（不含敏感信息）。</param>
public sealed record AuditEntry(
    DateTimeOffset At,
    Commands.Actor Actor,
    string CapabilityId,
    Commands.RiskLevel Risk,
    bool Ok,
    string? ErrorCode,
    TimeSpan Elapsed,
    string? Summary = null);

/// <summary>
/// 审计日志端口。
/// **每一次命令执行都必须留痕**，包括被拒绝的那些（拒绝本身是重要安全事件）。
/// </summary>
public interface IAuditLog
{
    void Write(AuditEntry entry);

    /// <summary>读取最近 N 条（新→旧）。</summary>
    IReadOnlyList<AuditEntry> Recent(int count);
}

/// <summary>
/// 内存审计日志。用于测试、诊断工具与 P0 阶段（尚未接持久化）。
/// 容量有上限，避免长时间运行内存增长。
/// </summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly Queue<AuditEntry> _entries = new();
    private readonly object _sync = new();
    private readonly int _capacity;

    public InMemoryAuditLog(int capacity = 2000)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Write(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_sync)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
                _entries.Dequeue();
        }
    }

    public IReadOnlyList<AuditEntry> Recent(int count)
    {
        if (count <= 0) return [];

        lock (_sync)
        {
            return [.. _entries.Reverse().Take(count)];
        }
    }
}
