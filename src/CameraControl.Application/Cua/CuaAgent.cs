using System.Text;
using System.Text.Json;
using CameraControl.Application.Diagnostics;

namespace CameraControl.Application.Cua;

/// <summary>提示词构造。</summary>
public static class CuaPromptBuilder
{
    /// <summary>
    /// 构造系统提示。
    ///
    /// 刻意写清楚"只能通过工具做事""不确定就查清单""失败要如实报告"，
    /// 因为模型最容易犯的两类错是：编造能力名、把失败圆成成功。
    /// </summary>
    public static string BuildSystemPrompt(string capabilityCatalogMarkdown, string providerName)
    {
        ArgumentNullException.ThrowIfNull(capabilityCatalogMarkdown);

        var builder = new StringBuilder();
        builder.AppendLine("你是一台机器视觉工位控制软件的助手，通过调用工具（capability）来操作软件。");
        builder.AppendLine();
        builder.AppendLine("硬性规则：");
        builder.AppendLine("1. 你**只能**通过下面清单里的工具做事。不要臆造工具名或参数。");
        builder.AppendLine("2. 不知道有哪些设备、状态如何时，先调用只读工具查询，不要猜。");
        builder.AppendLine("3. 工具返回 ok=false 时，如实向用户报告错误码与建议，不要假装成功。");
        builder.AppendLine("4. 标记为 Physical / Destructive 的操作需要人工确认；若被拒绝，把原因告诉用户，不要重试绕过。");
        builder.AppendLine("5. 任务完成后用一句中文总结你做了什么、结果是什么。");
        builder.AppendLine("6. 不要输出与任务无关的解释，保持简洁。");
        builder.AppendLine();
        builder.AppendLine($"当前模型提供方：{providerName}");
        builder.AppendLine();
        builder.AppendLine("可用能力：");
        builder.AppendLine(capabilityCatalogMarkdown);

        return builder.ToString();
    }
}

/// <summary>
/// AI 代理：驱动"模型 → 工具 → 结果 → 模型"的循环。
///
/// 安全边界：
/// - 所有工具调用都经 <see cref="Commands.ICommandBus"/>，因此风险策略、审计、指标全部生效；
/// - 物理/破坏性操作由 <see cref="Commands.IRiskPolicy"/> 决定是否拒绝，代理没有绕过路径；
/// - 有最大轮数上限，避免模型死循环烧 token。
/// </summary>
public sealed class CuaAgent : IAiAgent
{
    /// <summary>单次工具结果回填的最大字符数，防止上下文被大对象撑爆。</summary>
    private const int MaxToolResultChars = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILlmProvider _provider;
    private readonly Commands.ICapabilityCatalog _catalog;
    private readonly ICommandBinder _binder;
    private readonly Commands.ICommandBus _bus;
    private readonly Diagnostics.IMetrics _metrics;
    private readonly Diagnostics.IAppLogger _logger;

    public CuaAgent(
        ILlmProvider provider,
        Commands.ICapabilityCatalog catalog,
        ICommandBinder binder,
        Commands.ICommandBus bus,
        Diagnostics.IMetrics metrics,
        Diagnostics.IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(binder);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _provider = provider;
        _catalog = catalog;
        _binder = binder;
        _bus = bus;
        _metrics = metrics;
        _logger = logger;
    }

    public string ProviderName => _provider.Name;

