using System.Security.Principal;
using CameraControl.Application.Diagnostics;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Ports;
using MvCameraControl;

namespace CameraControl.Infrastructure.Diagnostics;

/// <summary>
/// 进程权限检查。
///
/// **这是本项目最容易被误判的一条**：以管理员（高完整性）身份运行时，
/// Windows 会拒绝该进程访问摄像头（E_ACCESSDENIED 0x80070005），
/// 且不会有任何授权记录，表现为"设置里全开却打不开相机"。
/// </summary>
public sealed class ElevationProbe : IHealthProbe
{
    public string Name => "process.elevation";

    public Task<HealthItem> RunAsync(CancellationToken cancellationToken)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        bool isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

        HealthItem item = isAdmin
            ? new HealthItem(
                Name,
                HealthStatus.Fail,
                "当前以管理员身份运行。Windows 会拒绝高完整性进程访问摄像头（0x80070005）。" +
                "请以普通用户身份（直接双击）启动。",
                new Dictionary<string, string>
                {
                    ["isAdmin"] = "true",
                    ["user"] = identity.Name,
                    ["integrity"] = "High"
                })
            : new HealthItem(
                Name,
                HealthStatus.Ok,
                "以普通用户运行，摄像头可访问。",
                new Dictionary<string, string>
                {
                    ["isAdmin"] = "false",
                    ["user"] = identity.Name
                });

        return Task.FromResult(item);
    }
}

/// <summary>操作系统与运行时信息。信息性检查，始终 Ok。</summary>
public sealed class OperatingSystemProbe : IHealthProbe
{
    public string Name => "system.os";

    public Task<HealthItem> RunAsync(CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>
        {
            ["os"] = Environment.OSVersion.VersionString,
            ["is64BitProcess"] = Environment.Is64BitProcess ? "true" : "false",
            ["is64BitOS"] = Environment.Is64BitOperatingSystem ? "true" : "false",
            ["arch"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["runtime"] = Environment.Version.ToString(),
            ["uiCulture"] = System.Globalization.CultureInfo.CurrentUICulture.Name
        };

        HealthStatus status = Environment.Is64BitProcess ? HealthStatus.Ok : HealthStatus.Warn;
        string message = Environment.Is64BitProcess
            ? "运行环境正常。"
            : "进程是 32 位，但海康 SDK 是 AMD64 原生库，可能导致加载失败。";

        return Task.FromResult(new HealthItem(Name, status, message, details));
    }
}

/// <summary>存储可写性与磁盘空间。</summary>
public sealed class StorageProbe : IHealthProbe
{
    private readonly string _directory;

    public StorageProbe(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    public string Name => "storage.settings";

    public Task<HealthItem> RunAsync(CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string> { ["directory"] = _directory };

        try
        {
            Directory.CreateDirectory(_directory);

            string probePath = Path.Combine(_directory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probePath, "ok");
            File.Delete(probePath);

            details["writable"] = "true";

            string root = Path.GetPathRoot(_directory) ?? string.Empty;
            if (root.Length > 0)
            {
                var drive = new DriveInfo(root);
                double freeGb = drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
                details["freeGb"] = freeGb.ToString("F1");

                // TODO-REALDATA: 1GB 告警阈值是**经验值，未按本项目实际数据量核算**。
                // 应改为基于"日志保留 14 天 + 标定数据 + 采集记录"的实测日均增长来定。
                if (freeGb < 1)
                {
                    return Task.FromResult(new HealthItem(
                        Name,
                        HealthStatus.Warn,
                        $"磁盘剩余空间偏低（{freeGb:F1} GB），可能影响日志与标定数据保存。",
                        details));
                }
            }

            return Task.FromResult(new HealthItem(Name, HealthStatus.Ok, "配置目录可写，磁盘空间充足。", details));
        }
        catch (Exception ex)
        {
            details["writable"] = "false";
            details["error"] = ex.Message;

            return Task.FromResult(new HealthItem(
                Name,
                HealthStatus.Fail,
                $"配置目录不可写：{ex.Message}",
                details));
        }
    }
}

/// <summary>海康 MVS SDK 可用性。</summary>
public sealed class HikSdkProbe : IHealthProbe
{
    public string Name => "sdk.hikvision";

    public Task<HealthItem> RunAsync(CancellationToken cancellationToken)
    {
        int ret = Hik.HikSdkRuntime.Acquire();
        try
        {
            bool ok = ret == MvError.MV_OK;

            return Task.FromResult(new HealthItem(
                Name,
                ok ? HealthStatus.Ok : HealthStatus.Fail,
                ok
                    ? "海康 MVS SDK 初始化成功。"
                    : $"海康 MVS SDK 初始化失败：0x{ret:X}。请确认已安装 MVS 客户端（含原生驱动）。",
                new Dictionary<string, string>
                {
                    ["initialized"] = ok ? "true" : "false",
                    ["errorCode"] = $"0x{ret:X}"
                }));
        }
        finally
        {
            if (ret == MvError.MV_OK) Hik.HikSdkRuntime.Release();
        }
    }
}

/// <summary>相机枚举检查。</summary>
public sealed class CameraProbe : IHealthProbe
{
    private readonly ICameraProvider _provider;

    public CameraProbe(ICameraProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public string Name => "cameras.available";

    public async Task<HealthItem> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<CameraDescriptor> devices = await _provider
                .DiscoverAsync(cancellationToken)
                .ConfigureAwait(false);

            int capturable = devices.Count(d => d.CanCapture);

            var details = new Dictionary<string, string>
            {
                ["total"] = devices.Count.ToString(),
                ["capturable"] = capturable.ToString(),
                ["devices"] = string.Join("; ", devices.Select(d => $"{d.DisplayName}({d.Kind})"))
            };

            return capturable > 0
                ? new HealthItem(Name, HealthStatus.Ok, $"发现 {capturable} 台可采集相机。", details)
                : new HealthItem(Name, HealthStatus.Warn, "没有发现可采集的相机（红外设备不计入）。", details);
        }
        catch (Exception ex)
        {
            return new HealthItem(
                Name,
                HealthStatus.Warn,
                $"相机枚举失败：{ex.Message}",
                new Dictionary<string, string> { ["error"] = ex.Message });
        }
    }
}
