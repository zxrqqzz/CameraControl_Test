namespace CameraControl.Application.Commands;

/// <summary>发起者类型。</summary>
public enum ActorKind
{
    /// <summary>人类用户（UI 操作）。</summary>
    Human = 0,

    /// <summary>AI 代理（CUA）。</summary>
    Ai = 1,

    /// <summary>系统内部（定时任务、流程触发）。</summary>
    System = 2
}

/// <summary>
/// 命令发起者。
/// 审计日志必须记录它——"谁下的指令"是事故追溯的第一要素。
/// </summary>
/// <param name="Kind">发起者类型。</param>
/// <param name="Name">显示名（人类账号 / AI 会话名）。</param>
/// <param name="SessionId">AI 会话 id（用于串起一整条任务链）。</param>
public sealed record Actor(ActorKind Kind, string? Name = null, string? SessionId = null)
{
    public static Actor Human(string? name = null) => new(ActorKind.Human, name);

    public static Actor Ai(string? name = null, string? sessionId = null) =>
        new(ActorKind.Ai, name, sessionId);

    public static Actor System(string? name = null) => new(ActorKind.System, name);

    public bool IsAi => Kind == ActorKind.Ai;
}

/// <summary>
/// 一个可被调用的原子命令。
/// 所有命令都必须有稳定的 <see cref="CapabilityId"/>，用于白名单校验与审计。
/// </summary>
public interface ICommand
{
    string CapabilityId { get; }
}

/// <summary>命令执行上下文（由总线注入，不由调用方伪造）。</summary>
/// <param name="Actor">发起者。</param>
/// <param name="IssuedAt">下发时间。</param>
public sealed record CommandContext(Actor Actor, DateTimeOffset IssuedAt);

/// <summary>结构化错误。AI 依赖它自我纠正，因此必须包含 code/retryable/hint。</summary>
/// <param name="Code">稳定错误码，如 CAMERA_NOT_OPEN。</param>
/// <param name="Message">人可读说明。</param>
/// <param name="Retryable">是否可重试。</param>
/// <param name="Hint">给调用方（尤其 AI）的下一步建议。</param>
public sealed record CommandError(
    string Code,
    string Message,
    bool Retryable = false,
    string? Hint = null);

/// <summary>命令执行结果。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="CapabilityId">能力 id。</param>
/// <param name="Data">成功时的返回数据。</param>
/// <param name="Error">失败时的结构化错误。</param>
/// <param name="Elapsed">执行耗时。</param>
public sealed record CommandResult(
    bool Ok,
    string CapabilityId,
    object? Data,
    CommandError? Error,
    TimeSpan Elapsed)
{
    public static CommandResult Success(string capabilityId, object? data, TimeSpan elapsed) =>
        new(true, capabilityId, data, null, elapsed);

    public static CommandResult Failure(string capabilityId, CommandError error, TimeSpan elapsed) =>
        new(false, capabilityId, null, error, elapsed);
}
