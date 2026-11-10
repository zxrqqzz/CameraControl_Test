namespace CameraControl.Infrastructure.YmConnect;

/// <summary>
/// 机器人连接配置。
///
/// 端口由 YMConnect（YCP 协议）固定，只需 IP——与旧项目一致。
/// </summary>
public sealed class RobotSettings
{
    /// <summary>机器人 IP（不给就视为未配置，不会尝试连接）。</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>运动速度（mm/s，直线运动用）。</summary>
    public double SpeedMmPerSec { get; set; } = 25;

    /// <summary>连接接收超时（毫秒）。旧项目实测用 3000。</summary>
    public int ReceiveTimeoutMs { get; set; } = 3000;

    /// <summary>发送重试次数。旧项目实测用 3。</summary>
    public int SendRetries { get; set; } = 3;

    /// <summary>
    /// 软限位（工作空间保护）。null 表示该轴不限制。
    /// 单位与机器人一致（mm / 度）。
    /// TODO-REALDATA: 默认全 null（不限制）是**故意**的——
    /// 未经实测的工作空间参数写死在代码里比不限制更危险。
    /// </summary>
    public SoftLimitSet SoftLimits { get; set; } = new();

    /// <summary>是否已配置可用 IP。</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Ip);
}

/// <summary>软限位集合：每轴 min/max，null 表示不限制。</summary>
public sealed class SoftLimitSet
{
    public double? MinX { get; set; }
    public double? MaxX { get; set; }
    public double? MinY { get; set; }
    public double? MaxY { get; set; }
    public double? MinZ { get; set; }
    public double? MaxZ { get; set; }
    public double? MinRx { get; set; }
    public double? MaxRx { get; set; }
    public double? MinRy { get; set; }
    public double? MaxRy { get; set; }
    public double? MinRz { get; set; }
    public double? MaxRz { get; set; }

    /// <summary>是否配置了任何一条限位。</summary>
    public bool HasAny =>
        MinX is not null || MaxX is not null || MinY is not null || MaxY is not null ||
        MinZ is not null || MaxZ is not null || MinRx is not null || MaxRx is not null ||
        MinRy is not null || MaxRy is not null || MinRz is not null || MaxRz is not null;

    /// <summary>检查一个位姿是否越界，返回越界轴说明；空数组表示通过。</summary>
    public IReadOnlyList<string> Check(double x, double y, double z, double rx, double ry, double rz)
    {
        List<string> violations = [];

        Check("X", x, MinX, MaxX, violations);
        Check("Y", y, MinY, MaxY, violations);
        Check("Z", z, MinZ, MaxZ, violations);
        Check("Rx", rx, MinRx, MaxRx, violations);
        Check("Ry", ry, MinRy, MaxRy, violations);
        Check("Rz", rz, MinRz, MaxRz, violations);

        return violations;
    }

    private static void Check(string axis, double value, double? min, double? max, List<string> violations)
    {
        if (min is not null && value < min) violations.Add($"{axis}={value:F2} 小于下限 {min:F2}");
        if (max is not null && value > max) violations.Add($"{axis}={value:F2} 大于上限 {max:F2}");
    }
}

/// <summary>机器人配置存储（复用统一的 JSON 设置存储）。</summary>
public sealed class RobotSettingsStore
{
    private const string Key = "robot";

    private readonly Domain.Ports.ISettingsStore _store;

    public RobotSettingsStore(Domain.Ports.ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public RobotSettings Load() =>
        _store.TryLoad<RobotSettings>(Key, out RobotSettings? settings) && settings is not null
            ? settings
            : new RobotSettings();

    public void Save(RobotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _store.Save(Key, settings);
    }
}
