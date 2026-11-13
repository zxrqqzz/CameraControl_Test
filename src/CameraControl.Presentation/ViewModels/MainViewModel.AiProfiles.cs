using CameraControl.Application.Commands;
using CameraControl.Application.Cua;
using CameraControl.Infrastructure.Ai;
using CameraControl.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CameraControl.Presentation.ViewModels;

/// <summary>AI 档案块：识别新增、手动新增、连通性测试、用量统计。</summary>
public sealed partial class MainViewModel
{

    /// <summary>把当前配置的模型清单与强度填进"使用"区（每个配置各记一份）。</summary>
    private void LoadCurrentSelection(LlmProfile? profile)
    {
        AiCurrentModelOptions.Clear();

        if (profile is null)
        {
            AiCurrentModel = string.Empty;
            AiCatalogSource = string.Empty;
            RefreshStrengthState(null);
            return;
        }

        foreach (string model in profile.Models) AiCurrentModelOptions.Add(model);

        AiCurrentModel = profile.Model;
        AiStrengthIndex = (int)profile.Strength;

        AiCatalogSource = profile.Models.Count == 0
            ? $"模型清单未缓存，可点「刷新模型」（服务：{profile.Name}）。"
            : $"{profile.Name}：可选模型 {profile.Models.Count} 个。";

        RefreshStrengthState(profile.Model);
    }

    /// <summary>
    /// 识别并新增：只给密钥。
    /// 先用 key 前缀挑最像的一家，不中再逐家探测；全不中才显示手填区域。
    /// </summary>
    [RelayCommand]
    private async Task DetectAndAddProfileAsync()
    {
        await RunGuardedAsync("识别服务商", async () =>
        {
            string? key = _pendingApiKey;

            if (string.IsNullOrWhiteSpace(key))
            {
                AiDetectStatus = "请先填入密钥。";
                return;
            }

            AiDetectStatus = "正在识别是哪家服务（先按前缀猜，不中再逐家探测）…";

            ProviderDetection? detection = await _providerCatalog
                .DetectAsync(key, CancellationToken.None)
                .ConfigureAwait(true);

            if (detection is null)
            {
                AiShowManualBaseUrl = true;
                AiDetectStatus =
                    "没能自动识别（可能是不在已知列表里的服务，或网络不通）。" +
                    "请在下方手填 baseUrl 后点「用手填信息新增」。";
                return;
            }

            LlmProfile profile = _llmSettings.AddProfile(
                detection.Provider.Name,
                detection.Provider.BaseUrl,
                detection.Models[0],
                key,
                LlmStrength.High,
                detection.Models);

            _pendingApiKey = null;
            RaiseApiKeyAccepted();
            AiShowManualBaseUrl = false;

            RefreshAiProfiles();

            AiDetectStatus =
                $"已识别为「{detection.Provider.Name}」：{detection.Provider.BaseUrl}，" +
                $"可用模型 {detection.Models.Count} 个（试了 {detection.TriedProviders.Count} 家）。已自动设为当前配置。";
            Status = AiDetectStatus;
        }).ConfigureAwait(true);
    }

    /// <summary>识别失败后的兜底：手动指定 baseUrl。</summary>
    [RelayCommand]
    private async Task AddManualProfileAsync()
    {
        await RunGuardedAsync("新增配置", async () =>
        {
            if (string.IsNullOrWhiteSpace(AiManualBaseUrl))
            {
                AiManualStatus = "请填写 baseUrl。";
                return;
            }

            string? key = _pendingApiKey;
            string normalized = LlmSettingsStore.NormalizeBaseUrl(AiManualBaseUrl);

            ModelCatalogResult catalog = await _catalog
                .FetchAsync(normalized, key, CancellationToken.None)
                .ConfigureAwait(true);

            AiManualModelOptions.Clear();
            foreach (ModelEntry entry in catalog.Models) AiManualModelOptions.Add(entry.Id);

            if (AiManualModelOptions.Count == 0)
            {
                AiManualStatus = "该地址没有返回任何模型，无法新增。";
                return;
            }

            string model = string.IsNullOrWhiteSpace(AiManualModel)
                ? AiManualModelOptions[0]
                : AiManualModel;

            LlmProfile profile = _llmSettings.AddProfile(
                string.Empty,
                normalized,
                model,
                key,
                LlmStrength.High,
                [.. AiManualModelOptions]);

            _pendingApiKey = null;
            RaiseApiKeyAccepted();
            AiShowManualBaseUrl = false;

            RefreshAiProfiles();

            AiManualStatus = $"已新增「{profile.Name}」→ {profile.BaseUrl}（模型 {profile.Model}）。";
            Status = AiManualStatus;
        }).ConfigureAwait(true);
    }

