using System.Diagnostics;
using CameraControl.Application.Auditing;
using CameraControl.Application.Capture;
using CameraControl.Application.Commands;
using CameraControl.Application.Cua;
using CameraControl.Application.Cua.Context;
using CameraControl.Application.Diagnostics;
using CameraControl.Domain.Ports;
using CameraControl.Infrastructure.Ai;
using CameraControl.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CameraControl.Presentation.ViewModels;

/// <summary>
/// 主窗口视图模型：设备枚举与采图、系统自检、AI 对话与模型配置的界面状态与命令。
///
/// 体量较大，按功能拆成多个 partial 文件，一个文件只负责一块：
///   MainViewModel.Devices.cs     设备：枚举 / 连接 / 断开 / 采图
///   MainViewModel.System.cs      系统：自检 / 指标 / 审计
///   MainViewModel.AiSettings.cs  AI：模型与强度选择
///   MainViewModel.AiProfiles.cs  AI：模型配置的增删改测 + 用量统计
///   MainViewModel.AiChat.cs      AI：对话执行 / 停止 / 历史 / 进度
///   MainViewModel.AiContext.cs   AI：上下文档位与窗口入口
/// 共用字段、构造函数与 <see cref="RunGuardedAsync"/> 等基础设施留在本文件。
/// </summary>
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
    private readonly IContextStore _contextStore;
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
        IContextStore contextStore,
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
        ArgumentNullException.ThrowIfNull(contextStore);
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
        _contextStore = contextStore;
        _logger = logger;

        Calibration = new CalibrationPanelViewModel(bus);
        Appearance = new AppearanceViewModel(Services.ThemeService.Current);

        LoadAiSettings();
        LoadAiHistory();
    }

    /// <summary>标定面板（采图 → 检测 → 求解）。</summary>
    public CalibrationPanelViewModel Calibration { get; }

    /// <summary>调色卡：主色切换（顶栏「调色」）。</summary>
    public AppearanceViewModel Appearance { get; }

    // ── 通用 ────────────────────────────────────────────────

    [ObservableProperty]
    private string _status = "就绪。";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// 当前页签索引。内容区 TabControl 与左侧导航同绑这一个值，
    /// 页签顺序只在这一处定义，导航文案不再和控件名相互耦合。
    /// </summary>
    [ObservableProperty]
    private int _selectedPageIndex;

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
        catch (Exception logException)
        {
            // 记日志本身失败不能再抛：退到调试输出，至少留一条痕迹
            Debug.WriteLine($"写日志失败（原始错误 {exception.GetType().Name}）：{logException.Message}");
        }
    }
}
