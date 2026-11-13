using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CameraControl.Application.Cua;

namespace CameraControl.Infrastructure.Ai;

/// <summary>
/// 流式（SSE）实现。
///
/// 为什么值得单独做：非流式下用户点完「运行」只能干等，
/// 而 DeepSeek 的思考内容要几十秒——期间没有任何反馈，体验像卡死。
/// 流式把思考与正文逐段吐出来，等待变成"看着它想"。
/// </summary>
public sealed partial class OpenAiCompatibleProvider
{
    /// <summary>流式对话：逐段返回思考与正文，最后发出一个 Completed 事件。</summary>
    public async IAsyncEnumerable<LlmStreamEvent> ChatStreamAsync(
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string payload = BuildRequestJson(request, stream: true);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_baseUrl}/{ChatCompletionsPath}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using HttpResponseMessage httpResponse = await _http
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
        {
            string errorBody = await httpResponse.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            throw new LlmException(
                httpResponse.StatusCode == HttpStatusCode.Unauthorized
                    ? LlmErrorCodes.Unauthorized
                    : LlmErrorCodes.Protocol,
                DescribeHttpFailure(httpResponse.StatusCode, errorBody));
        }

        await using Stream stream = await httpResponse.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using var reader = new StreamReader(stream, Encoding.UTF8);

        var content = new StringBuilder();
        var reasoning = new StringBuilder();

        var toolCalls = new SortedDictionary<int, ToolCallBuilder>();

        LlmUsage? usage = null;
        string? finishReason = null;
        string? requestId = null;

        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null) break;

            if (line.Length == 0) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            string data = line[5..].Trim();

            if (data == "[DONE]") break;

            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(data);
            }
            catch (JsonException)
            {
                // 心跳或非法片段，跳过而不是中断整条流
                continue;
            }

            using (document)
            {
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("id", out JsonElement idElement)
                    && idElement.ValueKind == JsonValueKind.String)
                {
                    requestId ??= idElement.GetString();
                }

                if (root.TryGetProperty("usage", out JsonElement usageElement)
                    && usageElement.ValueKind == JsonValueKind.Object)
                {
                    usage = new LlmUsage(
                        GetInt(usageElement, "prompt_tokens"),
                        GetInt(usageElement, "completion_tokens"),
                        CachedTokens: ReadCachedTokens(usageElement));
                }

                if (!root.TryGetProperty("choices", out JsonElement choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                {
                    continue;
                }

                JsonElement choice = choices[0];

                if (choice.TryGetProperty("finish_reason", out JsonElement finishElement)
                    && finishElement.ValueKind == JsonValueKind.String)
                {
                    finishReason = finishElement.GetString();
                }

                if (!choice.TryGetProperty("delta", out JsonElement delta)
                    || delta.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // 思考增量（DeepSeek）
                if (delta.TryGetProperty("reasoning_content", out JsonElement reasonElement)
                    && reasonElement.ValueKind == JsonValueKind.String)
                {
                    string? text = reasonElement.GetString();

                    if (!string.IsNullOrEmpty(text))
                    {
                        reasoning.Append(text);
                        yield return new LlmStreamEvent(LlmStreamKind.Reasoning, text);
                    }
                }

                // 正文增量
                if (delta.TryGetProperty("content", out JsonElement contentElement)
                    && contentElement.ValueKind == JsonValueKind.String)
                {
                    string? text = contentElement.GetString();

                    if (!string.IsNullOrEmpty(text))
                    {
                        content.Append(text);
                        yield return new LlmStreamEvent(LlmStreamKind.Content, text);
                    }
                }

                // 工具调用是分片下发的：按 index 聚合 id / name / arguments
                if (delta.TryGetProperty("tool_calls", out JsonElement calls)
                    && calls.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement call in calls.EnumerateArray())
                    {
                        int index = GetInt(call, "index");

                        if (!toolCalls.TryGetValue(index, out ToolCallBuilder? builder))
                        {
                            builder = new ToolCallBuilder();
                            toolCalls[index] = builder;
                        }

                        if (call.TryGetProperty("id", out JsonElement callId)
                            && callId.ValueKind == JsonValueKind.String)
                        {
                            builder.Id = callId.GetString();
                        }

                        if (call.TryGetProperty("function", out JsonElement function)
                            && function.ValueKind == JsonValueKind.Object)
                        {
                            if (function.TryGetProperty("name", out JsonElement nameElement)
                                && nameElement.ValueKind == JsonValueKind.String)
                            {
                                builder.Name = nameElement.GetString();
                            }

                            if (function.TryGetProperty("arguments", out JsonElement argsElement)
                                && argsElement.ValueKind == JsonValueKind.String)
                            {
                                builder.Arguments.Append(argsElement.GetString());
                            }
                        }
                    }
                }
            }
        }

        var assembled = new List<LlmToolCall>();

        foreach ((int index, ToolCallBuilder builder) in toolCalls)
        {
            assembled.Add(new LlmToolCall(
                builder.Id ?? $"call_{index}",
                builder.Name ?? string.Empty,
                builder.Arguments.Length == 0 ? "{}" : builder.Arguments.ToString()));
        }

        yield return new LlmStreamEvent(
            LlmStreamKind.Completed,
            Response: new LlmChatResponse(
                LlmMessage.Assistant(
                    content.Length == 0 ? null : content.ToString(),
                    assembled.Count == 0 ? null : assembled,
                    reasoning.Length == 0 ? null : reasoning.ToString()),
                usage,
                finishReason,
                requestId));
    }

    private static string DescribeHttpFailure(HttpStatusCode status, string body)
    {
        string snippet = body.Length <= 300 ? body : body[..300] + "…";

        return status == HttpStatusCode.Unauthorized
            ? $"流式请求被拒绝（401）：密钥无效或与地址不匹配。{snippet}"
            : $"流式请求失败（{(int)status}）：{snippet}";
    }

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}
