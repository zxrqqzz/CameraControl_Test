using System.Diagnostics;
using CameraControl.Application.Auditing;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Commands;

/// <summary>
/// 命令总线实现。
///
/// 执行管线（顺序不可调换）：
/// 1. 白名单校验 —— 不在能力清单里直接拒绝；
/// 2. 风险判定   —— 物理/破坏性操作对 AI 直接拒绝；
/// 3. 执行处理器；
/// 4. 审计落痕   —— 成功与失败都记录，被拒绝也记录。
/// </summary>
public sealed class CommandBus : ICommandBus
{
    private readonly CapabilityRegistry _registry;
    private readonly IRiskPolicy _riskPolicy;
    private readonly IAuditLog _auditLog;
    private readonly IClock _clock;

    public CommandBus(
        CapabilityRegistry registry,
        IRiskPolicy riskPolicy,
        IAuditLog auditLog,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(riskPolicy);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(clock);

        _registry = registry;
        _riskPolicy = riskPolicy;
        _auditLog = auditLog;
        _clock = clock;
    }

    public async Task<CommandResult> ExecuteAsync(
        ICommand command,
        Actor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);

        string capabilityId = command.CapabilityId ?? string.Empty;
        // Stopwatch 用单调时钟，避免系统时间跳变影响耗时统计
        var stopwatch = Stopwatch.StartNew();

        if (!_registry.TryGetEntry(capabilityId, out CapabilityRegistry.Entry entry))
        {
            stopwatch.Stop();
            var unknown = new CommandError(
                CommandErrorCodes.UnknownCapability,
                $"能力 '{capabilityId}' 不存在。",
                Retryable: false,
                Hint: "调用 cua.getCapabilities 查看可用能力清单。");

            Record(capabilityId, RiskLevel.Destructive, actor, ok: false, unknown.Code, stopwatch.Elapsed);
            return CommandResult.Failure(capabilityId, unknown, stopwatch.Elapsed);
        }

        RiskDecision decision = _riskPolicy.Evaluate(entry.Descriptor, actor);
        if (!decision.Allowed)
        {
            stopwatch.Stop();
            var denied = new CommandError(
                decision.Code ?? CommandErrorCodes.Denied,
                decision.Message ?? "该操作被安全策略拒绝。",
                Retryable: false,
                Hint: "请由人工在界面上确认并执行。");

            Record(capabilityId, entry.Descriptor.Risk, actor, ok: false, denied.Code, stopwatch.Elapsed);
            return CommandResult.Failure(capabilityId, denied, stopwatch.Elapsed);
        }

        var context = new CommandContext(actor, _clock.UtcNow);

        try
        {
            object? data = await entry.Invoke(command, context, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            Record(capabilityId, entry.Descriptor.Risk, actor, ok: true, null, stopwatch.Elapsed);
            return CommandResult.Success(capabilityId, data, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var cancelled = new CommandError(CommandErrorCodes.Cancelled, "操作已取消。", Retryable: true);
            Record(capabilityId, entry.Descriptor.Risk, actor, ok: false, cancelled.Code, stopwatch.Elapsed);
            return CommandResult.Failure(capabilityId, cancelled, stopwatch.Elapsed);
        }
        catch (CameraOperationException ex)
        {
            stopwatch.Stop();
            var domainError = new CommandError(ex.ErrorCode, ex.Message, Retryable: true, Hint: null);
            Record(capabilityId, entry.Descriptor.Risk, actor, ok: false, domainError.Code, stopwatch.Elapsed);
            return CommandResult.Failure(capabilityId, domainError, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var internalError = new CommandError(
                CommandErrorCodes.Internal,
                ex.Message,
                Retryable: false,
                Hint: "查看日志与诊断包。");

            Record(capabilityId, entry.Descriptor.Risk, actor, ok: false, internalError.Code, stopwatch.Elapsed);
            return CommandResult.Failure(capabilityId, internalError, stopwatch.Elapsed);
        }
    }

    private void Record(
        string capabilityId,
        RiskLevel risk,
        Actor actor,
        bool ok,
        string? errorCode,
        TimeSpan elapsed)
    {
        _auditLog.Write(new AuditEntry(
            At: _clock.UtcNow,
            Actor: actor,
            CapabilityId: capabilityId,
            Risk: risk,
            Ok: ok,
            ErrorCode: errorCode,
            Elapsed: elapsed));
    }
}
