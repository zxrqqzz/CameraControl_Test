using System.Text.Json;
using CameraControl.Application.Cua;
using CameraControl.Infrastructure.Ai;
using Xunit;

namespace CameraControl.Infrastructure.Tests;

/// <summary>
/// 思考强度 → 请求体。
///
/// 依据 DeepSeek 官方文档（thinking_mode）：
/// - 强度用 <c>reasoning_effort = low/high/max</c>
/// - 关闭思考用 <c>thinking.type = disabled</c>
/// - **没有 medium**（会被映射成 high）
///
/// 这些断言存在的理由：曾经"加了参数"但只在签名里加了默认值、
/// 请求体根本没带上——编译通过、测试全绿、功能是空的。
/// </summary>
public sealed class LlmRequestPayloadTests
{
    private static LlmChatRequest SimpleRequest() => new(
        [LlmMessage.User("hi")],
        [],
        Model: null,
        Temperature: 0,
        MaxTokens: 16);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static OpenAiCompatibleProvider Provider(string model, ReasoningOptions reasoning) =>
        new("https://api.deepseek.com/v1", "k", model, 0.1, 30, reasoning);

    // ── 强度映射 ────────────────────────────────────────

    [Theory]
    [InlineData(LlmStrength.Low, "low")]
    [InlineData(LlmStrength.High, "high")]
    [InlineData(LlmStrength.Max, "max")]
    public void DeepSeek各档应映射为low_high_max(LlmStrength strength, string expected)
    {
        ReasoningOptions options = ModelCatalogService.BuildReasoning("deepseek-chat", strength);

        Assert.Equal(expected, options.Effort);
        Assert.False(options.DisableThinking);
    }

    [Fact]
    public void DeepSeek选Off应关闭思考而不是传none()
    {
        ReasoningOptions options = ModelCatalogService.BuildReasoning("deepseek-chat", LlmStrength.Off);

        Assert.True(options.DisableThinking);
        Assert.Null(options.Effort);
    }

    [Fact]
    public void DeepSeek系列都应支持强度()
    {
        Assert.True(ModelCatalogService.SupportsReasoningEffort("deepseek-chat"));
        Assert.True(ModelCatalogService.SupportsReasoningEffort("deepseek-reasoner"));
        Assert.True(ModelCatalogService.SupportsReasoningEffort("DeepSeek-V4.1-Flash"));
    }

    [Fact]
    public void OpenAI推理模型没有max档应降级为high()
    {
        ReasoningOptions options = ModelCatalogService.BuildReasoning("o3-mini", LlmStrength.Max);

        Assert.Equal("high", options.Effort);
    }

    [Fact]
    public void 不支持的模型不产生任何参数()
    {
        ReasoningOptions options = ModelCatalogService.BuildReasoning("gpt-4o", LlmStrength.High);

        Assert.False(options.HasEffect);
    }

    // ── 请求体 ──────────────────────────────────────────

    [Fact]
    public void DeepSeek高强度应写入reasoning_effort()
    {
        using OpenAiCompatibleProvider provider =
            Provider("deepseek-chat", ModelCatalogService.BuildReasoning("deepseek-chat", LlmStrength.High));

        JsonElement root = Parse(provider.BuildRequestJson(SimpleRequest()));

        Assert.True(root.TryGetProperty("reasoning_effort", out JsonElement effort));
        Assert.Equal("high", effort.GetString());

        // 开启思考时不应出现 thinking 字段
        Assert.False(root.TryGetProperty("thinking", out _));
    }

    [Fact]
    public void DeepSeek关闭思考应写入thinking_disabled()
    {
        using OpenAiCompatibleProvider provider =
            Provider("deepseek-chat", ModelCatalogService.BuildReasoning("deepseek-chat", LlmStrength.Off));

        JsonElement root = Parse(provider.BuildRequestJson(SimpleRequest()));

        Assert.True(root.TryGetProperty("thinking", out JsonElement thinking));
        Assert.Equal("disabled", thinking.GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void 不支持的模型请求体里两个字段都不该出现()
    {
        using OpenAiCompatibleProvider provider =
            Provider("gpt-4o", ModelCatalogService.BuildReasoning("gpt-4o", LlmStrength.High));

        string json = provider.BuildRequestJson(SimpleRequest());

        Assert.DoesNotContain("reasoning_effort", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"thinking\"", json, StringComparison.Ordinal);
    }

    // ── 思维链回传（带 tools 时是硬要求）────────────────

    [Fact]
    public void 助手消息的reasoning_content应原样回传()
    {
        using OpenAiCompatibleProvider provider =
            Provider("deepseek-chat", ModelCatalogService.BuildReasoning("deepseek-chat", LlmStrength.High));

        var request = new LlmChatRequest(
            [
                LlmMessage.User("hi"),
                new LlmMessage(
                    LlmRole.Assistant,
                    Content: null,
                    ToolCalls: [new LlmToolCall("call_1", "device_list", "{}")],
                    ReasoningContent: "先看看有哪些设备")
            ],
            [new LlmToolDefinition("device_list", "列出设备", """{"type":"object"}""")],
            Model: null,
            Temperature: 0,
            MaxTokens: null);

        JsonElement root = Parse(provider.BuildRequestJson(request));

        JsonElement assistant = root.GetProperty("messages")[1];

        // DeepSeek 要求带 tools 时历史 reasoning_content 必须完整回传，否则 400
        Assert.Equal("先看看有哪些设备", assistant.GetProperty("reasoning_content").GetString());
    }
}
