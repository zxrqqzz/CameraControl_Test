using CameraControl.Domain.Calibration;
using Xunit;

namespace CameraControl.Domain.Tests;

/// <summary>
/// 联合光束法平差（BA）。
///
/// 与交替优化不同，BA 把相机参数与所有视角位姿放在同一个参数向量里一起优化，
/// 因此不存在"位姿误差被固化"的问题。
///
/// 验证方式仍是合成反演：已知内参 + 已知畸变投影，反解回去。
/// </summary>
public sealed class BundleAdjustmentCalibratorTests
{
    private const double TrueFx = 1100;
    private const double TrueFy = 1103;
    private const double TrueCx = 640;
    private const double TrueCy = 360;
    private const double TrueK1 = -0.22;
    private const double TrueK2 = 0.09;
    private const int Width = 1280;
    private const int Height = 720;
    private const int Cols = 9;
    private const int Rows = 6;
    private const double SquareMm = 25;

    private static readonly DistortionModel TrueDistortion = new(K1: TrueK1, K2: TrueK2);

    private static double[,] Rotation(double rx, double ry, double rz)
    {
        double a = rx * Math.PI / 180, b = ry * Math.PI / 180, c = rz * Math.PI / 180;

        return new double[,]
        {
            { Math.Cos(c) * Math.Cos(b), (Math.Cos(c) * Math.Sin(b) * Math.Sin(a)) - (Math.Sin(c) * Math.Cos(a)), (Math.Cos(c) * Math.Sin(b) * Math.Cos(a)) + (Math.Sin(c) * Math.Sin(a)) },
            { Math.Sin(c) * Math.Cos(b), (Math.Sin(c) * Math.Sin(b) * Math.Sin(a)) + (Math.Cos(c) * Math.Cos(a)), (Math.Sin(c) * Math.Sin(b) * Math.Cos(a)) - (Math.Cos(c) * Math.Sin(a)) },
            { -Math.Sin(b), Math.Cos(b) * Math.Sin(a), Math.Cos(b) * Math.Cos(a) }
        };
    }

    private static CalibrationView Project(
        double[,] rotation,
        double[] translation,
        ChessboardSpec spec,
        double noise,
        Random? random)
    {
        IReadOnlyList<Point3> objectPoints = spec.ObjectPoints();
        var imagePoints = new List<Point2>(objectPoints.Count);

        foreach (Point3 p in objectPoints)
        {
            double xc = (rotation[0, 0] * p.X) + (rotation[0, 1] * p.Y) + (rotation[0, 2] * p.Z) + translation[0];
            double yc = (rotation[1, 0] * p.X) + (rotation[1, 1] * p.Y) + (rotation[1, 2] * p.Z) + translation[1];
            double zc = (rotation[2, 0] * p.X) + (rotation[2, 1] * p.Y) + (rotation[2, 2] * p.Z) + translation[2];

            if (zc <= 1e-6) throw new InvalidOperationException("合成数据有误。");

            (double xd, double yd) = TrueDistortion.Distort(xc / zc, yc / zc);

            double u = (TrueFx * xd) + TrueCx;
            double v = (TrueFy * yd) + TrueCy;

            if (noise > 0 && random is not null)
            {
                u += (random.NextDouble() - 0.5) * noise * 2;
                v += (random.NextDouble() - 0.5) * noise * 2;
            }

            imagePoints.Add(new Point2(u, v));
        }

        return new CalibrationView(objectPoints, imagePoints);
    }

