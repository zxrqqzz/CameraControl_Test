using CameraControl.Domain.Calibration;
using Xunit;

namespace CameraControl.Domain.Tests;

/// <summary>
/// 手眼标定 AX = XB。
///
/// 验证方式：选一个已知的 X（相机在法兰上的位姿），
/// 用随机机器人位姿和固定靶标反算出每个位姿下的"靶标→相机"变换，
/// 再喂给求解器看能否把 X 还原回来。
/// </summary>
public sealed class HandEyeCalibratorTests
{
    /// <summary>真值：相机装在法兰上，偏移 20/-15/80mm，绕各轴有小角度。</summary>
    private static readonly Transform3D TrueX = Transform3D.FromEulerDegrees(
        x: 20, y: -15, z: 80,
        rx: 5, ry: -8, rz: 12);

    /// <summary>靶标在机器人基座坐标系中的位置（固定不变）。</summary>
    private static readonly Transform3D TargetInBase = Transform3D.FromEulerDegrees(
        x: 500, y: 120, z: 40,
        rx: 0, ry: 0, rz: 30);

    /// <summary>造采样：给定机器人位姿，反算靶标→相机。</summary>
    private static HandEyeSample MakeSample(Transform3D gripperInBase)
    {
        // T_target→cam = (T_robot · X)⁻¹ · T_target_in_base
        Transform3D cameraInBase = gripperInBase.Multiply(TrueX);
        Transform3D targetToCamera = cameraInBase.Inverse().Multiply(TargetInBase);

        return new HandEyeSample(gripperInBase, targetToCamera);
    }

    private static IReadOnlyList<HandEyeSample> MakeSamples()
    {
        // 姿态必须"旋转差异大"，否则 AX=XB 退化
        (double x, double y, double z, double rx, double ry, double rz)[] poses =
        [
            (400, 100, 300, 0, 0, 0),
            (430, 130, 320, 15, -10, 20),
            (380, 90, 280, -20, 12, -25),
            (450, 150, 350, 25, 20, 35),
            (360, 70, 260, -30, -18, -40),
            (420, 120, 310, 10, 30, 15),
            (390, 80, 270, -15, 25, -30),
            (440, 140, 330, 20, -25, 28),
            (370, 110, 290, -25, 8, -18),
            (410, 95, 305, 12, -20, 22)
        ];

        return [.. poses.Select(p => MakeSample(Transform3D.FromEulerDegrees(
            p.x, p.y, p.z, p.rx, p.ry, p.rz)))];
    }

    [Fact]
    public void 无噪声时应还原相机在法兰上的位姿()
    {
        HandEyeResult result = HandEyeCalibrator.Calibrate(MakeSamples());

        Assert.True(result.Ok, $"求解失败：{result.Error}");

        Transform3D x = result.CameraInGripper!;

        // 旋转误差
        double rotationError = HandEyeCalibrator.RotationAngleDegrees(
            x.Rotation);

        // 与真值比较：X_true⁻¹ · X_est 的旋转角
        Transform3D delta = TrueX.Inverse().Multiply(x);
        double deltaAngle = HandEyeCalibrator.RotationAngleDegrees(delta.Rotation);

        double deltaTranslation = Math.Sqrt(
            (delta.Translation[0] * delta.Translation[0])
            + (delta.Translation[1] * delta.Translation[1])
            + (delta.Translation[2] * delta.Translation[2]));

        Assert.True(deltaAngle < 0.1, $"旋转误差 {deltaAngle:F4}°（X 自身旋转角 {rotationError:F2}°）");
        Assert.True(deltaTranslation < 0.1, $"平移误差 {deltaTranslation:F4} mm");
    }

    [Fact]
    public void 残差应接近零()
    {
        HandEyeResult result = HandEyeCalibrator.Calibrate(MakeSamples());

        Assert.True(result.Ok);
        Assert.True(result.RotationErrorDeg < 0.05, $"旋转残差 {result.RotationErrorDeg:F5}°");
        Assert.True(result.TranslationErrorMm < 0.05, $"平移残差 {result.TranslationErrorMm:F5} mm");
        Assert.True(result.PairCount > 20, $"有效运动对只有 {result.PairCount} 个");
    }

    [Fact]
    public void 采样不足应明确失败()
    {
        HandEyeResult result = HandEyeCalibrator.Calibrate([MakeSample(Transform3D.Identity)]);

        Assert.False(result.Ok);
        Assert.Contains("至少需要", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void 机器人不动时应因缺乏旋转信息而失败()
    {
        // 所有位姿相同 → 相对旋转为 0 → 无法解出手眼
        Transform3D fixedPose = Transform3D.FromEulerDegrees(400, 100, 300, 0, 0, 0);

        var samples = new List<HandEyeSample>();
        for (int i = 0; i < 5; i++) samples.Add(MakeSample(fixedPose));

        HandEyeResult result = HandEyeCalibrator.Calibrate(samples);

        Assert.False(result.Ok);
        Assert.Contains("旋转", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void 欧拉角与四元数应互逆()
    {
        Transform3D t = Transform3D.FromEulerDegrees(10, 20, 30, 15, -25, 35);

        double[] q = t.ToQuaternion();
        double[,] back = Transform3D.QuaternionToMatrix(q);

        double error = HandEyeCalibrator.RotationAngleDegrees(
            Transform3D.Multiply(Transform3D.Transpose(t.Rotation), back));

        Assert.True(error < 1e-6, $"互转误差 {error:E3}°");
    }

    [Fact]
    public void 逆变换应还原单位变换()
    {
        Transform3D t = Transform3D.FromEulerDegrees(100, -200, 300, 10, 20, 30);
        Transform3D identity = t.Inverse().Multiply(t);

        double angle = HandEyeCalibrator.RotationAngleDegrees(identity.Rotation);
        double norm = Math.Sqrt(
            (identity.Translation[0] * identity.Translation[0])
            + (identity.Translation[1] * identity.Translation[1])
            + (identity.Translation[2] * identity.Translation[2]));

        Assert.True(angle < 1e-6);
        Assert.True(norm < 1e-6);
    }
}
