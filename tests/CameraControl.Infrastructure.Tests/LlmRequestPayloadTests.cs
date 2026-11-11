using System.Text.Json;
using CameraControl.Application.Cua;
using CameraControl.Infrastructure.Ai;
using Xunit;

namespace CameraControl.Infrastructure.Tests;

/// <summary>
/// 请求体构造。
///
/// 存在的理由：曾经"加了 reasoning_effort 参数"但只在构造签名里加了默认值、
/// 请求体根本没带上——编译通过、测试全绿、功能却是空的。
/// 这类"改了但没生效"只能靠断言**最终请求体**来抓。
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

    [Fact]
    public void 支持推理力度时应写入reasoning_effort()
    {
        using var provider = new OpenAiCompatibleProvider(
            "https://example.com/v1", "k", "o3-mini", 0.1, 30, reasoningEffort: "high");

        JsonElement root = Parse(provider.BuildRequestJson(SimpleRequest()));

        Assert.True(root.TryGetProperty("reasoning_effort", out JsonElement effort));
        Assert.Equal("high", effort.GetString());
    }

    [Fact]
    public void 不支持时应完全不出现该字段()
    {
        using var provider = new OpenAiCompatibleProvider(
            "https://example.com/v1", "k", "deepseek-chat", 0.1, 30, reasoningEffort: null);

        string json = provider.BuildRequestJson(SimpleRequest());

        // 传 null 必须省略字段，而不是发 "reasoning_effort": null（部分服务会因此 400）
        Assert.DoesNotContain("reasoning_effort", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 请求体应带上配置的模型()
    {
        using var provider = new OpenAiCompatibleProvider(
            "https://example.com/v1", "k", "deepseek-chat", 0.1, 30);

        JsonElement root = Parse(provider.BuildRequestJson(SimpleRequest()));

        Assert.Equal("deepseek-chat", root.GetProperty("model").GetString());
    }
}
