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
/// 开启后，绑定问题会以 Warn 出现在 app-*.log 里，带控件与属性名。
/// </summary>
internal sealed class WpfBindingTraceListener : TraceListener
{
    private readonly IAppLogger _logger;

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
            _logger.Warn("WPF 绑定问题", new Dictionary<string, object?>
            {
                ["detail"] = message.Length > 800 ? message[..800] : message
            });
        }
        catch
        {
            // 记日志失败不能影响 UI
        }
    }
}