    /// <summary>重新拉取当前配置的模型清单并缓存。</summary>
    [RelayCommand]
    private async Task ReloadCurrentModelsAsync()
    {
        await RunGuardedAsync("刷新模型清单", async () =>
        {
            if (AiSelectedProfile is not { } row)
            {
                Status = "请先选择配置。";
                return;
            }

            LlmProfile? profile = _llmSettings.FindProfile(row.Id);
            if (profile is null) return;

            string? key = _llmSettings.GetApiKey(profile.Id);

            ModelCatalogResult result = await _catalog
                .FetchAsync(profile.BaseUrl, key, CancellationToken.None)
                .ConfigureAwait(true);

            if (result.Models.Count > 0)
                _llmSettings.SetModels(profile.Id, [.. result.Models.Select(m => m.Id)]);

            RefreshAiProfiles();

            Status = $"已刷新：{result.Models.Count} 个模型（{(result.Source == ModelCatalogSource.Remote ? "服务端" : "内置兜底")}）。";
        }).ConfigureAwait(true);
    }

    /// <summary>把"使用"区选中的模型与强度写回当前配置（点应用才生效）。</summary>
    [RelayCommand]
    private void ApplyCurrentSelection()
    {
        if (AiSelectedProfile is not { } row)
        {
            Status = "请先选择配置。";
            return;
        }

        if (string.IsNullOrWhiteSpace(AiCurrentModel))
        {
            Status = "请先选择模型。";
            return;
        }

        bool supports = ModelCatalogService.SupportsReasoningEffort(AiCurrentModel);

        bool ok = _llmSettings.UpdateProfile(
            row.Id,
            model: AiCurrentModel,
            // 不支持就不写强度，避免下次误以为"设了 High 会生效"
            strength: supports ? (LlmStrength)AiStrengthIndex : LlmStrength.High);

        RefreshAiProfiles();

        Status = ok
            ? $"已应用：模型 {AiCurrentModel}" +
              (supports ? $"，强度 {(LlmStrength)AiStrengthIndex}。" : "（该模型不支持思考强度）。")
            : "应用失败：该配置已不存在。";
    }

    /// <summary>测试选中配置的连通性。</summary>
    [RelayCommand]
    private async Task TestAiConnectionAsync()
    {
        await RunGuardedAsync("测试模型连通性", async () =>
        {
            if (AiSelectedProfile is not { } row)
            {
                Status = "请先选择配置。";
                return;
            }

            LlmProfile? profile = _llmSettings.FindProfile(row.Id);
            if (profile is null)
            {
                Status = "该配置已不存在。";
                return;
            }

            string? key = _llmSettings.GetApiKey(profile.Id);

            if (key is null)
            {
                Status = "该配置的密钥无法解密，请删除后重新新增。";
                return;
            }

            string model = string.IsNullOrWhiteSpace(AiCurrentModel) ? profile.Model : AiCurrentModel;

            using var provider = new OpenAiCompatibleProvider(
                profile.BaseUrl,
                key,
                model,
                profile.Temperature,
                profile.TimeoutSeconds,
                ModelCatalogService.BuildReasoning(model, (LlmStrength)AiStrengthIndex),
                _logger);

            LlmChatResponse response = await provider.ChatAsync(
                new LlmChatRequest(
                    [LlmMessage.User("回复 OK 两个字母即可，不要多说。")],
                    [],
                    Model: model,
                    Temperature: 0,
                    MaxTokens: 32),
                CancellationToken.None).ConfigureAwait(true);

            string text = (response.Message.Content ?? string.Empty).Trim();

            Status = string.IsNullOrEmpty(text)
                ? $"连通成功（模型 {model}，返回空内容）。"
                : $"连通成功（模型 {model}）：{Truncate(text, 60)}";
        }).ConfigureAwait(true);
    }

    /// <summary>删除选中的配置（含密钥），需二次确认。</summary>
    [RelayCommand]
    private void RemoveAiProfile()
    {
        if (AiSelectedProfile is not { } row)
        {
            Status = "请先选择要删除的配置。";
            return;
        }

        bool confirmed = Dialog.Confirm(
            "删除模型配置",
            $"确定删除「{row.Name}」吗？\n\n" +
            $"baseUrl：{row.BaseUrl}\n模型：{row.Model}\n\n" +
            "它的密钥会被一并删除且无法恢复（加密存储，本来也看不到明文）。");

        if (!confirmed)
        {
            Status = "已取消删除。";
            return;
        }

        bool removed = _llmSettings.RemoveProfile(row.Id);

        RefreshAiProfiles();

        Status = removed ? $"已删除配置「{row.Name}」。密钥同时删除。" : "该配置已不存在。";
    }