    public async Task<AiTaskResult> RunAsync(
        AiTaskRequest request,
        Commands.Actor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var steps = new List<AiStep>();
        _metrics.Increment("ai.task.total");

        IReadOnlyList<Commands.CapabilityDescriptor> descriptors = _catalog.All;
        string catalogMarkdown = CapabilityCatalogExporter.ToMarkdown(
            CapabilityCatalogExporter.Export(descriptors, DateTimeOffset.UtcNow));

        IReadOnlyList<LlmToolDefinition> tools = [.. descriptors.Select(d => new LlmToolDefinition(
            Name: ToolName.FromCapabilityId(d.Id),
            Description: d.Description,
            ParametersJsonSchema: d.ParametersJsonSchema ?? """{ "type": "object", "properties": {} }"""))];

        var messages = new List<LlmMessage>
        {
            LlmMessage.System(CuaPromptBuilder.BuildSystemPrompt(catalogMarkdown, _provider.Name)),
            LlmMessage.User(request.Goal)
        };

        LlmUsage? totalUsage = null;
        int maxSteps = Math.Clamp(request.MaxSteps, 1, 32);

        for (int step = 1; step <= maxSteps; step++)
        {
            if (cancellationToken.IsCancellationRequested)
                return Failure(AiStopReason.Cancelled, steps, totalUsage, "任务已取消。", "cancelled");

            LlmChatResponse response;
            try
            {
                response = await _provider.ChatAsync(
                    new LlmChatRequest(messages, tools, request.Model),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Failure(AiStopReason.Cancelled, steps, totalUsage, "任务已取消。", "cancelled");
            }
            catch (LlmException ex)
            {
                _logger.Warn("AI 任务失败：LLM 调用异常", new Dictionary<string, object?>
                {
                    ["provider"] = _provider.Name,
                    ["code"] = ex.ErrorCode
                });

                return Failure(AiStopReason.LlmError, steps, totalUsage, ex.Message, ex.ErrorCode);
            }
            catch (Exception ex)
            {
                _logger.Error("AI 任务失败：未预期异常", new Dictionary<string, object?>
                {
                    ["provider"] = _provider.Name,
                    ["error"] = ex.Message
                });

                return Failure(AiStopReason.Error, steps, totalUsage, ex.Message, "internal");
            }

            totalUsage = Accumulate(totalUsage, response.Usage);

            IReadOnlyList<LlmToolCall> toolCalls = response.Message.ToolCalls ?? [];

            if (toolCalls.Count == 0)
            {
                steps.Add(new AiStep(step, response.Message.Content, []));
                _metrics.Increment("ai.task.completed");

                return new AiTaskResult(
                    AiStopReason.Completed,
                    response.Message.Content,
                    steps,
                    totalUsage);
            }

            // 把助手的工具调用消息本身也加进上下文，模型才知道自己请求过什么
            messages.Add(response.Message);

            var invocations = new List<AiToolInvocation>(toolCalls.Count);

            foreach (LlmToolCall call in toolCalls)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Failure(AiStopReason.Cancelled, steps, totalUsage, "任务已取消。", "cancelled");

                (string toolResult, AiToolInvocation invocation) = await InvokeAsync(call, actor, cancellationToken)
                    .ConfigureAwait(false);

                invocations.Add(invocation);
                messages.Add(LlmMessage.ToolResult(call.Id, call.Name, toolResult));
            }

            steps.Add(new AiStep(step, response.Message.Content, invocations));
        }

        _metrics.Increment("ai.task.max_steps_reached");

        return new AiTaskResult(
            AiStopReason.MaxSteps,
            null,
            steps,
            totalUsage,
            $"达到最大轮数（{maxSteps}）仍未完成，已停止以避免死循环。");
    }

    private async Task<(string ToolResult, AiToolInvocation Invocation)> InvokeAsync(
        LlmToolCall call,
        Commands.Actor actor,
        CancellationToken cancellationToken)
    {
        string? capabilityId = ToolName.ToCapabilityId(
            call.Name,
            _catalog.All.Select(d => d.Id));

        if (capabilityId is null)
        {
            string json = ErrorJson(AiErrorCodes.UnknownTool,
                $"没有名为 {call.Name} 的工具。请只使用清单里列出的工具。");

            return (json, new AiToolInvocation(call.Name, call.ArgumentsJson, false,
                AiErrorCodes.UnknownTool, Truncate(json), 0));
        }

        JsonElement arguments = ParseArgs(call.ArgumentsJson, out string? parseError);

        if (parseError is not null)
        {
            string json = ErrorJson(AiErrorCodes.BadArguments, parseError);
            return (json, new AiToolInvocation(capabilityId, call.ArgumentsJson, false,
                AiErrorCodes.BadArguments, Truncate(json), 0));
        }

        Commands.ICommand? command = _binder.Bind(capabilityId, arguments, out string? bindError);

        if (command is null)
        {
            string json = ErrorJson(AiErrorCodes.BadArguments, bindError ?? "参数无法绑定。");
            return (json, new AiToolInvocation(capabilityId, call.ArgumentsJson, false,
                AiErrorCodes.BadArguments, Truncate(json), 0));
        }

        _metrics.Increment($"ai.tool.{capabilityId}.total");

        Commands.CommandResult result = await _bus
            .ExecuteAsync(command, actor, cancellationToken)
            .ConfigureAwait(false);

        string payload = result.Ok
            ? SuccessJson(result.Data)
            : ErrorJson(result.Error?.Code ?? "ERROR", result.Error?.Message ?? "执行失败。", result.Error?.Hint);

        return (payload, new AiToolInvocation(
            capabilityId,
            call.ArgumentsJson,
            result.Ok,
            result.Error?.Code,
            Truncate(payload),
            result.Elapsed.TotalMilliseconds));
    }

    private static JsonElement ParseArgs(string? argumentsJson, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(argumentsJson)) return SafeRoot("{}");

        try
        {
            using JsonDocument document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            error = $"工具参数不是合法 JSON：{ex.Message}";
            return SafeRoot("{}");
        }
    }

    private static JsonElement SafeRoot(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string SuccessJson(object? data)
    {
        object payload = new { ok = true, data };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string ErrorJson(string code, string message, string? hint = null)
    {
        object payload = new { ok = false, error = new { code, message, hint } };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaxToolResultChars) return text;
        return text[..MaxToolResultChars] + "…(已截断)";
    }

    private static LlmUsage? Accumulate(LlmUsage? total, LlmUsage? delta)
    {
        if (delta is null) return total;
        if (total is null) return delta;

        return new LlmUsage(
            total.PromptTokens + delta.PromptTokens,
            total.CompletionTokens + delta.CompletionTokens);
    }

    private static AiTaskResult Failure(
        AiStopReason reason,
        IReadOnlyList<AiStep> steps,
        LlmUsage? usage,
        string message,
        string code)
    {
        return new AiTaskResult(reason, null, steps, usage, $"[{code}] {message}");
    }
}
