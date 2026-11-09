using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CameraControl.Application.Cua;

namespace CameraControl.Infrastructure.Ai;

/// <summary>
/// OpenAI 兼容的对话提供方。
///
/// 覆盖大多数服务（OpenAI / DeepSeek / 通义 / Moonshot / 本地 Ollama 的 /v1 等），
/// 因此不为每家写一个适配器——只需要配置 baseUrl + model + apiKey。
///
/// 只实现本项目需要的最小面：chat/completions + function calling。
/// </summary>
public sealed class OpenAiCompatibleProvider : ILlmProvider, IDisposable
{
    private const string ChatCompletionsPath = "chat/completions";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly double _temperature;
    private readonly HttpClient _http;

    public OpenAiCompatibleProvider(string baseUrl, string apiKey, string model, double temperature, int timeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
        _temperature = temperature;

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 600))
        };
    }

    public string Name => "OpenAI-Compatible";

    public async Task<LlmChatResponse> ChatAsync(LlmChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string json = BuildRequestJson(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/{ChatCompletionsPath}")
        {
            Content = content
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        HttpResponseMessage response;
        string body;

        try
        {
            response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException(LlmErrorCodes.Timeout, "调用模型超时。");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(LlmErrorCodes.Network, $"网络错误：{ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw MapHttpError(response.StatusCode, body);

            return ParseResponse(body);
        }
    }

    private string BuildRequestJson(LlmChatRequest request)
    {
        var messages = request.Messages.Select(ToWireMessage).ToArray();

        object[]? tools = request.Tools.Count == 0
            ? null
            : [.. request.Tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = ParseSchema(t.ParametersJsonSchema)
                }
            })];

        var payload = new
        {
            model = request.Model ?? _model,
            messages,
            tools,
            tool_choice = tools is null ? null : "auto",
            temperature = request.Temperature > 0 ? request.Temperature : _temperature,
            max_tokens = request.MaxTokens
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static object ToWireMessage(LlmMessage message)
    {
        return message.Role switch
        {
            LlmRole.Assistant when message.ToolCalls is { Count: > 0 } => new
            {
                role = "assistant",
                content = message.Content,
                tool_calls = message.ToolCalls.Select(c => new
                {
                    id = c.Id,
                    type = "function",
                    function = new { name = c.Name, arguments = c.ArgumentsJson }
                }).ToArray()
            },

            LlmRole.Tool => new
            {
                role = "tool",
                tool_call_id = message.ToolCallId,
                name = message.Name,
                content = message.Content ?? string.Empty
            },

            LlmRole.System => (object)new { role = "system", content = message.Content ?? string.Empty },
            LlmRole.User => new { role = "user", content = message.Content ?? string.Empty },
            _ => new { role = "assistant", content = message.Content ?? string.Empty }
        };
    }

    private static JsonElement ParseSchema(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            return JsonDocument.Parse("""{ "type": "object", "properties": {} }""").RootElement.Clone();

        try
        {
            using JsonDocument document = JsonDocument.Parse(schema);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // schema 写错时退回空对象，避免整次调用失败（清单会暴露问题）
            return JsonDocument.Parse("""{ "type": "object", "properties": {} }""").RootElement.Clone();
        }
    }

    private static LlmChatResponse ParseResponse(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new LlmException(LlmErrorCodes.Protocol, $"响应不是合法 JSON：{ex.Message}", ex);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                throw new LlmException(LlmErrorCodes.Protocol, "响应缺少 choices。");
            }

            JsonElement choice = choices[0];
            string? finishReason = choice.TryGetProperty("finish_reason", out JsonElement fr)
                && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;

            if (!choice.TryGetProperty("message", out JsonElement messageElement))
                throw new LlmException(LlmErrorCodes.Protocol, "响应缺少 message。");

            string? text = messageElement.TryGetProperty("content", out JsonElement contentElement)
                && contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString()
                : null;

            var toolCalls = new List<LlmToolCall>();
            if (messageElement.TryGetProperty("tool_calls", out JsonElement calls)
                && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement call in calls.EnumerateArray())
                {
                    if (!call.TryGetProperty("function", out JsonElement function)) continue;

                    string id = call.TryGetProperty("id", out JsonElement idElement)
                        ? idElement.GetString() ?? string.Empty
                        : string.Empty;

                    string name = function.TryGetProperty("name", out JsonElement nameElement)
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;

                    string arguments = function.TryGetProperty("arguments", out JsonElement argsElement)
                        ? argsElement.GetString() ?? "{}"
                        : "{}";

                    toolCalls.Add(new LlmToolCall(id, name, arguments));
                }
            }

            LlmUsage? usage = null;
            if (root.TryGetProperty("usage", out JsonElement usageElement)
                && usageElement.ValueKind == JsonValueKind.Object)
            {
                usage = new LlmUsage(
                    GetInt(usageElement, "prompt_tokens"),
                    GetInt(usageElement, "completion_tokens"));
            }

            string? requestId = root.TryGetProperty("id", out JsonElement idProp)
                && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null;

            return new LlmChatResponse(
                LlmMessage.Assistant(text, toolCalls.Count == 0 ? null : toolCalls),
                usage,
                finishReason,
                requestId);
        }
    }

    private static int GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int result)
            ? result
            : 0;

    private static LlmException MapHttpError(HttpStatusCode status, string body)
    {
        string detail = body.Length > 500 ? body[..500] : body;

        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new LlmException(LlmErrorCodes.Unauthorized, $"鉴权失败（{(int)status}）。请检查 API Key。"),

            HttpStatusCode.TooManyRequests =>
                new LlmException(LlmErrorCodes.RateLimited, "被限流（429）。请稍后重试。"),

            _ => new LlmException(LlmErrorCodes.Protocol,
                $"服务返回 {(int)status}：{detail}")
        };
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// 未配置时的占位提供方：任何调用都抛"未配置"，让上层给出可操作的提示，
/// 而不是在 DI 装配阶段就崩掉。
/// </summary>
public sealed class NotConfiguredLlmProvider : ILlmProvider
{
    public string Name => "未配置";

    public Task<LlmChatResponse> ChatAsync(LlmChatRequest request, CancellationToken cancellationToken) =>
        throw new LlmException(
            LlmErrorCodes.NotConfigured,
            "尚未配置模型 API。请设置 baseUrl、model 与 apiKey 后再使用 AI 功能。");
}
