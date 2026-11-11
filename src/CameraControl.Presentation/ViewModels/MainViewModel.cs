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

    /// <summary>配置列表（多条并存，可切换）。</summary>
    public ObservableCollection<LlmProfileRow> AiProfiles { get; } = [];

    [ObservableProperty]
    private LlmProfileRow? _aiSelectedProfile;

    [ObservableProperty]
    private string _aiNewName = string.Empty;

    [ObservableProperty]
    private string _aiNewBaseUrl = string.Empty;

    [ObservableProperty]
    private string _aiNewModel = string.Empty;

    /// <summary>强度：0=低 1=中 2=高。</summary>
    [ObservableProperty]
    private int _aiStrengthIndex = 1;

    [ObservableProperty]
    private string _aiKeyStatus = "(未配置)";

    [ObservableProperty]
    private string _aiCatalogSource = "尚未拉取模型清单";

    [ObservableProperty]
    private string _aiGoal = "列出当前所有相机，并告诉我哪一台可以采集";

    [ObservableProperty]
    private string _aiAnswer = string.Empty;

    /// <summary>可选模型（来自服务端 /models 或内置兜底）。</summary>
    public ObservableCollection<string> AiModelOptions { get; } = [];

    public ObservableCollection<string> AiSteps { get; } = [];

    /// <summary>
    /// 对话框服务（删除确认）。由 App 注入；未注入时拒绝删除，避免误删。
    /// </summary>
    public IDialogService Dialog { get; set; } = new RefusingDialogService();

    /// <summary>PasswordBox 只能写不能读：由视图调用此方法回填。</summary>
    public void SetPendingApiKey(string? value) => _pendingApiKey = value;

    private string? _pendingApiKey;

    private void LoadAiSettings() => RefreshAiProfiles();

    /// <summary>重新读取配置列表。</summary>
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

        Status = settings.Profiles.Count == 0
            ? "还没有模型配置：填 baseUrl、模型与 key 后点「新增配置」。"
            : $"共 {settings.Profiles.Count} 套配置，当前使用 {active?.Name ?? "(无)"}。";
    }

    /// <summary>拉取模型清单（失败自动用内置兜底）。</summary>
    [RelayCommand]
    private async Task RefreshAiModelsAsync()
    {
        await RunGuardedAsync("拉取模型清单", async () =>
        {
            LlmProfileRow? row = AiSelectedProfile;
            LlmProfile? profile = row is null ? null : _llmSettings.FindProfile(row.Id);

            string baseUrl = string.IsNullOrWhiteSpace(AiNewBaseUrl)
                ? profile?.BaseUrl ?? string.Empty
                : LlmSettingsStore.NormalizeBaseUrl(AiNewBaseUrl);

            // 还没保存配置时，用界面上刚输入的 key——否则会"没带 key"地去请求，
            // 拿到 401 却让人以为 key 错了。
            string? key = string.IsNullOrWhiteSpace(_pendingApiKey)
                ? (profile is null ? null : _llmSettings.GetApiKey(profile.Id))
                : _pendingApiKey;

            if (string.IsNullOrWhiteSpace(key))
            {
                AiCatalogSource = "还没有密钥：请先在「密钥」里输入，再拉取模型（不带 key 请求会被服务端拒绝）。";
                Status = AiCatalogSource;
                return;
            }

            ModelCatalogResult result = await _catalog
                .FetchAsync(baseUrl, key, CancellationToken.None)
                .ConfigureAwait(true);

            AiModelOptions.Clear();

            foreach (ModelEntry entry in result.Models)
                AiModelOptions.Add(entry.Id);

            AiCatalogSource = result.Source == ModelCatalogSource.Remote
                ? $"来源：服务端 /models（{result.Models.Count} 个）"
                : $"来源：内置兜底（{result.Models.Count} 个）— {result.Error}";

            if (AiModelOptions.Count > 0 && string.IsNullOrWhiteSpace(AiNewModel))
                AiNewModel = AiModelOptions[0];

            Status = AiCatalogSource;
        }).ConfigureAwait(true);
    }

    /// <summary>新增一套配置（唯一写入 key 的入口）。</summary>
    [RelayCommand]
    private void AddAiProfile()
    {
        try
        {
            string strengthText = AiStrengthIndex switch { 0 => "低", 2 => "高", _ => "中" };

            LlmProfile profile = _llmSettings.AddProfile(
                AiNewName,
                AiNewBaseUrl,
                AiNewModel,
                _pendingApiKey,
                (LlmStrength)AiStrengthIndex);

            // key 落盘后立刻从内存清掉，界面也不再保留
            _pendingApiKey = null;

            AiNewName = string.Empty;
            AiNewBaseUrl = string.Empty;
            AiNewModel = string.Empty;
            AiStrengthIndex = 1;

            RefreshAiProfiles();

            Status = $"已新增配置「{profile.Name}」→ {profile.BaseUrl}（模型 {profile.Model}，强度 {strengthText}）。" +
                     "密钥已加密保存，界面不可再查看或修改。";
        }
        catch (Exception ex)
        {
            Status = $"新增配置失败：{ex.Message}";
        }
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

        Status = ok
            ? $"已切换到「{row.Name}」（模型 {row.Model}，强度 {row.Strength}）。下次调用立即生效。"
            : "切换失败：该配置已不存在。";
    }

    /// <summary>测试选中配置的连通性（发一个最小请求）。</summary>
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

            using var provider = new OpenAiCompatibleProvider(
                profile.BaseUrl,
                key,
                profile.Model,
                profile.Temperature,
                profile.TimeoutSeconds,
                ModelCatalogService.SupportsReasoningEffort(profile.Model)
                    ? ModelCatalogService.ToReasoningEffort(profile.Strength)
                    : null);

            LlmChatResponse response = await provider.ChatAsync(
                new LlmChatRequest(
                    [LlmMessage.User("回复 OK 两个字母即可，不要多说。")],
                    [],
                    Model: profile.Model,
                    Temperature: 0,
                    MaxTokens: 16),
                CancellationToken.None).ConfigureAwait(true);

            string text = (response.Message.Content ?? string.Empty).Trim();

            Status = string.IsNullOrEmpty(text)
                ? $"连通成功（模型 {profile.Model}，返回空内容）。"
                : $"连通成功（模型 {profile.Model}）：{Truncate(text, 60)}";
        }).ConfigureAwait(true);
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
