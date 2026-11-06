using CameraControl.Domain.Capture;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Capture;

/// <summary>
/// 同步分组采集编排（Application 层核心用例）。
///
/// 顺序（不可调换）：
/// 1. 模式与预算校验（MotionFrozen 需要曝光/频闪预算）；
/// 2. 解析已打开会话（不自动打开，未打开即报错）；
/// 3. 就绪门（StaticSettled：振动衰减到阈值以下并持续稳定）；
/// 4. 武装；
/// 5. 触发；
/// 6. 并发收集各会话帧（每会话独立容错，缺失进 missing 列表）；
/// 7. 组装结果；
/// 8. 解除武装（finally 保证）。
/// </summary>
public sealed class CaptureGroupService : ICaptureGroupService
{
    private readonly ICameraSessionRegistry _sessions;
    private readonly IReadinessGate _readinessGate;
    private readonly ITriggerSourceFactory _triggerSources;
    private readonly IClock _clock;

    public CaptureGroupService(
        ICameraSessionRegistry sessions,
        IReadinessGate readinessGate,
        ITriggerSourceFactory triggerSources,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(readinessGate);
        ArgumentNullException.ThrowIfNull(triggerSources);
        ArgumentNullException.ThrowIfNull(clock);

        _sessions = sessions;
        _readinessGate = readinessGate;
        _triggerSources = triggerSources;
        _clock = clock;
    }

    public async Task<CaptureGroupResult> CaptureAsync(
        CaptureGroupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ── 1. 模式与预算 ──────────────────────────────────────
        if (request.Mode == CaptureMode.MotionFrozen)
        {
            throw new CameraOperationException(
                CaptureErrorCodes.ModeNotImplemented,
                "MotionFrozen（运动冻结采集）属于 P2 后期能力，当前版本未实现。请使用 StaticSettled。");
        }

        if (request.Trigger.IsHardware && _triggerSources.TryGet(TriggerSourceKind.Hardware) is null)
        {
            throw new CameraOperationException(
                CaptureErrorCodes.ModeNotImplemented,
                "硬件触发尚未实现，当前仅支持 software 触发。契约已就位，P2 提供实现。");
        }

        // ── 2. 解析会话 ────────────────────────────────────────
        List<ICameraSession> targets = ResolveSessions(request);

        // ── 3. 就绪门 ──────────────────────────────────────────
        ReadinessResult? readiness = null;
        if (request.Readiness is not null)
        {
            readiness = await _readinessGate
                .WaitAsync(request.Readiness, cancellationToken)
                .ConfigureAwait(false);

            if (!readiness.Ready)
            {
                throw new CameraOperationException(
                    CaptureErrorCodes.NotSettled,
                    $"等待系统稳定超时（{request.Readiness.Timeout.TotalMilliseconds:F0}ms），" +
                    "振动未衰减到阈值以下，为避免污染融合结果，本次不采集。");
            }
        }

        ITriggerSource trigger = _triggerSources.TryGet(request.Trigger.Kind)
            ?? throw new CameraOperationException(
                CaptureErrorCodes.ModeNotImplemented,
                $"触发类型 {request.Trigger.Kind} 尚未实现。");

        bool armed = false;
        try
        {
            // ── 4. 武装 ────────────────────────────────────────
            if (request.ArmFirst)
            {
                foreach (ICameraSession session in targets)
                    await session.ArmAsync(request.Trigger, cancellationToken).ConfigureAwait(false);

                armed = true;
            }

            // ── 5. 触发 ────────────────────────────────────────
            await trigger.FireAsync(request.Trigger, targets, cancellationToken).ConfigureAwait(false);

            // ── 6. 并发收集（每会话独立容错）──────────────────
            List<Frame> frames = await CollectFramesAsync(targets, request, cancellationToken)
                .ConfigureAwait(false);

            if (frames.Count == 0)
            {
                throw new CameraOperationException(
                    CaptureErrorCodes.CaptureTimeout,
                    $"在 {request.EffectiveTimeout.TotalMilliseconds:F0}ms 内没有任何相机返回帧。");
            }

            // ── 7. 组装 ────────────────────────────────────────
            TimestampQuality quality = request.Trigger.IsHardware
                ? TimestampQuality.HardwareStamped
                : TimestampQuality.SoftwareApprox;

            CaptureGroupResult result = CaptureGroupAssembler.Assemble(
                groupId: Guid.NewGuid().ToString("N"),
                frames: frames,
                requestedCameras: request.CameraIds,
                quality: quality,
                readiness: readiness);

            if (!result.IsComplete && result.Frames.Count > 0 && frames.Count < request.CameraIds.Count)
            {
                // 部分成功不是失败，但必须让上层看见；由能力层决定是否升级为错误
                return result;
            }

            return result;
        }
        finally
        {
            // ── 8. 解除武装（无论成败）────────────────────────
            if (armed)
            {
                foreach (ICameraSession session in targets)
                {
                    try
                    {
                        await session.DisarmAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 解除武装失败不应掩盖主流程异常
                    }
                }
            }
        }
    }

    private List<ICameraSession> ResolveSessions(CaptureGroupRequest request)
    {
        if (request.CameraIds.Count == 0)
        {
            throw new CameraOperationException(
                CaptureErrorCodes.InvalidParameter,
                "未指定任何相机。P0 需要显式传入至少一台相机 id。");
        }

        var targets = new List<ICameraSession>(request.CameraIds.Count);
        foreach (Domain.Cameras.CameraId id in request.CameraIds)
        {
            ICameraSession? session = _sessions.Find(id);
            if (session is null || !session.IsOpen)
            {
                throw new CameraOperationException(
                    CaptureErrorCodes.CameraNotOpen,
                    $"相机 '{id}' 未打开，无法采集。");
            }
            targets.Add(session);
        }

        return targets;
    }

    private async Task<List<Frame>> CollectFramesAsync(
        IReadOnlyList<ICameraSession> targets,
        CaptureGroupRequest request,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.EffectiveTimeout);

        // 每台相机独立起任务：一台超时/抛错不影响其它相机取帧
        Task<Frame?>[] tasks = [.. targets.Select(session => CaptureSafelyAsync(session, timeoutCts.Token))];
        Frame?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return [.. results.Where(f => f is not null).Select(f => f!)];
    }

    private static async Task<Frame?> CaptureSafelyAsync(ICameraSession session, CancellationToken cancellationToken)
    {
        try
        {
            return await session.CaptureSingleAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (CameraOperationException)
        {
            return null;
        }
    }
}
