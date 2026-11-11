using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CameraControl.Infrastructure.Ai;

/// <summary>模型能力档位（"强度"的模型维度）。</summary>
public enum ModelTier
{
    /// <summary>快：小模型，响应快、成本低。</summary>
    Fast = 0,

    /// <summary>均衡：默认选择。</summary>
    Balanced = 1,

    /// <summary>强：大模型或推理模型，慢但更可靠。</summary>
    Strong = 2
}

/// <summary>模型清单来源。</summary>
public enum ModelCatalogSource
{
    /// <summary>从服务端 /models 实时拉取。</summary>
    Remote = 0,

    /// <summary>内置常见清单（服务端不支持 /models 时兜底）。</summary>
    BuiltIn = 1
}

/// <summary>
/// 一个可选模型。
/// </summary>
/// <param name="Id">模型 id（请求里使用的名字）。</param>
/// <param name="Tier">能力档位。</param>
/// <param name="SupportsReasoningEffort">
/// 是否支持 <c>reasoning_effort</c> 参数。
/// 很多"推理模型"（如 deepseek-reasoner）**自身就在推理，但不接受这个参数**，
/// 传了会 400，所以必须与"是不是推理模型"分开表达。
/// </param>
/// <param name="Note">给用户看的说明。</param>
public sealed record ModelEntry(
    string Id,
    ModelTier Tier,
    bool SupportsReasoningEffort,
    string? Note = null);

/// <summary>模型清单结果。</summary>
/// <param name="Models">模型列表（按档位再按名字排序）。</param>
/// <param name="Source">来源。</param>
/// <param name="Error">拉取失败原因；成功为 null。</param>
public sealed record ModelCatalogResult(
    IReadOnlyList<ModelEntry> Models,
    ModelCatalogSource Source,
    string? Error = null)
{
    /// <summary>是否用了兜底清单。</summary>
    public bool IsFallback => Source == ModelCatalogSource.BuiltIn;
}

