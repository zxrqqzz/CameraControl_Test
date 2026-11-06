using CameraControl.Application.Auditing;
using CameraControl.Application.Capture;
using CameraControl.Application.Commands;
using CameraControl.Application.Capabilities;
using CameraControl.Domain.Ports;
using CameraControl.Domain.Privacy;
using CameraControl.Infrastructure;
using CameraControl.Infrastructure.Auditing;
using CameraControl.Infrastructure.Hik;
using CameraControl.Infrastructure.Privacy;
using CameraControl.Infrastructure.Readiness;
using CameraControl.Infrastructure.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace CameraControl.Composition;

/// <summary>
/// 组合根：把所有端口与适配器装配起来。
///
/// 这是**唯一**允许同时看到 Domain / Application / Infrastructure 的地方。
/// 其它宿主（WPF、CLI、诊断工具）都只调用这里，不自己 new 具体类型。
/// </summary>
public static class ServiceRegistration
{
    /// <summary>构造完整容器。</summary>
    /// <param name="auditDirectory">审计日志目录；null 用默认位置。</param>
    /// <param name="settingsDirectory">设置目录；null 用默认位置。</param>
    public static ServiceProvider Build(string? auditDirectory = null, string? settingsDirectory = null)
    {
        var services = new ServiceCollection();

        // ── 横切 ────────────────────────────────────────────────
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IAuditLog>(_ => new FileAuditLog(auditDirectory));
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(settingsDirectory));

        // ── 端口 → 适配器 ───────────────────────────────────────
        services.AddSingleton<ICameraProvider, HikCameraProvider>();
        services.AddSingleton<IImagePersistencePolicy, DefaultImagePersistencePolicy>();
        services.AddSingleton<SoftwareTriggerSource>();
        services.AddSingleton<ITriggerSourceFactory, TriggerSourceFactory>();

        // P0 就绪门为固定延时占位；P2 换 VisionStabilityGate
        services.AddSingleton<IReadinessGate, FixedDelayGate>();

        // ── 应用层 ──────────────────────────────────────────────
        services.AddSingleton<ICameraSessionRegistry, CameraSessionRegistry>();
        services.AddSingleton<ICaptureGroupService, CaptureGroupService>();
        services.AddSingleton<IRiskPolicy, DefaultRiskPolicy>();

        // ── 能力注册表 ──────────────────────────────────────────
        services.AddSingleton<CapabilityRegistry>(sp => new CapabilityRegistry()
            .Register(new CaptureGroupCapability(sp.GetRequiredService<ICaptureGroupService>()))
            .Register(new DeviceListCapability(sp.GetRequiredService<ICameraProvider>())));

        services.AddSingleton<ICapabilityCatalog>(sp => sp.GetRequiredService<CapabilityRegistry>());
        services.AddSingleton<ICommandBus, CommandBus>();

        return services.BuildServiceProvider();
    }
}