    private static IReadOnlyList<CalibrationView> BuildViews(double noise = 0)
    {
        // 深度刻意拉开：焦距与畸变才能在数学上分离
        (double rx, double ry, double rz, double tx, double ty, double tz)[] poses =
        [
            (-25, 8, 3, -60, -40, 420),
            (18, -22, 12, 55, -35, 1150),
            (-12, 26, -8, -50, -55, 620),
            (28, 15, 18, 60, -45, 900),
            (-30, -18, -15, -90, -60, 480),
            (8, 32, 6, 35, -60, 1050),
            (22, -12, -22, -55, -30, 700),
            (-18, -28, 25, 70, -55, 850),
            (32, 5, -10, -40, -65, 520),
            (-6, 20, 14, 45, -55, 1200)
        ];

        var spec = new ChessboardSpec(Cols, Rows, SquareMm);
        var random = new Random(20261109);
        var views = new List<CalibrationView>(poses.Length);

        foreach ((double rx, double ry, double rz, double tx, double ty, double tz) in poses)
            views.Add(Project(Rotation(rx, ry, rz), [tx, ty, tz], spec, noise, random));

        return views;
    }

    private static readonly ChessboardSpec Spec = new(Cols, Rows, SquareMm);

    [Fact]
    public void 无噪声时应精确还原内参与畸变()
    {
        var calibrator = new BundleAdjustmentCalibrator();
        IntrinsicsSolveResult result = calibrator.Solve(BuildViews(), Spec, Width, Height);

        Assert.True(result.Ok, $"求解失败：{result.Error}");
        CameraIntrinsics k = result.Intrinsics!;

        Assert.True(Math.Abs(k.Fx - TrueFx) / TrueFx < 0.005,
            $"fx={k.Fx:F2}（真值 {TrueFx}），RMSE={result.ReprojectionRmsePx:F4}px");
        Assert.True(Math.Abs(k.Fy - TrueFy) / TrueFy < 0.005, $"fy={k.Fy:F2}（真值 {TrueFy}）");
        Assert.True(Math.Abs(k.Cx - TrueCx) < 3, $"cx={k.Cx:F2}（真值 {TrueCx}）");
        Assert.True(Math.Abs(k.Cy - TrueCy) < 3, $"cy={k.Cy:F2}（真值 {TrueCy}）");

        Assert.NotNull(result.Distortion);
        Assert.True(Math.Abs(result.Distortion!.K1 - TrueK1) < 0.05,
            $"k1={result.Distortion.K1:F4}（真值 {TrueK1}）");

        Assert.True(result.ReprojectionRmsePx < 0.1,
            $"重投影 RMSE={result.ReprojectionRmsePx:F4}px，无噪声时应接近 0");
    }

    [Fact]
    public void 有噪声时应优于闭合解()
    {
        IReadOnlyList<CalibrationView> views = BuildViews(noise: 0.3);

        var closed = new ZhangCalibrator();
        IntrinsicsSolveResult closedResult = closed.Solve(views, Spec, Width, Height);

        var ba = new BundleAdjustmentCalibrator();
        IntrinsicsSolveResult baResult = ba.Solve(views, Spec, Width, Height);

        Assert.True(closedResult.Ok && baResult.Ok);

        double closedError = Math.Abs(closedResult.Intrinsics!.Fx - TrueFx) / TrueFx;
        double baError = Math.Abs(baResult.Intrinsics!.Fx - TrueFx) / TrueFx;

        Assert.True(baError < closedError,
            $"BA 焦距误差 {baError * 100:F3}% 应小于闭合解 {closedError * 100:F3}%");
    }

    [Fact]
    public void 分半标定应一致()
    {
        IReadOnlyList<CalibrationView> views = BuildViews(noise: 0.3);
        var calibrator = new BundleAdjustmentCalibrator();

        var first = views.Where((_, i) => i % 2 == 0).ToList();
        var second = views.Where((_, i) => i % 2 == 1).ToList();

        IntrinsicsSolveResult a = calibrator.Solve(first, Spec, Width, Height);
        IntrinsicsSolveResult b = calibrator.Solve(second, Spec, Width, Height);

        Assert.True(a.Ok && b.Ok);

        double delta = Math.Abs(a.Intrinsics!.Fx - b.Intrinsics!.Fx) / a.Intrinsics.Fx * 100;

        Assert.True(delta < 2.0, $"两组 fx 差异 {delta:F2}%（{a.Intrinsics.Fx:F2} vs {b.Intrinsics.Fx:F2}）");
    }
}
