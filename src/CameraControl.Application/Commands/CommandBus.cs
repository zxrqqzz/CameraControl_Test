using System.Diagnostics;
using CameraControl.Application.Auditing;
using CameraControl.Application.Diagnostics;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Commands;

/// <summary>
/// 命令总线实现。
///
/// 执行管线（顺序不可调换）：
/// 1. 白名单校验 —— 不在能力清单里直接拒绝；
/// 2. 风险判定   —— 物理/破坏性操作对 AI 拒绝；
/// 3. 人工确认   —— 被判定"需确认"时，若装了确认门则弹窗询问，批准后继续，拒绝则中止；
/// 4. 执行处理器；
/// 5. 指标记录   —— 次数/失败数/耗时/确认次数；
/// 6. 审计落痕   —— 成功与失败都记录，被拒绝也记录。
///
/// 注意：确认门是**可选**依赖。没装（如无 UI 的 CLI）时保持"直接拒绝"的失败关闭行为。
/// </summary>
public sealed class CommandBus : ICommandBus
{
    private readonly CapabilityRegistry _registry;
    private readonly IRiskPolicy _riskPolicy;
    private readonly IAuditLog _auditLog;
    private readonly IClock _clock;
    private readonly IMetrics _metrics;
    private readonly IAppLogger _logger;
    // 用 IEnumerable 注入而不是可选单个实例：
    // MS.DI 对未注册的 IEnumerable 会解析成空集合（而不是抛异常），
    // 这样"没装确认门"就天然等于"保持失败关闭"。
    private readonly IConfirmationGate? _confirmationGate;

    public CommandBus(
        CapabilityRegistry registry,
        IRiskPolicy riskPolicy,
        IAuditLog auditLog,
        IClock clock,
        IMetrics metrics,
        IAppLogger logger,
        IEnumerable<IConfirmationGate>? confirmationGates = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(riskPolicy);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _riskPolicy = riskPolicy;
        _auditLog = auditLog;
        _clock = clock;
        _metrics = metrics;
        _logger = logger;
        _confirmationGate = confirmationGates?.FirstOrDefault();
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
            bool needsConfirmation = string.Equals(
                decision.Code,
                CommandErrorCodes.ConfirmationRequired,
                StringComparison.Ordinal);

            // 装了确认门：请人来决定；没装：保持失败关闭（直接拒绝）
            if (needsConfirmation && _confirmationGate is not null)
            {
                _metrics.Increment($"command.{capabilityId}.confirmation_requested");

                bool approved = await _confirmationGate
                    .RequestAsync(entry.Descriptor, actor, $"{entry.Descriptor.Title}（{capabilityId}）", cancellationToken)
                    .ConfigureAwait(false);

                if (!approved)
                {
                    stopwatch.Stop();
                    _metrics.Increment($"command.{capabilityId}.confirmation_denied");

                    var rejected = new CommandError(
                        CommandErrorCodes.Cancelled,
                        "操作已被人工拒绝。",
                        Retryable: false,
                        Hint: "如需执行，请由操作员在界面上确认。");

                    Record(capabilityId, entry.Descriptor.Risk, actor, ok: false, rejected.Code, stopwatch.Elapsed);
                    return CommandResult.Failure(capabilityId, rejected, stopwatch.Elapsed);
                }

                _metrics.Increment($"command.{capabilityId}.confirmation_approved");
                _logger.Info("人工已确认执行", new Dictionary<string, object?>
                {
                    ["capability"] = capabilityId,
                    ["risk"] = entry.Descriptor.Risk.ToString(),
                    ["requestedBy"] = actor.Name
                });
            }
            else
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

        // 指标：按能力聚合，便于看"哪个能力慢/失败多"
        _metrics.Increment($"command.{capabilityId}.total");
        _metrics.Observe($"command.{capabilityId}.duration_ms", elapsed.TotalMilliseconds);

        if (!ok)
        {
            _metrics.Increment($"command.{capabilityId}.failed");
            _logger.Warn("命令失败", new Dictionary<string, object?>
            {
                ["capability"] = capabilityId,
                ["risk"] = risk.ToString(),
                ["actor"] = actor.Kind.ToString(),
                ["actorName"] = actor.Name,
                ["error"] = errorCode,
                ["elapsedMs"] = Math.Round(elapsed.TotalMilliseconds, 2)
            });
        }
        else
        {
            _logger.Info("命令成功", new Dictionary<string, object?>
            {
                ["capability"] = capabilityId,
                ["risk"] = risk.ToString(),
                ["actor"] = actor.Kind.ToString(),
                ["actorName"] = actor.Name,
                ["elapsedMs"] = Math.Round(elapsed.TotalMilliseconds, 2)
            });
        }
    }
}
