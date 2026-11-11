using CameraControl.Application.Cua;
using CameraControl.Application.Diagnostics;

namespace CameraControl.Infrastructure.Ai;

/// <summary>
/// 按"当前生效的配置"动态路由到具体提供方。
///
/// 为什么需要：配置可以切换（多套并存），如果启动时把提供方实例固定住，
/// 切换配置后仍然会用旧的那一套——用户会以为"切换没生效"。
/// 这里每次调用前比对配置签名，变了就重建。
///
/// 密钥每次从 DPAPI 解密，不缓存在字段里（降低驻留时间）。
/// </summary>
public sealed class RoutingLlmProvider : ILlmProvider, IDisposable
{
    private readonly LlmSettingsStore _store;
    private readonly IAppLogger? _logger;
    private readonly object _sync = new();

    private OpenAiCompatibleProvider? _current;
    private string? _signature;

    public RoutingLlmProvider(LlmSettingsStore store, IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _logger = logger;
    }

    public string Name => "按当前配置路由";

    public Task<LlmChatResponse> ChatAsync(LlmChatRequest request, CancellationToken cancellationToken) =>
        Resolve().ChatAsync(request, cancellationToken);

    /// <summary>取（必要时重建）当前配置对应的提供方。</summary>
    public OpenAiCompatibleProvider Resolve()
    {
        lock (_sync)
        {
            LlmProfile? profile = _store.ActiveProfile()
                ?? throw new LlmException(
                    LlmErrorCodes.NotConfigured,
                    "还没有任何模型配置。请在「AI」页新增一条配置（baseUrl + 模型 + key）。");

            string? apiKey = _store.GetApiKey(profile.Id);

            if (apiKey is null)
            {
                throw new LlmException(
                    LlmErrorCodes.NotConfigured,
                    profile.HasKey
                        ? "当前配置的密钥无法解密（可能换了 Windows 账户或机器）。请删除该配置后重新新增。"
                        : "当前配置还没有保存密钥。请删除后重新新增，并在新增时填入 key。");
            }

            if (string.IsNullOrWhiteSpace(profile.Model))
            {
                throw new LlmException(
                    LlmErrorCodes.NotConfigured,
                    "当前配置还没有选择模型。");
            }

            string signature = string.Join(
                '|',
                profile.Id,
                profile.BaseUrl,
                profile.Model,
                profile.Strength.ToString(),
                profile.TimeoutSeconds.ToString(),
                profile.Temperature.ToString("F2"));

            if (_current is not null && signature == _signature) return _current;

            _current?.Dispose();

            // 只有模型明确支持 reasoning_effort 才传，否则留空避免 400
            string? effort = ModelCatalogService.SupportsReasoningEffort(profile.Model)
                ? ModelCatalogService.ToReasoningEffort(profile.Strength)
                : null;

            _current = new OpenAiCompatibleProvider(
                profile.BaseUrl,
                apiKey,
                profile.Model,
                profile.Temperature,
                profile.TimeoutSeconds,
                effort,
                _logger);

            _signature = signature;

            _logger?.Info("模型提供方已按当前配置重建", new Dictionary<string, object?>
            {
                ["profile"] = profile.Name,
                ["baseUrl"] = profile.BaseUrl,
                ["model"] = profile.Model,
                ["strength"] = profile.Strength.ToString(),
                ["reasoningEffort"] = effort
            });

            return _current;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _current?.Dispose();
            _current = null;
            _signature = null;
        }
    }
}
