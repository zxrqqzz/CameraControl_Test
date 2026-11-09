using CameraControl.Application.Diagnostics;
using Serilog;
using Serilog.Events;

namespace CameraControl.Infrastructure.Logging;

/// <summary>
/// Serilog 适配器。
///
/// Application 只认识 <see cref="IAppLogger"/>，不依赖 Serilog；
/// 具体日志框架与落盘位置完全由组合根决定。
/// </summary>
public sealed class SerilogAppLogger : IAppLogger
{
    private readonly ILogger _logger;

    public SerilogAppLogger(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public bool IsEnabled(LogLevel level) => _logger.IsEnabled(ToSerilog(level));

    public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object?>? fields = null)
    {
        LogEventLevel serilogLevel = ToSerilog(level);
        if (!_logger.IsEnabled(serilogLevel)) return;

        if (fields is null || fields.Count == 0)
        {
            _logger.Write(serilogLevel, "{Message}", message);
            return;
        }

        // 结构化字段：用属性名前缀 _ 避免与 Serilog 内置属性冲突
        var properties = fields
            .Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value))
            .ToArray();

        _logger.Write(serilogLevel, "{Message} {@Fields}", message, properties);
    }

    private static LogEventLevel ToSerilog(LogLevel level) => level switch
    {
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Information
    };
}

/// <summary>
/// 一个极简的"无操作"日志器，用于测试与不想初始化 Serilog 的宿主。
/// </summary>
public sealed class NullAppLogger : IAppLogger
{
    public static NullAppLogger Instance { get; } = new();

    public bool IsEnabled(LogLevel level) => false;

    public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object?>? fields = null)
    {
        // 故意什么也不做
    }
}
