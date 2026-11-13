using System.Collections.ObjectModel;
using CameraControl.Application.Cua;
using CameraControl.Infrastructure.Ai;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CameraControl.Presentation.ViewModels;

/// <summary>AI 设置块：模型档案、模型 / 强度 / 密钥的当前选择。</summary>
public sealed partial class MainViewModel
{

    // ── AI ──────────────────────────────────────────────────

    /// <summary>已保存的配置（多条并存，可切换）。</summary>
    public ObservableCollection<LlmProfileRow> AiProfiles { get; } = [];

    [ObservableProperty]
    private LlmProfileRow? _aiSelectedProfile;

    // ── 新增：只填密钥，自动识别厂商 ──────────────────

    [ObservableProperty]
    private string _aiDetectStatus = "填入密钥后点「识别并新增」——会自动判断是哪家服务，不用填 baseUrl。";

    /// <summary>识别失败时才显示的手填区域。</summary>
    [ObservableProperty]
    private bool _aiShowManualBaseUrl;

    [ObservableProperty]
    private string _aiManualBaseUrl = string.Empty;

    [ObservableProperty]
    private string _aiManualModel = string.Empty;

    [ObservableProperty]
    private string _aiManualStatus = string.Empty;

    /// <summary>手填时兜底的模型清单。</summary>
    public ObservableCollection<string> AiManualModelOptions { get; } = [];

    // ── 使用：切换该 key 下的模型与强度 ──────────────

    [ObservableProperty]
    private string _aiCurrentModel = string.Empty;

    [ObservableProperty]
    private int _aiStrengthIndex = 1;

    /// <summary>
    /// 当前模型是否支持思考强度。
    /// 只有支持时才显示强度下拉——见 XAML 的 Visibility 绑定。
    /// </summary>
    [ObservableProperty]
    private bool _aiStrengthSupported;

    [ObservableProperty]
    private string _aiStrengthHint = string.Empty;

    [ObservableProperty]
    private string _aiKeyStatus = "(未配置)";

    /// <summary>主界面顶部的一行摘要（配置详情在设置窗口里）。</summary>
    [ObservableProperty]
    private string _aiSettingsSummary = "还没有配置模型";

    [ObservableProperty]
    private string _aiCatalogSource = string.Empty;

    [ObservableProperty]
    private string _aiGoal = "列出当前所有相机，并告诉我哪一台可以采集";

    /// <summary>
    /// 对话流：用户消息靠右、助手消息靠左，一条一轮。
    /// 历史（本地上下文里的条目）在启动 / 切换配置时灌进来，本次运行的消息直接追加。
    /// </summary>
    public ObservableCollection<ChatMessage> AiMessages { get; } = [];

    /// <summary>对话区是否为空（决定要不要显示"问点什么试试"的占位）。</summary>
    public bool HasAiMessages => AiMessages.Count > 0;

    /// <summary>输入行右侧显示的"模型 · 强度"（点击打开 AI 设置）。</summary>
    [ObservableProperty]
    private string _aiModelLine = "未配置模型";

    /// <summary>滚到底的令牌：每来一段增量就 +1，视图侧据此把对话区滚到底。</summary>
    [ObservableProperty]
    private int _chatScrollTick;

    /// <summary>本次会话累计用量（进程内，重启清零）。</summary>
    private LlmUsage? _sessionUsage;

    /// <summary>当前配置可选的模型。</summary>
    public ObservableCollection<string> AiCurrentModelOptions { get; } = [];

    public IDialogService Dialog { get; set; } = new RefusingDialogService();

    /// <summary>PasswordBox 只能写不能读：由视图调用此方法回填。</summary>
    public void SetPendingApiKey(string? value) => _pendingApiKey = value;

    /// <summary>
    /// 密钥已被接受（新增成功）。视图收到后应清空 PasswordBox——
    /// 密钥落盘后不该继续显示在界面上。
    /// </summary>
    public event EventHandler? ApiKeyAccepted;

    private void RaiseApiKeyAccepted() => ApiKeyAccepted?.Invoke(this, EventArgs.Empty);

    private string? _pendingApiKey;

    private void LoadAiSettings() => RefreshAiProfiles();

    /// <summary>模型变化时刷新强度可用性。</summary>
    partial void OnAiCurrentModelChanged(string value) => RefreshStrengthState(value);

    private void RefreshStrengthState(string? model)
    {
        AiStrengthSupported = !string.IsNullOrWhiteSpace(model)
                              && ModelCatalogService.SupportsReasoningEffort(model);

        AiStrengthHint = AiStrengthSupported
            ? string.Empty
            : string.Empty;
    }

    /// <summary>重新读取配置列表，并同步"当前使用"的模型/强度。</summary>
    [RelayCommand]
    private void RefreshAiProfiles()
    {
        LlmSettings settings = _llmSettings.Load();
        string? activeBefore = _llmSettings.ActiveProfile()?.Id;
        LlmProfile? active = _llmSettings.ActiveProfile();

        string? previousId = AiSelectedProfile?.Id;

        AiProfiles.Clear();

        foreach (LlmProfile profile in settings.Profiles)
        {
            AiProfiles.Add(new LlmProfileRow(
                profile.Id,
                profile.Name,
                profile.BaseUrl,
                profile.Model,
                profile.Strength,
                profile.KeyHint ?? "(无)",
                profile.Id == active?.Id));
        }

        AiSelectedProfile = AiProfiles.FirstOrDefault(p => p.Id == previousId)
                            ?? AiProfiles.FirstOrDefault(p => p.IsActive)
                            ?? AiProfiles.FirstOrDefault();

        AiKeyStatus = active is null
            ? "(未配置)"
            : _llmSettings.GetApiKey(active.Id) is null
                ? (active.HasKey ? "(无法解密，请删除后重建)" : "(未设置)")
                : $"已加密保存 {active.KeyHint}";

        LoadCurrentSelection(active);

        AiSettingsSummary = active is null
            ? "还没有配置模型 —— 点右侧「设置…」填入密钥"
            : $"{active.Name} · {active.Model} · 强度 {active.Strength}";

        // 输入行右侧那一行"模型 · 强度"（点它打开 AI 设置）
        AiModelLine = active is null
            ? "未配置模型"
            : $"{active.Model} · 强度 {active.Strength}";

        // 换配置才重载历史：关设置窗口也会走到这里，不能把刚发生的对话清掉
        if (active?.Id != activeBefore) LoadAiHistory();
        RefreshContextInfo();

        Status = settings.Profiles.Count == 0
            ? "还没有配置：填密钥后点「识别并新增」即可。"
            : $"共 {settings.Profiles.Count} 套配置，当前使用 {active?.Name ?? "(无)"}。";
    }
}
