using System.Text.Json;

namespace CameraControl.Application.Cua;

/// <summary>消息角色。</summary>
public enum LlmRole
{
    System = 0,
    User = 1,
    Assistant = 2,

    /// <summary>工具调用结果回填。</summary>
    Tool = 3
}

/// <summary>一次工具调用请求（由模型发起）。</summary>
/// <param name="Id">模型给出的调用 id，回填结果时必须原样带回。</param>
/// <param name="Name">工具名（OpenAI 兼容接口要求 <c>^[a-zA-Z0-9_-]{1,64}$</c>）。</param>
/// <param name="ArgumentsJson">参数 JSON 字符串。</param>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>一条对话消息。</summary>
/// <param name="Role">角色。</param>
/// <param name="Content">文本内容（工具调用消息可为 null）。</param>
/// <param name="ToolCalls">助手消息附带的工具调用。</param>
/// <param name="ToolCallId">工具消息对应的调用 id。</param>
/// <param name="Name">工具消息的工具名。</param>
/// <param name="ReasoningContent">
/// 思维链内容（DeepSeek 等思考模型返回）。
/// **带 tools 时必须原样回传给 API**，否则下一轮会 400。
/// 不带 tools 时可省略（API 会忽略）。
/// </param>
public sealed record LlmMessage(
    LlmRole Role,
    string? Content,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null,
    string? ReasoningContent = null)
{
    public static LlmMessage System(string content) => new(LlmRole.System, content);

    public static LlmMessage User(string content) => new(LlmRole.User, content);

    public static LlmMessage Assistant(
        string? content,
        IReadOnlyList<LlmToolCall>? toolCalls = null,
        string? reasoningContent = null) =>
        new(LlmRole.Assistant, content, toolCalls, ReasoningContent: reasoningContent);

    public static LlmMessage ToolResult(string toolCallId, string name, string content) =>
        new(LlmRole.Tool, content, ToolCallId: toolCallId, Name: name);
}

/// <summary>暴露给模型的工具定义。</summary>
/// <param name="Name">工具名（已做字符集转换）。</param>
/// <param name="Description">说明。</param>
/// <param name="ParametersJsonSchema">参数 schema。</param>
public sealed record LlmToolDefinition(string Name, string Description, string ParametersJsonSchema);

/// <summary>一次对话请求。</summary>
/// <param name="Messages">上下文。</param>
/// <param name="Tools">可用工具。</param>
/// <param name="Model">模型名；null 用提供方默认。</param>
/// <param name="Temperature">采样温度，默认 0.1（自动化场景要确定性）。</param>
/// <param name="MaxTokens">回复 token 上限。</param>
public sealed record LlmChatRequest(
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<LlmToolDefinition> Tools,
    string? Model = null,
    double Temperature = 0.1,
    int? MaxTokens = null);

/// <summary>
/// token 用量。
/// </summary>
/// <param name="PromptTokens">输入 token 总数（含缓存命中部分）。</param>
/// <param name="CompletionTokens">输出 token。</param>
/// <param name="CachedTokens">
/// 输入里**命中缓存**的部分。
/// DeepSeek 用 <c>prompt_cache_hit_tokens</c>，OpenAI 用 <c>prompt_tokens_details.cached_tokens</c>，
/// 两家字段名不同，解析时统一到这里。
/// </param>
/// <param name="CacheWriteTokens">缓存写入（目前只有部分厂商提供，未知为 0）。</param>
public sealed record LlmUsage(
    int PromptTokens,
    int CompletionTokens,
    int CachedTokens = 0,
    int CacheWriteTokens = 0)
{
    public int TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>未命中缓存的输入。</summary>
    public int UncachedInputTokens => Math.Max(0, PromptTokens - CachedTokens);

    /// <summary>缓存命中率（0–1）。</summary>
    public double CacheHitRate => PromptTokens <= 0 ? 0 : (double)CachedTokens / PromptTokens;

    /// <summary>累加（用于多轮汇总）。</summary>
    public LlmUsage Add(LlmUsage? other) => other is null
        ? this
        : new LlmUsage(
            PromptTokens + other.PromptTokens,
            CompletionTokens + other.CompletionTokens,
            CachedTokens + other.CachedTokens,
            CacheWriteTokens + other.CacheWriteTokens);
}

