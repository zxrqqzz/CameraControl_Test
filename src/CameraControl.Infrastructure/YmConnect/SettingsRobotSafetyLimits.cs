using CameraControl.Domain.Robotics;

namespace CameraControl.Infrastructure.YmConnect;

/// <summary>
/// 由配置驱动的软限位校验。
///
/// 未配置任何限位时 <see cref="HasLimits"/> 为 false，
/// 能力层会明确告知"未配置"而不是假装检查过。
/// </summary>
public sealed class SettingsRobotSafetyLimits : IRobotSafetyLimits
{
    private readonly RobotSettingsStore _settings;

    public SettingsRobotSafetyLimits(RobotSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public bool HasLimits => _settings.Load().SoftLimits.HasAny;

    public IReadOnlyList<string> Validate(RobotPose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);

        return _settings.Load().SoftLimits.Check(
            pose.X, pose.Y, pose.Z,
            pose.Rx, pose.Ry, pose.Rz);
    }
}
