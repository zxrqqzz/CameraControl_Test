using CameraControl.Domain.Cameras;
using CameraControl.Domain.Capture;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Capture;

/// <summary>
/// 帧编码抽象：把原始像素转成可显示/可传输的图片字节。
///
/// 放在 Application 层是为了不污染领域层（领域只认像素），
/// 具体实现（PNG）在 Infrastructure。
/// </summary>
public interface IFrameEncoder
{
    /// <summary>编码为 PNG；不支持的像素格式应抛 <see cref="NotSupportedException"/>。</summary>
    byte[] EncodePng(FrameImage image);
}

/// <summary>某台相机的采集统计（内存态，进程退出即消失）。</summary>
public sealed record CaptureStatistics(
    string CameraId,
    long Frames,
    long Failures,
    DateTimeOffset? LastFrameUtc,
    int LastWidth,
    int LastHeight,
    string? LastPixelFormat,
    int? LastExposureUs,
    string? LastError);

/// <summary>采集统计接口。</summary>
public interface ICaptureStatistics
{
    /// <summary>记录一次成功采集。</summary>
    void RecordSuccess(CameraId id, Frame frame);

    /// <summary>记录一次失败。</summary>
    void RecordFailure(CameraId id, string error);

    /// <summary>读取统计；从未采集过时返回零值快照。</summary>
    CaptureStatistics Get(CameraId id);
}

/// <summary>
/// 内存态采集统计。
/// 只累加计数与最后一帧元数据，**不保留像素**（隐私：帧数据永不驻留）。
/// </summary>
public sealed class CaptureStatisticsService : ICaptureStatistics
{
    private sealed class Counter
    {
        public long Frames;
        public long Failures;
        public DateTimeOffset? LastFrameUtc;
        public int LastWidth;
        public int LastHeight;
        public string? LastPixelFormat;
        // TODO: 曝光值来自相机参数查询，能力层暂未回填（保持 null 表示未知）
        public int? LastExposureUs = null;
        public string? LastError;
    }

    private readonly Dictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public void RecordSuccess(CameraId id, Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_sync)
        {
            Counter counter = GetOrCreate(id);
            counter.Frames++;
            counter.LastFrameUtc = frame.TimestampUtc;
            counter.LastWidth = frame.Image.Width;
            counter.LastHeight = frame.Image.Height;
            counter.LastPixelFormat = frame.Image.PixelFormat;
            counter.LastError = null;
        }
    }

    public void RecordFailure(CameraId id, string error)
    {
        lock (_sync)
        {
            Counter counter = GetOrCreate(id);
            counter.Failures++;
            counter.LastError = error;
        }
    }

    public CaptureStatistics Get(CameraId id)
    {
        lock (_sync)
        {
            Counter counter = GetOrCreate(id);

            return new CaptureStatistics(
                id.Value ?? string.Empty,
                counter.Frames,
                counter.Failures,
                counter.LastFrameUtc,
                counter.LastWidth,
                counter.LastHeight,
                counter.LastPixelFormat,
                counter.LastExposureUs,
                counter.LastError);
        }
    }

    private Counter GetOrCreate(CameraId id)
    {
        string key = id.Value ?? string.Empty;

        if (!_counters.TryGetValue(key, out Counter? counter))
        {
            counter = new Counter();
            _counters[key] = counter;
        }

        return counter;
    }
}

/// <summary>
/// 连续预览服务：后台按固定节拍取帧，只保留**最新一帧**（内存）。
///
/// 隐私：帧只在内存里，停止或关闭相机即丢弃；不落盘。
/// </summary>
public interface ICapturePreviewService
{
    /// <summary>开始预览；已在预览则按新参数重启。</summary>
    Task StartAsync(CameraId id, int fps, CancellationToken cancellationToken);

    /// <summary>停止预览。</summary>
    Task StopAsync(CameraId id);

    /// <summary>取最新一帧；没有则返回 null。</summary>
    Frame? Latest(CameraId id);

    /// <summary>正在预览的相机。</summary>
    IReadOnlyList<string> Active { get; }
}

/// <summary>连续预览实现。</summary>
public sealed class CapturePreviewService : ICapturePreviewService, IAsyncDisposable, IDisposable
{
    private sealed class Loop
    {
        public readonly CancellationTokenSource Cancellation = new();
        public Task? Worker;
        public Frame? Frame;
        public readonly object Sync = new();
    }

    private readonly ICameraSessionRegistry _registry;
    private readonly ICaptureStatistics _statistics;
    private readonly Dictionary<string, Loop> _loops = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public CapturePreviewService(ICameraSessionRegistry registry, ICaptureStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(statistics);
        _registry = registry;
        _statistics = statistics;
    }

    public IReadOnlyList<string> Active
    {
        get
        {
            lock (_sync) return [.. _loops.Keys];
        }
    }

    public async Task StartAsync(CameraId id, int fps, CancellationToken cancellationToken)
    {
        await StopAsync(id).ConfigureAwait(false);

        ICameraSession? session = _registry.Find(id)
            ?? throw new CameraOperationException(
                CaptureErrorCodes.CameraNotOpen,
                $"相机 {id} 未打开，请先调用 camera.open。");

        int intervalMs = Math.Clamp(1000 / Math.Clamp(fps, 1, 60), 10, 2000);

        var loop = new Loop();
        string key = id.Value ?? string.Empty;

        lock (_sync) _loops[key] = loop;

        loop.Worker = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));

            try
            {
                while (await timer.WaitForNextTickAsync(loop.Cancellation.Token).ConfigureAwait(false))
                {
                    try
                    {
                        Frame frame = await session.CaptureSingleAsync(options: null, loop.Cancellation.Token)
                            .ConfigureAwait(false);

                        _statistics.RecordSuccess(id, frame);

                        lock (loop.Sync) loop.Frame = frame;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // 单帧失败不终止预览：相机偶发超时是常见的
                        _statistics.RecordFailure(id, ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
        }, cancellationToken);
    }

    public Task StopAsync(CameraId id)
    {
        Loop? loop;

        lock (_sync)
        {
            if (!_loops.Remove(id.Value ?? string.Empty, out loop)) return Task.CompletedTask;
        }

        loop.Cancellation.Cancel();

        // 只等很短时间：取帧可能正卡在驱动里，不能因此挂住界面
        try { loop.Worker?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* 取消导致的异常属正常 */ }

        lock (loop.Sync) loop.Frame = null;

        loop.Cancellation.Dispose();

        return Task.CompletedTask;
    }

    public Frame? Latest(CameraId id)
    {
        lock (_sync)
        {
            if (!_loops.TryGetValue(id.Value ?? string.Empty, out Loop? loop)) return null;

            lock (loop.Sync) return loop.Frame;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (string key in Active)
            await StopAsync(new CameraId(key)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // 退出路径：只发起取消，不阻塞等待（与 CameraSessionRegistry 同样的理由）
        string[] keys = [.. Active];

        lock (_sync)
        {
            foreach (string key in keys)
            {
                if (_loops.Remove(key, out Loop? loop))
                {
                    try { loop.Cancellation.Cancel(); }
                    catch { /* 退出路径不抛 */ }
                }
            }
        }
    }
}
