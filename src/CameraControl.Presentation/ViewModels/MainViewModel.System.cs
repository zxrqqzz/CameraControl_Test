using System.Collections.ObjectModel;
using CameraControl.Application.Auditing;
using CameraControl.Application.Commands;
using CameraControl.Application.Diagnostics;
using CommunityToolkit.Mvvm.Input;

namespace CameraControl.Presentation.ViewModels;

/// <summary>系统块：自检清单、运行指标、审计流水。</summary>
public sealed partial class MainViewModel
{

    // ── 系统 ────────────────────────────────────────────────

    public ObservableCollection<string> SelfCheckItems { get; } = [];

    public ObservableCollection<string> MetricItems { get; } = [];

    public ObservableCollection<string> AuditItems { get; } = [];

    [RelayCommand]
    private async Task RunSelfCheckAsync()
    {
        await RunGuardedAsync("启动自检", async () =>
        {
            SelfCheckReport report = await _selfCheck.RunAsync(CancellationToken.None);

            SelfCheckItems.Clear();
            foreach (HealthItem item in report.Items)
            {
                string mark = item.Status switch
                {
                    HealthStatus.Ok => "[正常]",
                    HealthStatus.Warn => "[注意]",
                    _ => "[失败]"
                };

                SelfCheckItems.Add($"{mark} {item.Name}: {item.Message}");
            }

            Status = report.OverallOk
                ? "自检通过。"
                : $"自检发现 {report.FailCount} 项失败、{report.WarnCount} 项注意。";
        });
    }

    [RelayCommand]
    private void RefreshMetrics()
    {
        MetricItems.Clear();

        foreach (KeyValuePair<string, double> pair in _metrics.Snapshot())
            MetricItems.Add($"{pair.Key} = {pair.Value:0.###}");

        if (MetricItems.Count == 0) MetricItems.Add("(还没有任何指标，先执行几个操作)");
    }

    [RelayCommand]
    private void RefreshAudit()
    {
        AuditItems.Clear();

        foreach (AuditEntry entry in _audit.Recent(100))
        {
            string mark = entry.Ok ? "OK  " : "FAIL";
            AuditItems.Add(
                $"{entry.At.ToLocalTime():HH:mm:ss} {mark} [{entry.Actor.Kind}] " +
                $"{entry.CapabilityId} ({entry.Risk}, {entry.Elapsed.TotalMilliseconds:F0}ms)" +
                (entry.ErrorCode is null ? "" : $"  {entry.ErrorCode}"));
        }

        if (AuditItems.Count == 0) AuditItems.Add("(暂无审计记录)");
    }
}
