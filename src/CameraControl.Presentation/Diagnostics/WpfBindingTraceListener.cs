using System.Diagnostics;
using CameraControl.Application.Diagnostics;

namespace CameraControl.Presentation.Diagnostics;

/// <summary>
/// 把 WPF 的绑定错误转发到应用日志。
///
/// 为什么需要：WPF 的绑定失败（例如"按钮绑了个不存在的命令"）
/// **不会抛异常、不会进我们的日志**，界面表现就是"点了没反应"。
/// 曾经 `RunAiAsync` 因误删 `[RelayCommand]` 导致运行按钮完全无效，
/// 却查不到任何痕迹——就是被这类静默失败坑的。
///
/// 去重：绑定错误会**按数据项/按布局轮次反复触发**（一个坏模板能刷几万条）。
/// 这里每条只完整记一次，重复的直接丢掉；不同条记满上限后只提示一次。
/// </summary>
internal sealed class WpfBindingTraceListener : TraceListener
{
    /// <summary>最多跟踪多少条不同的绑定问题（防内存被异常内容撑爆）。</summary>
    private const int MaxDistinctMessages = 200;

    private readonly IAppLogger _logger;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private bool _capNotified;

    public WpfBindingTraceListener(IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public override void Write(string? message)
    {
        // WPF 会先 Write 前缀再 WriteLine 正文，这里只在正文落日志，避免重复
    }

    public override void WriteLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        try
        {
            string detail = message.Length > 800 ? message[..800] : message;

            lock (_sync)
            {
                if (_seen.Contains(detail))
                    return; // 同一条已经记过

                if (_seen.Count >= MaxDistinctMessages)
                {
                    // 记够多了：不再新增跟踪项（防内存无限增长），只提示一次
                    if (_capNotified) return;

                    _capNotified = true;
                    _logger.Warn($"WPF 绑定问题已记录 {MaxDistinctMessages} 条，后续不同条不再写入日志");
                    return;
                }

                _seen.Add(detail);
            }

            _logger.Warn("WPF 绑定问题", new Dictionary<string, object?>
            {
                ["detail"] = detail
            });
        }
        catch
        {
            // 记日志失败不能影响 UI
        }
    }
}
