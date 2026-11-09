using CameraControl.Domain.Ports;

namespace CameraControl.Application.Diagnostics;

/// <summary>健康状态。</summary>
public enum HealthStatus
{
    /// <summary>正常。</summary>
    Ok = 0,

    /// <summary>可用但有隐患（如未安装 MVS、磁盘空间偏低）。</summary>
    Warn = 1,

    /// <summary>会直接导致功能不可用（如以管理员身份运行 → 摄像头被拒）。</summary>
    Fail = 2
}

/// <summary>单项自检结果。</summary>
/// <param name="Name">检查项标识，如 process.elevation。</param>
/// <param name="Status">状态。</param>
/// <param name="Message">人可读说明。</param>
/// <param name="Details">附加信息（键值均为字符串，便于导出）。</param>
public sealed record HealthItem(
    string Name,
    HealthStatus Status,
    string Message,
    IReadOnlyDictionary<string, string>? Details = null);

/// <summary>自检报告。</summary>
/// <param name="At">执行时间。</param>
/// <param name="OverallOk">是否全部为 Ok。</param>
/// <param name="Items">各项结果。</param>
public sealed record SelfCheckReport(
    DateTimeOffset At,
    bool OverallOk,
    IReadOnlyList<HealthItem> Items)
{
    public int FailCount => Items.Count(i => i.Status == HealthStatus.Fail);

    public int WarnCount => Items.Count(i => i.Status == HealthStatus.Warn);
}

/// <summary>单项健康检查探针。新增检查项只需新增一个实现并注册。</summary>
public interface IHealthProbe
{
    string Name { get; }

    Task<HealthItem> RunAsync(CancellationToken cancellationToken);
}

/// <summary>启动自检服务。</summary>
public interface ISelfCheck
{
    Task<SelfCheckReport> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 自检聚合器：跑完所有探针并汇总。
///
/// 单个探针抛异常不会中断整体自检——该项记为 Fail，其余照常执行。
/// </summary>
public sealed class SelfCheckService : ISelfCheck
{
    private readonly IReadOnlyList<IHealthProbe> _probes;
    private readonly IClock _clock;

    public SelfCheckService(IEnumerable<IHealthProbe> probes, Domain.Ports.IClock clock)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(clock);

        _probes = [.. probes];
        _clock = clock;
    }

    public async Task<SelfCheckReport> RunAsync(CancellationToken cancellationToken)
    {
        var items = new List<HealthItem>(_probes.Count);

        foreach (IHealthProbe probe in _probes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                HealthItem item = await probe.RunAsync(cancellationToken).ConfigureAwait(false);
                items.Add(item);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                items.Add(new HealthItem(
                    probe.Name,
                    HealthStatus.Fail,
                    $"检查项执行异常：{ex.Message}"));
            }
        }

        bool overallOk = items.All(i => i.Status == HealthStatus.Ok);
        return new SelfCheckReport(_clock.UtcNow, overallOk, items);
    }
}