/// <summary>
/// 模型目录：优先请求 <c>{baseUrl}/models</c>，失败则回退到内置清单。
///
/// 为什么需要兜底：不少"OpenAI 兼容"的服务根本没有 /models 接口，
/// 拉不到就直接报错会把这类服务完全堵死。
/// </summary>
public sealed class ModelCatalogService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>内置常见模型（拉不到 /models 时兜底）。</summary>
    private static readonly ModelEntry[] BuiltInModels =
    [
        new("deepseek-chat", ModelTier.Balanced, false, "DeepSeek 通用对话"),
        new("deepseek-reasoner", ModelTier.Strong, false, "DeepSeek 推理模型（自身推理，不接受 reasoning_effort）"),

        new("gpt-4o-mini", ModelTier.Fast, false, "OpenAI 小模型"),
        new("gpt-4o", ModelTier.Balanced, false, "OpenAI 通用"),
        new("o3-mini", ModelTier.Strong, true, "OpenAI 推理模型，支持 reasoning_effort"),
        new("o1", ModelTier.Strong, true, "OpenAI 推理模型，支持 reasoning_effort"),

        new("qwen-turbo", ModelTier.Fast, false, "通义千问 快"),
        new("qwen-plus", ModelTier.Balanced, false, "通义千问 均衡"),
        new("qwen-max", ModelTier.Strong, false, "通义千问 强"),

        new("glm-4-flash", ModelTier.Fast, false, "智谱 快"),
        new("glm-4-plus", ModelTier.Balanced, false, "智谱 均衡"),

        new("moonshot-v1-8k", ModelTier.Fast, false, "Kimi 短上下文"),
        new("moonshot-v1-32k", ModelTier.Balanced, false, "Kimi 中上下文"),

        new("llama3.1:8b", ModelTier.Fast, false, "本地 Ollama 小模型"),
        new("llama3.1:70b", ModelTier.Strong, false, "本地 Ollama 大模型")
    ];

    private readonly HttpClient _http;

    public ModelCatalogService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// 拉取模型清单。baseUrl 应已归一化（含 /v1）。
    /// </summary>
    public async Task<ModelCatalogResult> FetchAsync(
        string baseUrl,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return BuiltIn("baseUrl 为空，使用内置清单。");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");

            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using HttpResponseMessage response =
                await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                bool keySent = !string.IsNullOrWhiteSpace(apiKey);

                string hint = response.StatusCode switch
                {
                    // 401 有两种完全不同的原因，混在一起会误导人去反复检查 key
                    HttpStatusCode.Unauthorized => keySent
                        ? "密钥被拒绝（401）——key 无效，或与该 baseurl 不是同一家服务"
                        : "本次请求没有携带密钥（401）——请先填入密钥再拉取",
                    HttpStatusCode.NotFound => "该服务没有 /models 接口（404）",
                    HttpStatusCode.Forbidden => "无权访问（403）",
                    _ => $"HTTP {(int)response.StatusCode}"
                };

                return BuiltIn($"拉取失败：{hint}，使用内置清单。");
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            List<ModelEntry>? models = ParseModels(body);

            if (models is null || models.Count == 0)
                return BuiltIn("服务返回的模型列表为空，使用内置清单。");

            return new ModelCatalogResult(Sort(models), ModelCatalogSource.Remote);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BuiltIn($"拉取失败：{ex.Message}，使用内置清单。");
        }
    }

    /// <summary>内置清单（不需要网络）。</summary>
    public ModelCatalogResult BuiltInModelsOnly(string? reason = null) =>
        new(Sort([.. BuiltInModels]), ModelCatalogSource.BuiltIn, reason);

    /// <summary>按模型名判断能力档位（用于服务端返回任意模型时的分类）。</summary>
    public static ModelTier Classify(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        string id = modelId.ToLowerInvariant();

        // 先看内置表里的精确条目
        foreach (ModelEntry entry in BuiltInModels)
        {
            if (string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase)) return entry.Tier;
        }

        // 强：推理模型 / 大参数
        string[] strong =
        [
            "reasoner", "o1", "o3", "o4", "gpt-5", "opus",
            "70b", "72b", "110b", "405b", "max", "ultra", "pro", "large"
        ];

        if (strong.Any(k => id.Contains(k, StringComparison.Ordinal))) return ModelTier.Strong;

        // 快：小模型 / 轻量档
        string[] fast =
        [
            "mini", "flash", "lite", "small", "nano", "turbo", "haiku",
            "1.5b", "3b", "7b", "8b", "9b", "13b"
        ];

        if (fast.Any(k => id.Contains(k, StringComparison.Ordinal))) return ModelTier.Fast;

        return ModelTier.Balanced;
    }

    /// <summary>
    /// 是否接受 <c>reasoning_effort</c> 参数。
    ///
    /// 只有明确支持的服务才传：传错参数会 400，比"力度没生效"更糟。
    /// </summary>
    public static bool SupportsReasoningEffort(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        string id = modelId.ToLowerInvariant();

        return id.StartsWith("o1", StringComparison.Ordinal)
               || id.StartsWith("o3", StringComparison.Ordinal)
               || id.StartsWith("o4", StringComparison.Ordinal)
               || id.StartsWith("gpt-5", StringComparison.Ordinal)
               || id.Contains("-reasoning", StringComparison.Ordinal);
    }

    /// <summary>推理力度 → 服务端参数值。</summary>
    public static string ToReasoningEffort(LlmStrength strength) => strength switch
    {
        LlmStrength.Low => "low",
        LlmStrength.High => "high",
        _ => "medium"
    };

    public void Dispose() => _http.Dispose();

    // ── 私有 ────────────────────────────────────────────────

    private ModelCatalogResult BuiltIn(string reason) =>
        new(Sort([.. BuiltInModels]), ModelCatalogSource.BuiltIn, reason);

    private static List<ModelEntry>? ParseModels(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var models = new List<ModelEntry>();

            foreach (JsonElement item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out JsonElement idElement)) continue;

                string? id = idElement.GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;

                models.Add(new ModelEntry(
                    id,
                    Classify(id),
                    SupportsReasoningEffort(id)));
            }

            return models;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<ModelEntry> Sort(List<ModelEntry> models) =>
        [.. models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Tier)
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)];
}
