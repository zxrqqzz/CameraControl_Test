using CameraControl.Application.Cua;
using CameraControl.Infrastructure.Ai;
using Xunit;

namespace CameraControl.Infrastructure.Tests;

/// <summary>
/// 用量解析。
///
/// 各家字段不统一：
/// - DeepSeek：<c>prompt_cache_hit_tokens</c>
/// - OpenAI：<c>prompt_tokens_details.cached_tokens</c>
/// 解析错了就会把"缓存命中"显示成 0，用户会以为缓存没生效。
/// </summary>
public sealed class LlmUsageTests
{
    private const string DeepSeekResponse = """
    {
      "id": "req-1",
      "choices": [{ "finish_reason": "stop", "message": { "content": "OK" } }],
      "usage": {
        "prompt_tokens": 2288,
        "completion_tokens": 3045,
        "total_tokens": 5333,
        "prompt_cache_hit_tokens": 2288,
        "prompt_cache_miss_tokens": 0
      }
    }
    """;

    private const string OpenAiResponse = """
    {
      "id": "req-2",
      "choices": [{ "finish_reason": "stop", "message": { "content": "OK" } }],
      "usage": {
        "prompt_tokens": 1000,
        "completion_tokens": 50,
        "total_tokens": 1050,
        "prompt_tokens_details": { "cached_tokens": 768 }
      }
    }
    """;

    private const string NoUsageResponse = """
    {
      "id": "req-3",
      "choices": [{ "finish_reason": "stop", "message": { "content": "OK" } }]
    }
    """;

    [Fact]
    public void 应解析DeepSeek的缓存命中字段()
    {
        LlmUsage usage = OpenAiCompatibleProvider.ParseResponse(DeepSeekResponse).Usage!;

        Assert.Equal(2288, usage.PromptTokens);
        Assert.Equal(3045, usage.CompletionTokens);
        Assert.Equal(2288, usage.CachedTokens);
        Assert.Equal(0, usage.UncachedInputTokens);
        Assert.Equal(1.0, usage.CacheHitRate, 3);
        Assert.Equal(5333, usage.TotalTokens);
    }

    [Fact]
    public void 应解析OpenAI的嵌套缓存字段()
    {
        LlmUsage usage = OpenAiCompatibleProvider.ParseResponse(OpenAiResponse).Usage!;

        Assert.Equal(1000, usage.PromptTokens);
        Assert.Equal(768, usage.CachedTokens);
        Assert.Equal(232, usage.UncachedInputTokens);
        Assert.Equal(0.768, usage.CacheHitRate, 3);
    }

    [Fact]
    public void 没有usage字段时应为null而不是伪造零值()
    {
        Assert.Null(OpenAiCompatibleProvider.ParseResponse(NoUsageResponse).Usage);
    }

    [Fact]
    public void 累加应交缓存与输出分别相加()
    {
        var first = new LlmUsage(1000, 100, CachedTokens: 800);
        var second = new LlmUsage(500, 50, CachedTokens: 400);

        LlmUsage total = first.Add(second);

        Assert.Equal(1500, total.PromptTokens);
        Assert.Equal(150, total.CompletionTokens);
        Assert.Equal(1200, total.CachedTokens);
        Assert.Equal(300, total.UncachedInputTokens);
        Assert.Equal(1650, total.TotalTokens);
    }

    [Fact]
    public void Add对null参数应安全()
    {
        var usage = new LlmUsage(10, 5);

        // 累加时"本轮没有 usage"是常见情况（服务端未返回），不能因此抛异常
        Assert.Same(usage, usage.Add(null));
        Assert.Equal(10, usage.Add(null).PromptTokens);
    }
}
