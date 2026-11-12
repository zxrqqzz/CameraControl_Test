using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CameraControl.Infrastructure.Ai;

/// <summary>一个已知的模型服务商。</summary>
/// <param name="Name">显示名。</param>
/// <param name="BaseUrl">已归一化（含版本前缀）。</param>
/// <param name="KeyHints">
/// key 前缀提示。只用于**排序**（先试更像的），不用于判断——
/// 大量服务都用 <c>sk-</c> 开头，靠前缀猜必然猜错。
/// </param>
public sealed record ProviderCandidate(string Name, string BaseUrl, IReadOnlyList<string> KeyHints);

/// <summary>识别结果。</summary>
/// <param name="Provider">命中的服务商。</param>
/// <param name="Models">该 key 实际可用的模型。</param>
/// <param name="TriedProviders">一共试了几家（诊断用）。</param>
public sealed record ProviderDetection(
    ProviderCandidate Provider,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> TriedProviders);

/// <summary>
/// 按 key 自动识别模型服务商。
///
/// 做法：**逐个探测**已知服务商的 <c>/models</c>，第一个返回 200 的就是它。
/// 为什么不按 key 前缀猜：DeepSeek / OpenAI / Moonshot / 通义 / 硅基流动
/// 都用 <c>sk-</c> 开头，前缀根本不具区分度，猜错只会让人以为 key 无效。
///
/// 探测顺序按前缀相似度排（<c>sk-or-</c> 先试 OpenRouter、<c>gsk_</c> 先试 Groq），
/// 命中即停，正常情况 1~2 次请求就能确定。
/// </summary>
public sealed class ProviderCatalog : IDisposable
{
    /// <summary>已知服务商（顺序即默认探测顺序）。</summary>
    public static readonly IReadOnlyList<ProviderCandidate> Known =
    [
        new("DeepSeek", "https://api.deepseek.com/v1", ["sk-"]),
        new("OpenAI", "https://api.openai.com/v1", ["sk-proj-", "sk-"]),
        new("Moonshot（Kimi）", "https://api.moonshot.cn/v1", ["sk-"]),
        new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", []),
        new("通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", ["sk-"]),
        new("硅基流动", "https://api.siliconflow.cn/v1", ["sk-"]),
        new("火山方舟", "https://ark.cn-beijing.volces.com/api/v3", []),
        new("Groq", "https://api.groq.com/openai/v1", ["gsk_"]),
        new("OpenRouter", "https://openrouter.ai/api/v1", ["sk-or-"]),
        new("本机 Ollama", "http://localhost:11434/v1", [])
    ];

    private readonly HttpClient _http;

    public ProviderCatalog()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>
    /// 用 key 逐个探测服务商。全部失败返回 null（调用方应退回手填 baseUrl）。
    /// </summary>
    public async Task<ProviderDetection?> DetectAsync(
        string apiKey,
        CancellationToken cancellationToken,
        string? preferredBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        var tried = new List<string>();

        foreach (ProviderCandidate provider in Order(apiKey, preferredBaseUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();
            tried.Add(provider.Name);

            List<string>? models = await TryListModelsAsync(provider.BaseUrl, apiKey, cancellationToken)
                .ConfigureAwait(false);

            if (models is { Count: > 0 })
                return new ProviderDetection(provider, models, tried);
        }

        return null;
    }

    /// <summary>按 key 前缀与手工 baseUrl 调整探测顺序。</summary>
    private static IEnumerable<ProviderCandidate> Order(string apiKey, string? preferredBaseUrl)
    {
        string key = apiKey.Trim();

        return Known
            .OrderByDescending(p => preferredBaseUrl is not null
                                    && string.Equals(p.BaseUrl, preferredBaseUrl, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.KeyHints.Any(h => key.StartsWith(h, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(p => p.KeyHints.Count);
    }

    /// <summary>取某服务商的模型列表；失败返回 null（不抛，用于探测）。</summary>
    private async Task<List<string>?> TryListModelsAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");

            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using HttpResponseMessage response =
                await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK) return null;

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var models = new List<string>();

            foreach (JsonElement item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out JsonElement id) && id.GetString() is { Length: > 0 } name)
                    models.Add(name);
            }

            models.Sort(StringComparer.OrdinalIgnoreCase);

            return models;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
