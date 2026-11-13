namespace CameraControl.Application.Cua;

/// <summary>一次 AI 任务请求。</summary>
/// <param name="Goal">自然语言目标，如"拍一张当前工件并告诉我分辨率"。</param>
/// <param name="MaxSteps">最大工具调用轮数，防止死循环。</param>
/// <param name="Model">模型名；null 用提供方默认。</param>
/// <param name="ContextProfileId">
/// 用哪套配置的上下文文档。null 表示不读写上下文（例如连通性测试）。
/// </param>
public sealed record AiTaskRequest(
    string Goal,
    int MaxSteps = 8,
    string? Model = null,
    string? ContextProfileId = null);

/// <summary>单次工具调用记录。</summary>
/// <param name="CapabilityId">能力 id（绑定时已知）；无法识别时为原始工具名。</param>
/// <param name="ArgumentsJson">模型给出的参数。</param>
/// <param name="Ok">是否成功。</param>
/// <param name="ErrorCode">失败错误码。</param>
/// <param name="ResultPreview">结果摘要（截断后）。</param>
/// <param name="ElapsedMs">耗时。</param>
public sealed record AiToolInvocation(
    string CapabilityId,
    string ArgumentsJson,
    bool Ok,
    string? ErrorCode,
    string ResultPreview,
    double ElapsedMs);

/// <summary>一步（一轮模型回复 + 其触发的工具调用）。</summary>
/// <param name="Step">序号（从 1 开始）。</param>
/// <param name="AssistantText">模型这一轮的文字（可为空）。</param>
/// <param name="Invocations">这一轮触发的工具调用。</param>
public sealed record AiStep(int Step, string? AssistantText, IReadOnlyList<AiToolInvocation> Invocations);

/// <summary>任务结束原因。</summary>
public enum AiStopReason
{
    /// <summary>模型给出最终答复。</summary>
    Completed = 0,

    /// <summary>达到最大轮数。</summary>
    MaxSteps = 1,

    /// <summary>被取消。</summary>
    Cancelled = 2,

    /// <summary>LLM 调用失败。</summary>
    LlmError = 3,

    /// <summary>其它未预期错误。</summary>
    Error = 4
}

/// <summary>一次 AI 任务的执行结果。</summary>
/// <param name="StopReason">结束原因。</param>
/// <param name="Answer">模型最终答复（<see cref="AiStopReason.Completed"/> 时非空）。</param>
/// <param name="Steps">执行轨迹，用于审计与回放。</param>
/// <param name="Usage">token 用量。</param>
/// <param name="Error">失败说明。</param>
public sealed record AiTaskResult(
    AiStopReason StopReason,
    string? Answer,
    IReadOnlyList<AiStep> Steps,
    LlmUsage? Usage = null,
    string? Error = null)
{
    public bool Completed => StopReason == AiStopReason.Completed;

    public int ToolCallCount => Steps.Sum(s => s.Invocations.Count);
}

/// <summary>AI 代理端口。</summary>
public interface IAiAgent
{
    /// <summary>提供方名称（审计用）。</summary>
    string ProviderName { get; }

    Task<AiTaskResult> RunAsync(
        AiTaskRequest request,
        Commands.Actor actor,
        CancellationToken cancellationToken);
}

/// <summary>AI 相关错误码。</summary>
public static class AiErrorCodes
{
    public const string UnknownTool = "AI_UNKNOWN_TOOL";
    public const string BadArguments = "AI_BAD_ARGUMENTS";
    public const string MaxSteps = "AI_MAX_STEPS_REACHED";
    public const string NotConfigured = "AI_NOT_CONFIGURED";
}