    /// <summary>切换当前使用的配置。</summary>
    [RelayCommand]
    private void UseAiProfile()
    {
        if (AiSelectedProfile is not { } row)
        {
            Status = "请先选择要使用的配置。";
            return;
        }

        bool ok = _llmSettings.SetActive(row.Id);

        RefreshAiProfiles();

        Status = ok ? $"已切换到「{row.Name}」（模型 {row.Model}）。" : "切换失败：该配置已不存在。";
    }

    /// <summary>把本次与累计用量写进这条助手消息（用量小框里显示）。</summary>
    private void ApplyAiUsage(LlmUsage? usage, ChatMessage message)
    {
        if (usage is not null)
            _sessionUsage = _sessionUsage is null ? usage : _sessionUsage.Add(usage);

        // 本轮摘要（气泡右下角）：只说这一轮，会话总量已经移到页面底栏
        message.UsageLine = usage is null
            ? "用量 —"
            : $"本次 {usage.TotalTokens:N0} tok";

        if (usage is null)
        {
            message.UsageDetail = "本次没有拿到用量（服务端未返回 usage 字段）。";
        }
        else
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine($"提供方 / 模型：{_agent.ProviderName} / {AiCurrentModel}");
            text.AppendLine("—— 本轮 ——");
            text.AppendLine($"未缓存输入：{usage.UncachedInputTokens:N0} tok");
            text.AppendLine($"缓存读取：{usage.CachedTokens:N0} tok（命中 {usage.CacheHitRate:P1}）");
            text.AppendLine($"输出：{usage.CompletionTokens:N0} tok");
            text.AppendLine($"合计：{usage.TotalTokens:N0} tok");
            text.AppendLine("—— 上下文 ——");
            text.AppendLine(AiContextSummary);

            message.UsageDetail = text.ToString().TrimEnd();
        }

        RefreshSessionUsage();

        // 用量落盘：重启后历史对话仍能看到每一轮花了多少
        if (usage is not null)
            Services.UsageJournal.Append(_activeUsageContextId, usage);

        RefreshSessionUsage();
    }

    /// <summary>
    /// 会话累计用量（AI 页最底部那一行）。
    /// 从每条消息的小框里剥离出来：单轮明细留在气泡里，总量只在这里出现一次。
    /// </summary>
    [ObservableProperty]
    private string _sessionUsageLine = "本次运行累计：还没有调用模型";

    private string? _activeUsageContextId;

    /// <summary>底部那行"本上下文累计"是否展开明细小框。</summary>
    [ObservableProperty]
    private bool _sessionUsageOpen;

    /// <summary>底部小框里的明细（输入/缓存/输出/合计 + 缓存命中率）。</summary>
    [ObservableProperty]
    private string _sessionUsageDetail = "还没有用量记录。";

    [RelayCommand]
    private void ToggleSessionUsage() => SessionUsageOpen = !SessionUsageOpen;

    private void RefreshSessionUsage()
    {
        // 以落盘流水为准：历史轮次 + 本次运行都在里面，重启不清零
        IReadOnlyList<Services.TurnUsageEntry> entries = Services.UsageJournal.Read(_activeUsageContextId);

        if (entries.Count == 0)
        {
            SessionUsageLine = "本上下文累计：还没有用量记录";
            SessionUsageDetail = "还没有用量记录。跑一次 AI 任务后这里会出现明细。";
            return;
        }

        long uncached = entries.Sum(e => (long)e.UncachedInputTokens);
        long cached = entries.Sum(e => (long)e.CachedTokens);
        long completion = entries.Sum(e => (long)e.CompletionTokens);
        long total = uncached + cached + completion;

        double hitRate = uncached + cached == 0 ? 0 : (double)cached / (uncached + cached);

        SessionUsageLine =
            $"本上下文累计 {total:N0} tok（{entries.Count} 轮）　·　输入 {uncached + cached:N0}（缓存命中 {hitRate:P0}）　·　输出 {completion:N0}";

        SessionUsageDetail =
            $"上下文：{_activeUsageContextId ?? "(未启用)"}{Environment.NewLine}" +
            $"轮次：{entries.Count}{Environment.NewLine}" +
            "—— 合计 ——" + Environment.NewLine +
            $"未缓存输入：{uncached:N0} tok{Environment.NewLine}" +
            $"缓存读取：{cached:N0} tok（命中 {hitRate:P1}）{Environment.NewLine}" +
            $"输出：{completion:N0} tok{Environment.NewLine}" +
            $"总计：{total:N0} tok";
    }
}
