using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CameraControl.Application.Auditing;
using CameraControl.Application.Capture;
using CameraControl.Application.Commands;
using CameraControl.Application.Cua;
using CameraControl.Application.Diagnostics;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Capture;
using CameraControl.Domain.Ports;
using CameraControl.Infrastructure.Ai;
using CameraControl.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CameraControl.Presentation.ViewModels;

/// <summary>相机列表的一行。</summary>
public sealed partial class CameraRowViewModel : ObservableObject
{
    public CameraRowViewModel(CameraDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public CameraDescriptor Descriptor { get; }

    public string Id => Descriptor.Id.Value;

    public string DisplayName => Descriptor.DisplayName;

    public string KindText => Descriptor.Kind switch
    {
        CameraKind.HikGigE => "海康 GigE",
        CameraKind.HikUsb3 => "海康 USB3",
        CameraKind.Uvc => "UVC / 内置",
        _ => Descriptor.Kind.ToString()
    };

    public bool IsInfrared => Descriptor.IsInfraredOnly;

    public bool CanCapture => Descriptor.CanCapture;

    /// <summary>是否可用（红外设备只列出，不允许取流）。</summary>
    public string Hint => IsInfrared ? "仅列出，不取流（隐私）" : string.Empty;

    [ObservableProperty]
    private bool _isOpen;
}

/// <summary>主视图模型：设备 / 系统 / AI 三块。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ICameraProvider _provider;
    private readonly ICameraSessionRegistry _sessions;
    private readonly ICaptureGroupService _capture;
    private readonly ISelfCheck _selfCheck;
    private readonly IMetrics _metrics;
    private readonly IAuditLog _audit;
    private readonly IAiAgent _agent;
    private readonly LlmSettingsStore _llmSettings;
    private readonly ModelCatalogService _catalog;
    private readonly ProviderCatalog _providerCatalog;
    private readonly IAppLogger _logger;

    public MainViewModel(
        ICameraProvider provider,
        ICameraSessionRegistry sessions,
        ICaptureGroupService capture,
        ISelfCheck selfCheck,
        IMetrics metrics,
        IAuditLog audit,
        IAiAgent agent,
        LlmSettingsStore llmSettings,
        ModelCatalogService catalog,
        ProviderCatalog providerCatalog,
        IAppLogger logger,
        CameraControl.Application.Commands.ICommandBus bus)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(selfCheck);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(llmSettings);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(providerCatalog);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(bus);

        _provider = provider;
        _sessions = sessions;
        _capture = capture;
        _selfCheck = selfCheck;
        _metrics = metrics;
        _audit = audit;
        _agent = agent;
        _llmSettings = llmSettings;
        _catalog = catalog;
        _providerCatalog = providerCatalog;
        _logger = logger;

        Calibration = new CalibrationPanelViewModel(bus);

