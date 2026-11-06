namespace CameraControl.Domain.Robotics;

/// <summary>
/// 机器人 6 轴位姿。
/// EyeInHand 形态下，采集组结果需要携带"采集时刻"的位姿，用于把图像坐标换算到机器人坐标系。
/// </summary>
/// <param name="X">X（mm）</param>
/// <param name="Y">Y（mm）</param>
/// <param name="Z">Z（mm）</param>
/// <param name="Rx">绕 X 旋转（度）</param>
/// <param name="Ry">绕 Y 旋转（度）</param>
/// <param name="Rz">绕 Z 旋转（度）</param>
/// <param name="UserFrame">用户坐标系编号（1–63）</param>
public sealed record RobotPose(
    double X,
    double Y,
    double Z,
    double Rx,
    double Ry,
    double Rz,
    int UserFrame = 1)
{
    /// <summary>到另一个位姿的三维直线距离（mm，忽略姿态差）。</summary>
    public double DistanceTo(RobotPose other)
    {
        ArgumentNullException.ThrowIfNull(other);
        double dx = X - other.X;
        double dy = Y - other.Y;
        double dz = Z - other.Z;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}
