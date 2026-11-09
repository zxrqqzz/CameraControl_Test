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

    public MainViewModel(
        ICameraProvider provider,
        ICameraSessionRegistry sessions,
        ICaptureGroupService capture,
        ISelfCheck selfCheck,
        IMetrics metrics,
        IAuditLog audit,
        IAiAgent agent,
        LlmSettingsStore llmSettings)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(selfCheck);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(llmSettings);

        _provider = provider;
        _sessions = sessions;
        _capture = capture;
        _selfCheck = selfCheck;
        _metrics = metrics;
        _audit = audit;
        _agent = agent;
        _llmSettings = llmSettings;

        LoadAiSettings();
    }

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

    [ObservableProperty]
    private string _aiBaseUrl = string.Empty;

    [ObservableProperty]
    private string _aiModel = string.Empty;

    [ObservableProperty]
    private string _aiApiKeyInput = string.Empty;

    [ObservableProperty]
    private string _aiKeyStatus = "(未配置)";

    [ObservableProperty]
    private string _aiGoal = "列出当前所有相机，并告诉我哪一台可以采集";

    [ObservableProperty]
    private string _aiAnswer = string.Empty;

    public ObservableCollection<string> AiSteps { get; } = [];

    private void LoadAiSettings()
    {
        LlmSettings settings = _llmSettings.Load();

        AiBaseUrl = settings.BaseUrl;
        AiModel = settings.Model;
        AiKeyStatus = _llmSettings.GetApiKey() is null ? "(未配置)" : "(已配置，已加密存储)";
    }

    [RelayCommand]
    private void SaveAiConfig()
    {
        try
        {
            LlmSettings settings = _llmSettings.Load();
            settings.BaseUrl = AiBaseUrl.Trim();
            settings.Model = AiModel.Trim();
            _llmSettings.Save(settings);

            if (!string.IsNullOrWhiteSpace(AiApiKeyInput))
            {
                _llmSettings.SetApiKey(AiApiKeyInput.Trim());
                AiApiKeyInput = string.Empty;
            }

            AiKeyStatus = _llmSettings.GetApiKey() is null ? "(未配置)" : "(已配置，已加密存储)";
            Status = _llmSettings.IsConfigured()
                ? "AI 配置已保存。"
                : "已保存，但仍缺少 BaseUrl / Model / API Key。";
        }
        catch (Exception ex)
        {
            Status = $"保存配置失败：{ex.Message}";
        }
    }

    [RelayCommand]
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
            Status = $"{action}失败：相机不存在（{ex.Message}）。请先刷新设备列表。";
        }
        catch (CameraOperationException ex)
        {
            Status = $"{action}失败：[{ex.ErrorCode}] {ex.Message}";
        }
        catch (LlmException ex)
        {
            Status = $"{action}失败：[{ex.ErrorCode}] {ex.Message}";
        }
        catch (Exception ex)
        {
            Status = $"{action}失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