/// <summary>一次对话响应。</summary>
/// <param name="Message">助手回复。</param>
/// <param name="Usage">用量。</param>
/// <param name="FinishReason">结束原因（stop / tool_calls / length）。</param>
/// <param name="RequestId">提供方返回的请求 id（便于对账）。</param>
public sealed record LlmChatResponse(
    LlmMessage Message,
    LlmUsage? Usage = null,
    string? FinishReason = null,
    string? RequestId = null);

/// <summary>
/// 大模型提供方端口。
///
/// 只定义"一次带工具定义的对话"，不绑定任何厂商 SDK——
/// OpenAI / DeepSeek / 通义 / Ollama 大多兼容同一套 HTTP 协议，
/// 由 Infrastructure 的一个实现覆盖。
/// </summary>
public interface ILlmProvider
{
    /// <summary>提供方显示名（用于审计与日志）。</summary>
    string Name { get; }

    Task<LlmChatResponse> ChatAsync(LlmChatRequest request, CancellationToken cancellationToken);
}

/// <summary>LLM 调用失败。</summary>
public sealed class LlmException : Exception
{
    public LlmException(string errorCode, string message, Exception? inner = null)
        : base(message, inner) => ErrorCode = errorCode;

    /// <summary>稳定错误码：未配置 / 鉴权失败 / 限流 / 网络 / 协议不兼容。</summary>
    public string ErrorCode { get; }
}

/// <summary>LLM 错误码。</summary>
public static class LlmErrorCodes
{
    public const string NotConfigured = "LLM_NOT_CONFIGURED";
    public const string Unauthorized = "LLM_UNAUTHORIZED";
    public const string RateLimited = "LLM_RATE_LIMITED";
    public const string Network = "LLM_NETWORK_ERROR";
    public const string Protocol = "LLM_PROTOCOL_ERROR";
    public const string Timeout = "LLM_TIMEOUT";
}

/// <summary>工具名与能力 id 的互转。</summary>
/// <remarks>
/// OpenAI 兼容接口要求函数名只能含 <c>[a-zA-Z0-9_-]</c>，
/// 而能力 id 用点分层（<c>capture.group</c>），因此必须做映射。
/// </remarks>
public static class ToolName
{
    public static string FromCapabilityId(string capabilityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        return capabilityId.Replace('.', '_');
    }

    /// <summary>把工具名还原成能力 id（借助能力清单，避免歧义）。</summary>
    public static string? ToCapabilityId(string toolName, IEnumerable<string> knownCapabilityIds)
    {
        ArgumentNullException.ThrowIfNull(knownCapabilityIds);

        foreach (string id in knownCapabilityIds)
        {
            if (string.Equals(FromCapabilityId(id), toolName, StringComparison.Ordinal))
                return id;
        }

        return null;
    }
}

/// <summary>参数解析辅助。</summary>
public static class JsonArgs
{
    public static JsonDocument Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return JsonDocument.Parse("{}");
        return JsonDocument.Parse(json);
    }

    public static string? GetString(JsonElement root, string property)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(property, out JsonElement value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public static int? GetInt(JsonElement root, string property)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(property, out JsonElement value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out int result)
            ? result
            : null;
    }

    public static bool? GetBool(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out JsonElement value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public static double? GetDouble(JsonElement root, string property)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(property, out JsonElement value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetDouble(out double result)
            ? result
            : null;
    }

    /// <summary>必填数值；缺失时抛 <see cref="JsonException"/>，由绑定器转成结构化错误回填给模型。</summary>
    public static double RequireDouble(JsonElement root, string property) =>
        GetDouble(root, property)
        ?? throw new JsonException($"缺少必填数值参数 {property}。");

    public static IReadOnlyList<string> GetStringArray(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))];
    }
}
