using CameraControl.Domain.Capture;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Capture;

/// <summary>采集相关错误码（稳定，供 AI 自我纠正）。</summary>
public static class CaptureErrorCodes
{
    /// <summary>目标会话未打开。</summary>
    public const string CameraNotOpen = "CAMERA_NOT_OPEN";

    /// <summary>采集超时。</summary>
    public const string CaptureTimeout = "CAPTURE_TIMEOUT";

    /// <summary>该触发/采集模式尚未实现。</summary>
    public const string ModeNotImplemented = "TRIGGER_MODE_NOT_IMPLEMENTED";

    /// <summary>等待稳定超时，系统未进入可采集状态。</summary>
    public const string NotSettled = "NOT_SETTLED";

    /// <summary>曝光/模糊预算不通过。</summary>
    public const string BlurBudgetExceeded = "BLUR_BUDGET_EXCEEDED";

    /// <summary>部分相机未取到帧。</summary>
    public const string PartialGroupCapture = "PARTIAL_GROUP_CAPTURE";

    /// <summary>参数非法。</summary>
    public const string InvalidParameter = "INVALID_PARAMETER";
}

/// <summary>同步分组采集服务。</summary>
public interface ICaptureGroupService
{
    /// <summary>
    /// 执行一次同步分组采集。失败时抛 <see cref="CameraOperationException"/>（带错误码）。
    /// </summary>
    Task<CaptureGroupResult> CaptureAsync(CaptureGroupRequest request, CancellationToken cancellationToken);
}

/// <summary>按触发源类型解析可用的触发实现。</summary>
public interface ITriggerSourceFactory
{
    /// <summary>返回 null 表示该类型尚未实现。</summary>
    ITriggerSource? TryGet(TriggerSourceKind kind);
}

/// <summary>已打开相机会话的注册表。</summary>
public interface ICameraSessionRegistry
{
    IReadOnlyList<ICameraSession> OpenSessions { get; }

    ICameraSession? Find(Domain.Cameras.CameraId id);

    /// <summary>打开会话；已打开则复用。</summary>
    Task<ICameraSession> OpenAsync(Domain.Cameras.CameraId id, CancellationToken cancellationToken);

    /// <summary>关闭并移除会话。</summary>
    Task CloseAsync(Domain.Cameras.CameraId id);
}

/// <summary>
/// 已打开会话的注册表实现。
/// 负责"打开/关闭/查找"，不做取流；单台相机异常不影响其它会话。
/// </summary>
public sealed class CameraSessionRegistry : ICameraSessionRegistry, IAsyncDisposable, IDisposable
{
    private readonly ICameraProvider _provider;
    private readonly Dictionary<string, ICameraSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public CameraSessionRegistry(ICameraProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public IReadOnlyList<ICameraSession> OpenSessions
    {
        get
        {
            lock (_sync) return [.. _sessions.Values];
        }
    }

    public ICameraSession? Find(Domain.Cameras.CameraId id)
    {
        lock (_sync)
            return _sessions.TryGetValue(id.Value ?? string.Empty, out ICameraSession? session)
                ? session
                : null;
    }

    /// <summary>打开会话；已打开则直接返回现有会话。</summary>
    public async Task<ICameraSession> OpenAsync(Domain.Cameras.CameraId id, CancellationToken cancellationToken)    {
        ICameraSession? existing = Find(id);
        if (existing is not null) return existing;

        ICameraSession session = await _provider.OpenAsync(id, cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            // 并发打开时以先注册者为准
            if (_sessions.TryGetValue(id.Value ?? string.Empty, out ICameraSession? raced))
                return raced;

            _sessions[id.Value ?? string.Empty] = session;
            return session;
        }
    }

    /// <summary>关闭并移除会话。</summary>
    public async Task CloseAsync(Domain.Cameras.CameraId id)
    {
        ICameraSession? session;
        lock (_sync)
        {
            string key = id.Value ?? string.Empty;
            if (!_sessions.Remove(key, out session)) return;
        }

        await session.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        ICameraSession[] sessions;
        lock (_sync)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (ICameraSession session in sessions)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 释放阶段吞掉异常是刻意的：一台相机关不掉不应影响其它相机释放
            }
        }
    }

    /// <summary>
    /// 同步释放（应用退出路径）。
    ///
    /// 为什么需要：<see cref="IServiceProvider"/> 的同步 <c>Dispose()</c> 一旦遇到
    /// **只实现 IAsyncDisposable** 的服务会直接抛 <see cref="InvalidOperationException"/>，
    /// 而 WPF 的 <c>OnExit</c> 只能同步收尾。
    ///
    /// 为什么**不阻塞等待**：WinRT 的 <c>MediaCapture</c> 收尾需要 STA 线程，
    /// 在 UI 线程上同步等待自己的异步续体必然死锁。
    /// 因此这里只在**当前线程发起**释放（满足 STA 亲和），不等待完成，
    /// 异常通过 ContinueWith 观察掉；进程随即结束，句柄由系统回收。
    /// </summary>
    public void Dispose()
    {
        ICameraSession[] sessions;
        lock (_sync)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (ICameraSession session in sessions)
        {
            try
            {
                _ = session.DisposeAsync().AsTask().ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch
            {
                // 退出路径不得因为释放失败而崩溃
            }
        }
    }
}