        LoadAiSettings();
    }

    /// <summary>标定面板（采图 → 检测 → 求解）。</summary>
    public CalibrationPanelViewModel Calibration { get; }

    // ── 通用 ────────────────────────────────────────────────

    [ObservableProperty]
    private string _status = "就绪。";

    [ObservableProperty]
    private bool _isBusy;

    // ── 设备 ────────────────────────────────────────────────

    public ObservableCollection<CameraRowViewModel> Cameras { get; } = [];

    [ObservableProperty]
    private CameraRowViewModel? _selectedCamera;

    [ObservableProperty]
    private BitmapSource? _previewImage;

    [ObservableProperty]
    private string _imageInfo = string.Empty;

    [RelayCommand]
    private async Task RefreshCamerasAsync()
    {
        await RunGuardedAsync("刷新设备列表", async () =>
        {
            IReadOnlyList<CameraDescriptor> devices = await _provider.DiscoverAsync(CancellationToken.None);

            Cameras.Clear();
            foreach (CameraDescriptor descriptor in devices)
                Cameras.Add(new CameraRowViewModel(descriptor));

            SelectedCamera ??= Cameras.FirstOrDefault(c => c.CanCapture);

            Calibration.SyncCameras(Cameras);
            Calibration.SelectedCamera ??= SelectedCamera;

            Status = devices.Count == 0
                ? "未发现相机。请确认相机已上电、网线已连接、MVS 客户端已安装。"
                : $"发现 {devices.Count} 台设备（可采集 {devices.Count(c => c.CanCapture)} 台）。";
        });
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedCamera is not CameraRowViewModel row) return;

        if (!row.CanCapture)
        {
            Status = $"{row.DisplayName} 不允许取流（{row.Hint}）。";
            return;
        }

        await RunGuardedAsync($"连接 {row.DisplayName}", async () =>
        {
            await _sessions.OpenAsync(row.Descriptor.Id, CancellationToken.None);
            row.IsOpen = true;
            Status = $"已连接：{row.DisplayName}";
        });
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (SelectedCamera is not CameraRowViewModel row) return;

        await RunGuardedAsync($"断开 {row.DisplayName}", async () =>
        {
            await _sessions.CloseAsync(row.Descriptor.Id);
            row.IsOpen = false;
            PreviewImage = null;
            ImageInfo = string.Empty;
            Status = $"已断开：{row.DisplayName}";
        });
    }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (SelectedCamera is not CameraRowViewModel row) return;

        await RunGuardedAsync("单帧采集", async () =>
        {
            // 就绪门当前是占位实现：固定延时。真实振动判据见 docs/05。
            var request = CaptureGroupRequest.ForSingleCamera(
                row.Descriptor.Id,
                readiness: ReadinessCriteria.FixedDelayPlaceholder(TimeSpan.FromMilliseconds(200)));

            CaptureGroupResult result = await _capture.CaptureAsync(request, CancellationToken.None);

            if (result.Frames.Count == 0)
            {
                Status = "采集失败：未收到帧。";
                return;
            }

            Frame frame = result.Frames[0];
            BitmapSource? bitmap = FrameToBitmap.Convert(frame.Image);

            if (bitmap is null)
            {
                Status = $"不支持的像素格式：{frame.Image.PixelFormat}";
                return;
            }

            PreviewImage = bitmap;
            ImageInfo = $"{frame.Image.Width}×{frame.Image.Height}  {frame.Image.PixelFormat}  " +
                        $"{FrameToBitmap.FormatBytes(frame.Image.Pixels.Length)}  " +
                        $"时间戳精度：{result.TimestampQuality}";
            Status = $"采集成功（{result.Frames.Count} 帧，偏斜 {result.Skew.TotalMilliseconds:F1}ms）。";
        });
    }

    // ── 系统 ────────────────────────────────────────────────

    public ObservableCollection<string> SelfCheckItems { get; } = [];

    public ObservableCollection<string> MetricItems { get; } = [];

    public ObservableCollection<string> AuditItems { get; } = [];

    [RelayCommand]
    private async Task RunSelfCheckAsync()
    {
        await RunGuardedAsync("启动自检", async () =>
        {
            SelfCheckReport report = await _selfCheck.RunAsync(CancellationToken.None);

            SelfCheckItems.Clear();
            foreach (HealthItem item in report.Items)
            {
                string mark = item.Status switch
                {
                    HealthStatus.Ok => "[正常]",
                    HealthStatus.Warn => "[注意]",
                    _ => "[失败]"
                };

                SelfCheckItems.Add($"{mark} {item.Name}: {item.Message}");
            }

            Status = report.OverallOk
                ? "自检通过。"
                : $"自检发现 {report.FailCount} 项失败、{report.WarnCount} 项注意。";
        });
    }

    [RelayCommand]
    private void RefreshMetrics()
    {
        MetricItems.Clear();

        foreach (KeyValuePair<string, double> pair in _metrics.Snapshot())
            MetricItems.Add($"{pair.Key} = {pair.Value:0.###}");

        if (MetricItems.Count == 0) MetricItems.Add("(还没有任何指标，先执行几个操作)");
    }

    [RelayCommand]
    private void RefreshAudit()
    {
        AuditItems.Clear();

        foreach (AuditEntry entry in _audit.Recent(100))
        {
            string mark = entry.Ok ? "OK  " : "FAIL";
            AuditItems.Add(
                $"{entry.At.ToLocalTime():HH:mm:ss} {mark} [{entry.Actor.Kind}] " +
                $"{entry.CapabilityId} ({entry.Risk}, {entry.Elapsed.TotalMilliseconds:F0}ms)" +
                (entry.ErrorCode is null ? "" : $"  {entry.ErrorCode}"));
        }

        if (AuditItems.Count == 0) AuditItems.Add("(暂无审计记录)");
    }

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

    [ObservableProperty]
    private string _aiCatalogSource = string.Empty;

    [ObservableProperty]
    private string _aiGoal = "列出当前所有相机，并告诉我哪一台可以采集";

    [ObservableProperty]
    private string _aiAnswer = string.Empty;

    /// <summary>当前配置可选的模型。</summary>
    public ObservableCollection<string> AiCurrentModelOptions { get; } = [];

    public ObservableCollection<string> AiSteps { get; } = [];

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

        Status = settings.Profiles.Count == 0
            ? "还没有配置：填密钥后点「识别并新增」即可。"
            : $"共 {settings.Profiles.Count} 套配置，当前使用 {active?.Name ?? "(无)"}。";
    }

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

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    private async Task RunAiAsync()
    {
        if (string.IsNullOrWhiteSpace(AiGoal))
        {
            Status = "请先输入目标。";
            return;
        }

        AiSteps.Clear();
        AiAnswer = string.Empty;

        await RunGuardedAsync("AI 任务", async () =>
        {
            AiTaskResult result = await _agent.RunAsync(
                new AiTaskRequest(AiGoal, MaxSteps: 8),
                Actor.Ai("wpf-agent", Guid.NewGuid().ToString("N")[..8]),
                CancellationToken.None);

            foreach (AiStep step in result.Steps)
            {
                if (!string.IsNullOrWhiteSpace(step.AssistantText))
                    AiSteps.Add($"[{step.Step}] 模型：{step.AssistantText}");

                foreach (AiToolInvocation invocation in step.Invocations)
                {
                    string mark = invocation.Ok ? "OK  " : "FAIL";
                    AiSteps.Add(
                        $"[{step.Step}] {mark} {invocation.CapabilityId} ({invocation.ElapsedMs:F0}ms)" +
                        (invocation.ErrorCode is null ? "" : $"  {invocation.ErrorCode}"));
                }
            }

            if (result.Completed)
            {
                AiAnswer = result.Answer ?? "(模型没有给出文字答复)";
                Status = $"AI 任务完成，工具调用 {result.ToolCallCount} 次。";
            }
            else
            {
                AiAnswer = result.Error ?? "任务未完成。";
                Status = $"AI 任务未完成（{result.StopReason}）。";
            }
        });
    }

    // ── 统一异常兜底 ────────────────────────────────────────

    private async Task RunGuardedAsync(string action, Func<Task> body)
    {
        if (IsBusy) return;

        IsBusy = true;
        Status = $"{action}…";

        try
        {
            await body();
        }
        catch (CameraNotFoundException ex)
        {
            LogFailure(action, ex, null);
            Status = $"{action}失败：相机不存在（{ex.Message}）。请先刷新设备列表。";
        }
        catch (CameraOperationException ex)
        {
            LogFailure(action, ex, ex.ErrorCode);
            Status = $"{action}失败：[{ex.ErrorCode}] {ex.Message}";
        }
        catch (LlmException ex)
        {
            LogFailure(action, ex, ex.ErrorCode);
            Status = $"{action}失败：[{ex.ErrorCode}] {ex.Message}";
        }
        catch (Exception ex)
        {
            LogFailure(action, ex, null);
            Status = $"{action}失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 把失败写进日志。
    ///
    /// 为什么必须记：界面上的 Status 只显示一次、随下一次操作消失；
    /// 之前"请求失败"这类问题在日志里查不到任何痕迹，等于无法排障。
    /// </summary>
    private void LogFailure(string action, Exception exception, string? errorCode)
    {
        try
        {
            _logger.Error($"{action}失败", new Dictionary<string, object?>
            {
                ["action"] = action,
                ["errorCode"] = errorCode,
                ["type"] = exception.GetType().Name,
                ["message"] = exception.Message,
                ["inner"] = exception.InnerException?.Message
            });
        }
        catch
        {
            // 记日志失败不能再抛
        }
    }
}
