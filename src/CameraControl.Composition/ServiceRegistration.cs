using CameraControl.Application.Auditing;
using CameraControl.Application.Capabilities;
using CameraControl.Application.Capture;
using CameraControl.Application.Commands;
using CameraControl.Application.Cua;
using CameraControl.Application.Diagnostics;
using CameraControl.Domain.Ports;
using CameraControl.Domain.Privacy;
using CameraControl.Domain.Robotics;
using CameraControl.Infrastructure;
using CameraControl.Infrastructure.Ai;
using CameraControl.Infrastructure.Auditing;
using CameraControl.Infrastructure.Diagnostics;
using CameraControl.Infrastructure.Hik;
using CameraControl.Infrastructure.Logging;
using CameraControl.Infrastructure.Privacy;
using CameraControl.Infrastructure.Readiness;
using CameraControl.Infrastructure.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace CameraControl.Composition;

/// <summary>组合根选项。</summary>
/// <param name="DataDirectory">数据根目录；null 用 %LocalAppData%\CameraControl。</param>
/// <param name="ConsoleLogging">是否同时输出到控制台（桌面应用通常只需文件）。</param>
/// <param name="MinimumLogLevel">文件日志最低级别。</param>
public sealed record CompositionOptions(
    string? DataDirectory = null,
    bool ConsoleLogging = true,
    LogLevel MinimumLogLevel = LogLevel.Information);

/// <summary>
/// 组合根：把所有端口与适配器装配起来。
///
/// 这是**唯一**允许同时看到 Domain / Application / Infrastructure / Serilog 的地方。
/// 其它宿主（WPF、CLI、诊断工具）都只调用这里，不自己 new 具体类型。
/// </summary>
public static class ServiceRegistration
{
    /// <summary>解析数据目录（公开出来便于宿主显示"日志在哪"）。</summary>
    public static string ResolveDataDirectory(string? dataDirectory = null) =>
        dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CameraControl");

    /// <summary>构造完整容器。调用方负责在退出时 Dispose。</summary>
    /// <param name="options">组合选项。</param>
    /// <param name="confirmationGate">
    /// 可选的人工确认门（WPF 宿主传入弹窗实现）。
    /// 传 null 时 CommandBus 会保持失败关闭——AI 的物理操作一律被拒。
    /// </param>
    public static ServiceProvider Build(
        CompositionOptions? options = null,
        IConfirmationGate? confirmationGate = null)
    {
        options ??= new CompositionOptions();

        string dataDirectory = ResolveDataDirectory(options.DataDirectory);
        string logDirectory = Path.Combine(dataDirectory, "logs");
        string auditDirectory = Path.Combine(dataDirectory, "audit");

        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(logDirectory);
        Directory.CreateDirectory(auditDirectory);

        ConfigureSerilog(logDirectory, options);

        var services = new ServiceCollection();

        // ── 横切 ────────────────────────────────────────────────
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IAppLogger>(_ => new SerilogAppLogger(Log.Logger));
        services.AddSingleton<IMetrics, InMemoryMetrics>();
        services.AddSingleton<IAuditLog>(_ => new FileAuditLog(auditDirectory));
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(dataDirectory));

        // ── 端口 → 适配器 ───────────────────────────────────────
        services.AddSingleton<ICameraProvider, HikCameraProvider>();
        services.AddSingleton<IImagePersistencePolicy, DefaultImagePersistencePolicy>();
        services.AddSingleton<SoftwareTriggerSource>();
        services.AddSingleton<ITriggerSourceFactory, TriggerSourceFactory>();

        // P0/P1 就绪门为固定延时占位；P2 换 VisionStabilityGate
        services.AddSingleton<IReadinessGate, FixedDelayGate>();

        // ── 机器人（P5：控制器尚未接入，先给明确"未连接"的实现）────
        // 换成真实适配器时只改这一行（如 YMConnectRobotAdapter）。
        services.AddSingleton<IRobot>(sp => new UnavailableRobot(sp.GetRequiredService<IClock>()));

        // ── 应用层 ──────────────────────────────────────────────
        services.AddSingleton<ICameraSessionRegistry, CameraSessionRegistry>();
        services.AddSingleton<ICaptureGroupService, CaptureGroupService>();
        services.AddSingleton<IRiskPolicy, DefaultRiskPolicy>();

