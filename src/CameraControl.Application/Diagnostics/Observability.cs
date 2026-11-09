namespace CameraControl.Application.Diagnostics;

/// <summary>日志级别。</summary>
public enum LogLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3
}

/// <summary>
/// 应用日志端口。
///
/// 只定义"记一条结构化日志"，不绑定任何具体日志框架——
/// 实现放在 Infrastructure（Serilog），Application 层保持干净。
/// </summary>
public interface IAppLogger
{
    bool IsEnabled(LogLevel level);

    void Log(LogLevel level, string message, IReadOnlyDictionary<string, object?>? fields = null);
}

/// <summary>便捷扩展。</summary>
public static class AppLoggerExtensions
{
    public static void Debug(this IAppLogger logger, string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        logger.Log(LogLevel.Debug, message, fields);

    public static void Info(this IAppLogger logger, string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        logger.Log(LogLevel.Information, message, fields);

    public static void Warn(this IAppLogger logger, string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        logger.Log(LogLevel.Warning, message, fields);

    public static void Error(this IAppLogger logger, string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        logger.Log(LogLevel.Error, message, fields);
}

/// <summary>
/// 指标端口。
///
/// P1 只需要计数器与耗时观测；不引入 Prometheus/OTel 之类的重依赖。
/// </summary>
public interface IMetrics
{
    /// <summary>计数 +value（默认 1）。</summary>
    void Increment(string counter, double value = 1);

    /// <summary>记录一次观测值（如耗时毫秒）。</summary>
    void Observe(string histogram, double value);

    /// <summary>当前快照（计数器值 + 观测的 count/sum/min/max）。</summary>
    IReadOnlyDictionary<string, double> Snapshot();
}

/// <summary>
/// 内存指标实现。进程内可见，供 system.getMetrics 与诊断导出使用。
///
/// 注意：直方图只保留聚合值，不保留原始样本，避免长时间运行内存增长。
/// </summary>
public sealed class InMemoryMetrics : IMetrics
{
    private readonly Dictionary<string, double> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Aggregate> _histograms = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public void Increment(string counter, double value = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(counter);

        lock (_sync)
        {
            _counters.TryGetValue(counter, out double current);
            _counters[counter] = current + value;
        }
    }

    public void Observe(string histogram, double value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(histogram);

        lock (_sync)
        {
            if (!_histograms.TryGetValue(histogram, out Aggregate aggregate))
            {
                aggregate = new Aggregate(value);
            }
            else
            {
                aggregate.Add(value);
            }

            _histograms[histogram] = aggregate;
        }
    }

    public IReadOnlyDictionary<string, double> Snapshot()
    {
        var result = new SortedDictionary<string, double>(StringComparer.Ordinal);

        lock (_sync)
        {
            foreach ((string key, double value) in _counters)
                result[key] = value;

            foreach ((string key, Aggregate aggregate) in _histograms)
            {
                result[$"{key}.count"] = aggregate.Count;
                result[$"{key}.sum"] = aggregate.Sum;
                result[$"{key}.min"] = aggregate.Min;
                result[$"{key}.max"] = aggregate.Max;

                if (aggregate.Count > 0)
                    result[$"{key}.avg"] = aggregate.Sum / aggregate.Count;
            }
        }

        return result;
    }

    private struct Aggregate
    {
        public Aggregate(double first)
        {
            Count = 1;
            Sum = first;
            Min = first;
            Max = first;
        }

        public double Count { get; private set; }

        public double Sum { get; private set; }

        public double Min { get; private set; }

        public double Max { get; private set; }

        public void Add(double value)
        {
            Count++;
            Sum += value;
            if (value < Min) Min = value;
            if (value > Max) Max = value;
        }
    }
}
