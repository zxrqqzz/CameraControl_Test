namespace CameraControl.Application.Commands;

/// <summary>命令总线层面的通用错误码（稳定，不随文案变化）。</summary>
public static class CommandErrorCodes
{
    /// <summary>能力 id 不在白名单内。</summary>
    public const string UnknownCapability = "UNKNOWN_CAPABILITY";

    /// <summary>该操作需要人工确认，当前发起者无权直接执行。</summary>
    public const string ConfirmationRequired = "CONFIRMATION_REQUIRED";

    /// <summary>被安全策略明确拒绝。</summary>
    public const string Denied = "DENIED";

    /// <summary>被取消。</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>未预期的内部错误。</summary>
    public const string Internal = "INTERNAL_ERROR";
}

/// <summary>安全策略对某次调用的判定结果。</summary>
/// <param name="Allowed">是否允许直接执行。</param>
/// <param name="Code">拒绝时的错误码。</param>
/// <param name="Message">拒绝原因说明。</param>
public sealed record RiskDecision(bool Allowed, string? Code = null, string? Message = null)
{
    public static readonly RiskDecision Allow = new(true);

    public static RiskDecision Reject(string code, string message) => new(false, code, message);
}

/// <summary>
/// 风险策略：决定"谁可以不经确认直接执行什么"。
/// </summary>
public interface IRiskPolicy
{
    RiskDecision Evaluate(CapabilityDescriptor descriptor, Actor actor);
}

/// <summary>
/// P0 默认策略（分级授权）。
///
/// - ReadOnly / Reversible：人 与 AI 都可直接执行（AI 需留痕，由审计保证）。
/// - Physical / Destructive：**AI 一律拒绝**（返回 CONFIRMATION_REQUIRED）；
///   人类在 P0 尚无确认弹窗，界面点击本身视为确认。
///
/// 后续接入确认门后，人类也要走 <see cref="IConfirmationGate"/>。
/// </summary>
public sealed class DefaultRiskPolicy : IRiskPolicy
{
    public RiskDecision Evaluate(CapabilityDescriptor descriptor, Actor actor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(actor);

        if (!descriptor.RequiresConfirmation)
            return RiskDecision.Allow;

        if (actor.Kind == ActorKind.Human)
            return RiskDecision.Allow;

        return RiskDecision.Reject(
            CommandErrorCodes.ConfirmationRequired,
            $"能力 {descriptor.Id} 属于 {descriptor.Risk} 级操作，AI 不能直接执行，需人工确认后由人发起。");
    }
}

/// <summary>人工确认门（P2 接入）。P0 仅定义契约。</summary>
public interface IConfirmationGate
{
    Task<bool> RequestAsync(
        CapabilityDescriptor descriptor,
        Actor actor,
        string? summary,
        CancellationToken cancellationToken);
}