        // ── 自检探针（新增检查项只需再加一个 IHealthProbe）──────
        services.AddSingleton<IHealthProbe, ElevationProbe>();
        services.AddSingleton<IHealthProbe, OperatingSystemProbe>();
        services.AddSingleton<IHealthProbe>(_ => new StorageProbe(dataDirectory));
        services.AddSingleton<IHealthProbe, HikSdkProbe>();
        services.AddSingleton<IHealthProbe, CameraProbe>();
        services.AddSingleton<ISelfCheck, SelfCheckService>();

        // ── 能力注册表 ──────────────────────────────────────────
        // 注意：cua.getCapabilities 需要读取注册表本身，因此分两步——
        // 先注册普通能力，再把"导出能力清单"这个能力注册进去。
        services.AddSingleton<CapabilityRegistry>(sp =>
        {
            var registry = new CapabilityRegistry()
                .Register(new CaptureGroupCapability(sp.GetRequiredService<ICaptureGroupService>()))
                .Register(new DeviceListCapability(sp.GetRequiredService<ICameraProvider>()))
                .Register(new SystemSelfCheckCapability(sp.GetRequiredService<ISelfCheck>()))
                .Register(new SystemGetMetricsCapability(sp.GetRequiredService<IMetrics>()))
                .Register(new SystemGetAuditLogCapability(sp.GetRequiredService<IAuditLog>()))
                .Register(new RobotGetStateCapability(sp.GetRequiredService<IRobot>()))
                .Register(new RobotStopCapability(sp.GetRequiredService<IRobot>()))
                .Register(new RobotMoveToCapability(sp.GetRequiredService<IRobot>()));

            registry.Register(new CuaGetCapabilitiesCapability(
                registry,
                sp.GetRequiredService<IClock>()));

            return registry;
        });

        services.AddSingleton<ICapabilityCatalog>(sp => sp.GetRequiredService<CapabilityRegistry>());

        // 确认门：只有宿主提供时才注册；未注册时 CommandBus 拿不到门 → 失败关闭
        if (confirmationGate is not null)
            services.AddSingleton(confirmationGate);

        services.AddSingleton<ICommandBus, CommandBus>();

        // ── AI / CUA ────────────────────────────────────────────
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<LlmSettingsStore>();
        services.AddSingleton<ILlmProvider>(sp =>
        {
            LlmSettingsStore store = sp.GetRequiredService<LlmSettingsStore>();
            LlmSettings settings = store.Load();
            string? apiKey = store.GetApiKey();

            // 未配置时给一个会明确报错的占位实现，而不是让 DI 装配直接失败
            return apiKey is null || string.IsNullOrWhiteSpace(settings.Model)
                ? new NotConfiguredLlmProvider()
                : new OpenAiCompatibleProvider(
                    settings.BaseUrl,
                    apiKey,
                    settings.Model,
                    settings.Temperature,
                    settings.TimeoutSeconds);
        });

        services.AddSingleton<ICommandBinder, DefaultCommandBinder>();
        services.AddSingleton<IAiAgent, CuaAgent>();

        return services.BuildServiceProvider();
    }

    private static void ConfigureSerilog(string logDirectory, CompositionOptions options)
    {
        LoggerConfiguration configuration = new LoggerConfiguration()
            .MinimumLevel.Is(ToSerilog(options.MinimumLogLevel))
            .Enrich.WithProperty("app", "CameraControl")
            .WriteTo.File(
                Path.Combine(logDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: false);

        if (options.ConsoleLogging)
            configuration = configuration.WriteTo.Console();

        Log.Logger = configuration.CreateLogger();
    }

    private static Serilog.Events.LogEventLevel ToSerilog(LogLevel level) => level switch
    {
        LogLevel.Debug => Serilog.Events.LogEventLevel.Debug,
        LogLevel.Information => Serilog.Events.LogEventLevel.Information,
        LogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
        LogLevel.Error => Serilog.Events.LogEventLevel.Error,
        _ => Serilog.Events.LogEventLevel.Information
    };
}
