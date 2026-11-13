using CameraControl.Application.Auditing;
using CameraControl.Application.Capabilities;
using CameraControl.Application.Capture;
using CameraControl.Application.Comm;
using CameraControl.Application.Commands;
using CameraControl.Application.Cua;
using CameraControl.Application.Diagnostics;
using CameraControl.Domain.Calibration;
using CameraControl.Domain.Ports;
using CameraControl.Domain.Privacy;
using CameraControl.Domain.Robotics;
using CameraControl.Infrastructure;
using CameraControl.Application.Cua.Context;
using CameraControl.Infrastructure.Context;
using CameraControl.Infrastructure.Ai;
using CameraControl.Infrastructure.Auditing;
using CameraControl.Infrastructure.Cameras;
using CameraControl.Infrastructure.Diagnostics;
using CameraControl.Infrastructure.Hik;
using CameraControl.Infrastructure.Logging;
using CameraControl.Infrastructure.Privacy;
using CameraControl.Infrastructure.Readiness;
using CameraControl.Infrastructure.Storage;
using CameraControl.Infrastructure.Triggers;
using CameraControl.Infrastructure.Uvc;
using CameraControl.Infrastructure.YmConnect;
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
    private static string? _resolvedDataDirectory;

    /// <summary>
    /// 解析数据目录（公开出来便于宿主显示"日志在哪"）。
    ///
    /// **默认放在程序旁边**（可移植，不写 C 盘用户目录）。
    /// 实测有机器上 `%LocalAppData%\CameraControl` 的 ACL 完全正常
    /// （用户 FullControl、可继承），但进程连随机名文件都建不了——
    /// 属于外部拦截（安全软件按进程拦写入），换到程序目录通常就通了。
    ///
    /// 顺序：
    /// 1. 仓库根下的 <c>data</c>（源码运行时；让主程序与 Agent CLI **共用**同一份配置）
    /// 2. 可执行文件旁的 <c>data</c>（发布后的绿色版布局）
    /// 3. LocalAppData → 换名 → Roaming → ProgramData → TEMP（逐级兜底）
    ///
    /// 每个候选都用**真实写一个随机文件**来验证，不只看权限位。
    /// </summary>
    /// <summary>
    /// 解析 <c>Mods</c> 目录：优先仓库根（源码运行），否则可执行文件旁（发布版）。
    /// 开关文件是 <c>Mods/context/mod.json</c>；整个目录缺失即视为关闭该功能。
    /// </summary>
    public static string ResolveModsDirectory()
    {
        string? repositoryRoot = FindRepositoryRoot();

        return repositoryRoot is not null
            ? Path.Combine(repositoryRoot, "Mods")
            : Path.Combine(AppContext.BaseDirectory, "Mods");
    }

    public static string ResolveDataDirectory(string? dataDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectory)) return dataDirectory;

        if (_resolvedDataDirectory is not null) return _resolvedDataDirectory;

        var candidates = new List<string>();

        string? repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot is not null)
            candidates.Add(Path.Combine(repositoryRoot, "data"));

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "data"));

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        candidates.Add(Path.Combine(local, "CameraControl"));
        candidates.Add(Path.Combine(local, "CameraControl-Data"));
        candidates.Add(Path.Combine(roaming, "CameraControl"));
        candidates.Add(Path.Combine(programData, "CameraControl"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "CameraControl"));

        foreach (string candidate in candidates)
        {
            if (IsWritable(candidate))
            {
                _resolvedDataDirectory = candidate;
                return candidate;
            }
        }

        // 全都不行：仍返回首选，让错误信息指向它，而不是悄悄换地方
        _resolvedDataDirectory = candidates[0];
        return candidates[0];
    }

    /// <summary>
    /// 从可执行文件目录向上找仓库根（含 CameraControl.slnx）。
    /// 找到就用 <c>&lt;仓库&gt;\data</c>，让 WPF 与 Agent CLI 共用一份配置/日志。
    /// </summary>
    private static string? FindRepositoryRoot()
    {
        try
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            for (int depth = 0; depth < 8 && directory is not null; depth++)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CameraControl.slnx")))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }
        catch
        {
            // 找不到就当发布版处理
        }

        return null;
    }

    /// <summary>真实写一个探针文件来判断可写（只看权限位不可靠）。</summary>
    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            string probe = Path.Combine(directory, $"probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

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
        services.AddSingleton<ICalibrationStore>(_ => new JsonCalibrationStore(dataDirectory));

        // ── 端口 → 适配器 ───────────────────────────────────────
        // 相机：海康（GigE/USB3）+ UVC（本机摄像头）合并；单路失败不影响另一路
        services.AddSingleton<HikCameraProvider>();
        services.AddSingleton<UvcCameraProvider>();
        services.AddSingleton<ICameraProvider>(sp => new CompositeCameraProvider(
            sp.GetRequiredService<HikCameraProvider>(),
            sp.GetRequiredService<UvcCameraProvider>()));

        services.AddSingleton<IImagePersistencePolicy, DefaultImagePersistencePolicy>();
        services.AddSingleton<SoftwareTriggerSource>();
        services.AddSingleton<ITriggerSourceFactory, TriggerSourceFactory>();

        // P0/P1 就绪门为固定延时占位；P2 换 VisionStabilityGate
        services.AddSingleton<IReadinessGate, FixedDelayGate>();

        // ── 机器人：安川 Motoman（YMConnect）───────────────────
        // 未配置 IP 时适配器自身会返回明确的"未连接"，不会尝试联网。
        services.AddSingleton<RobotSettingsStore>();
        services.AddSingleton<IRobotSafetyLimits, SettingsRobotSafetyLimits>();
        services.AddSingleton<IDeviceAliasStore, DeviceAliasStore>();
        services.AddSingleton<ICuaAuthorization, CuaAuthorization>();

        // 标定会话（交互式采图 → 求解 → 落盘）
        services.AddSingleton<Application.Calibration.ICalibrationSession, Application.Calibration.CalibrationSessionService>();
        services.AddSingleton<ICalibrationStatistics, CalibrationStatisticsService>();
        services.AddSingleton<IRobot>(sp => new YmConnectRobot(
            sp.GetRequiredService<RobotSettingsStore>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IAppLogger>()));

        // ── 应用层 ──────────────────────────────────────────────
        services.AddSingleton<ICameraSessionRegistry, CameraSessionRegistry>();
        services.AddSingleton<ICaptureStatistics, CaptureStatisticsService>();
        services.AddSingleton<ICapturePreviewService, CapturePreviewService>();
        services.AddSingleton<IFrameEncoder, Infrastructure.Imaging.PngFrameEncoder>();

        // 光源与通讯：暂未接入驱动（失败关闭，给出明确错误而不是静默成功）
        services.AddSingleton<ILightPort, Infrastructure.Light.UnavailableLightPort>();
        services.AddSingleton<ICommChannelFactory, Infrastructure.Light.UnavailableCommChannelFactory>();
        services.AddSingleton<ICommManager, CommManager>();
        services.AddSingleton<ICaptureGroupService, CaptureGroupService>();
        services.AddSingleton<IRiskPolicy, DefaultRiskPolicy>();
                // 联合 BA（相机参数 + 全部位姿一起优化）已通过合成数据验证；
        // 交替优化器 RefiningCalibrator 仍有缺陷，这里改用 BA。详见 docs/09。
        services.AddSingleton<Domain.Calibration.ICameraCalibrator, Domain.Calibration.BundleAdjustmentCalibrator>();

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
        .Register(new CameraOpenCapability(
            sp.GetRequiredService<ICameraSessionRegistry>(),
            sp.GetRequiredService<ICameraProvider>()))
        .Register(new CameraCloseCapability(sp.GetRequiredService<ICameraSessionRegistry>()))
        .Register(new CameraListOpenCapability(sp.GetRequiredService<ICameraSessionRegistry>()))
        .Register(new CameraGetStatusCapability(sp.GetRequiredService<ICameraSessionRegistry>()))
        .Register(new CaptureSingleCapability(
            sp.GetRequiredService<ICameraSessionRegistry>(),
            sp.GetRequiredService<ICaptureStatistics>(),
            sp.GetRequiredService<IFrameEncoder>()))
        .Register(new CaptureArmCapability(sp.GetRequiredService<ICameraSessionRegistry>()))
        .Register(new CaptureDisarmCapability(sp.GetRequiredService<ICameraSessionRegistry>()))
        .Register(new CaptureTriggerCapability(sp.GetRequiredService<ICameraSessionRegistry>()))
        .Register(new CaptureWaitSettledCapability(sp.GetRequiredService<IReadinessGate>()))
        .Register(new CaptureGetStatsCapability(sp.GetRequiredService<ICaptureStatistics>()))
        .Register(new CaptureStartContinuousCapability(sp.GetRequiredService<ICapturePreviewService>()))
        .Register(new CaptureStopContinuousCapability(sp.GetRequiredService<ICapturePreviewService>()))
        .Register(new LightOnCapability(sp.GetRequiredService<ILightPort>()))
        .Register(new LightOffCapability(sp.GetRequiredService<ILightPort>()))
        .Register(new LightSetBrightnessCapability(sp.GetRequiredService<ILightPort>()))
        .Register(new LightPulseCapability(sp.GetRequiredService<ILightPort>()))
        .Register(new LightStrobeCapability(sp.GetRequiredService<ILightPort>()))
        .Register(new LightGetStateCapability(sp.GetRequiredService<ILightPort>()))
        .Register(new LightListCapability(sp.GetRequiredService<ILightPort>()))
        .Register(new CommAddCapability(sp.GetRequiredService<ICommManager>()))
        .Register(new CommRemoveCapability(sp.GetRequiredService<ICommManager>()))
        .Register(new CommListCapability(sp.GetRequiredService<ICommManager>()))
        .Register(new CommOpenCapability(sp.GetRequiredService<ICommManager>()))
        .Register(new CommCloseCapability(sp.GetRequiredService<ICommManager>()))
        .Register(new CommSendCapability(sp.GetRequiredService<ICommManager>()))
        .Register(new CommGetLogCapability(sp.GetRequiredService<ICommManager>()))
        .Register(new CaptureFrameCapability(
            sp.GetRequiredService<ICapturePreviewService>(),
            sp.GetRequiredService<IFrameEncoder>()))
                .Register(new DeviceListCapability(sp.GetRequiredService<ICameraProvider>()))
                .Register(new SystemSelfCheckCapability(sp.GetRequiredService<ISelfCheck>()))
                .Register(new SystemGetMetricsCapability(sp.GetRequiredService<IMetrics>()))
                .Register(new SystemGetAuditLogCapability(sp.GetRequiredService<IAuditLog>()))
                .Register(new DeviceGetCapability(
            sp.GetRequiredService<ICameraProvider>(),
            sp.GetRequiredService<IDeviceAliasStore>()))
        .Register(new DeviceRenameCapability(sp.GetRequiredService<IDeviceAliasStore>()))
        .Register(new SystemGetEnvironmentCapability(sp.GetRequiredService<IClock>()))
        .Register(new SystemGetSettingsCapability())
        .Register(new SystemExportDiagnosticsCapability())
        .Register(new CuaGetAuthorizationModeCapability(sp.GetRequiredService<ICuaAuthorization>()))
        .Register(new CuaStopCapability(
            sp.GetRequiredService<ICuaAuthorization>(),
            sp.GetRequiredService<ICapturePreviewService>()))
        .Register(new CuaRequestElevationCapability())
        .Register(new CalibrationDetectCornersCapability(
            sp.GetRequiredService<ICameraSessionRegistry>(),
            sp.GetRequiredService<Application.Calibration.ICalibrationSession>(),
            sp.GetRequiredService<ICalibrationStatistics>()))
        .Register(new CalibrationGetStateCapability(sp.GetRequiredService<Application.Calibration.ICalibrationSession>()))
        .Register(new CalibrationConfigureCapability(sp.GetRequiredService<Application.Calibration.ICalibrationSession>()))
        .Register(new CalibrationAddPointCapability(sp.GetRequiredService<Application.Calibration.ICalibrationSession>()))
        .Register(new CalibrationRemovePointCapability(sp.GetRequiredService<Application.Calibration.ICalibrationSession>()))
        .Register(new CalibrationResetCapability(sp.GetRequiredService<Application.Calibration.ICalibrationSession>()))
        .Register(new CalibrationApplyCapability(
            sp.GetRequiredService<Application.Calibration.ICalibrationSession>(),
            sp.GetRequiredService<Domain.Calibration.ICalibrationStore>()))
        .Register(new CalibrationExportCapability(sp.GetRequiredService<Domain.Calibration.ICalibrationStore>()))
        .Register(new CalibrationSolveHandEyeCapability())
        .Register(new RobotGetStateCapability(sp.GetRequiredService<IRobot>()))
        .Register(new RobotGetPoseCapability(sp.GetRequiredService<IRobot>()))
        .Register(new RobotMoveRelativeCapability(
            sp.GetRequiredService<IRobot>(),
            sp.GetRequiredService<IRobotSafetyLimits>()))
        .Register(new RobotCheckSoftLimitCapability(
            sp.GetRequiredService<IRobot>(),
            sp.GetRequiredService<IRobotSafetyLimits>()))
        .Register(new RobotGetUserFrameCapability())
        .Register(new RobotSetUserFrameCapability())
                .Register(new RobotStopCapability(sp.GetRequiredService<IRobot>()))
                .Register(new RobotMoveToCapability(sp.GetRequiredService<IRobot>()))
                .Register(new CalibrationGetCapability(sp.GetRequiredService<ICalibrationStore>()))
                .Register(new CalibrationListCapability(sp.GetRequiredService<ICalibrationStore>()))
                .Register(new CalibrationRemoveCapability(sp.GetRequiredService<ICalibrationStore>()))
                .Register(new CalibrationSolveCapability(
                    sp.GetRequiredService<Domain.Calibration.ICameraCalibrator>(),
                    sp.GetRequiredService<ICalibrationStore>(),
                    sp.GetRequiredService<IClock>()));

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
        // ── 上下文（Mods/context/mod.json 开关）──────────────
        // 不做 DLL 反射加载：只需要"能关、能删干净"，
        // 而插件一多，反射扫描会拖慢启动。
        string modsDirectory = ResolveModsDirectory();
        ContextModSettings contextMods = ContextModSettings.Load(modsDirectory);

        services.AddSingleton(contextMods);

        services.AddSingleton<ITokenEstimator, CharacterTokenEstimator>();

        services.AddSingleton<IContextStore>(sp => new JsonlContextStore(
            Path.Combine(dataDirectory, "context"),
            contextMods.ToStoreOptions(),
            sp.GetRequiredService<ITokenEstimator>()));

        services.AddSingleton<LlmSettingsStore>();
        services.AddSingleton<ModelCatalogService>();
        services.AddSingleton<ProviderCatalog>();
        // 配置可切换：用路由提供方，每次调用按"当前生效配置"取实际实现，
        // 避免切换配置后仍然用旧的 provider 实例。
        services.AddSingleton<ILlmProvider, RoutingLlmProvider>();
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
