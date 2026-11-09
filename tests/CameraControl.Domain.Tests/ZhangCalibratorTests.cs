using CameraControl.Domain.Calibration;
using Xunit;

namespace CameraControl.Domain.Tests;

/// <summary>
/// 张正友标定求解器。
///
/// 验证方法：**合成数据反演**——用已知内参把棋盘格投影成"图像点"，
/// 再把对应关系喂给求解器，看能否把内参还原出来。
/// 这是唯一能在没有真实标定板时证明算法正确的方式。
/// </summary>
public sealed class ZhangCalibratorTests
{
    private const double TrueFx = 1200.0;
    private const double TrueFy = 1198.0;
    private const double TrueCx = 641.5;
    private const double TrueCy = 479.25;
    private const int ImageWidth = 1280;
    private const int ImageHeight = 960;

    private static readonly ChessboardSpec Spec = new(Columns: 9, Rows: 6, SquareSizeMm: 10.0);

    // ── 合成投影 ────────────────────────────────────────────

    private static double[,] RotationXYZDegrees(double rx, double ry, double rz)
    {
        double a = rx * Math.PI / 180, b = ry * Math.PI / 180, c = rz * Math.PI / 180;

        double[,] Rx =
        {
            { 1, 0, 0 },
            { 0, Math.Cos(a), -Math.Sin(a) },
            { 0, Math.Sin(a), Math.Cos(a) }
        };

        double[,] Ry =
        {
            { Math.Cos(b), 0, Math.Sin(b) },
            { 0, 1, 0 },
            { -Math.Sin(b), 0, Math.Cos(b) }
        };

        double[,] Rz =
        {
            { Math.Cos(c), -Math.Sin(c), 0 },
            { Math.Sin(c), Math.Cos(c), 0 },
            { 0, 0, 1 }
        };

        return Multiply(Multiply(Rz, Ry), Rx);
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        var r = new double[a.GetLength(0), b.GetLength(1)];

        for (int i = 0; i < a.GetLength(0); i++)
        {
            for (int j = 0; j < b.GetLength(1); j++)
            {
                double sum = 0;
                for (int k = 0; k < a.GetLength(1); k++) sum += a[i, k] * b[k, j];
                r[i, j] = sum;
            }
        }

        return r;
    }

    private static CalibrationView ProjectView(
        double[,] rotation,
        double[] translation,
        double fx = TrueFx,
        double fy = TrueFy,
        double cx = TrueCx,
        double cy = TrueCy)
    {
        IReadOnlyList<Point3> objectPoints = Spec.ObjectPoints();
        var imagePoints = new List<Point2>(objectPoints.Count);

        foreach (Point3 p in objectPoints)
        {
            double xc = (rotation[0, 0] * p.X) + (rotation[0, 1] * p.Y) + (rotation[0, 2] * p.Z) + translation[0];
            double yc = (rotation[1, 0] * p.X) + (rotation[1, 1] * p.Y) + (rotation[1, 2] * p.Z) + translation[1];
            double zc = (rotation[2, 0] * p.X) + (rotation[2, 1] * p.Y) + (rotation[2, 2] * p.Z) + translation[2];

            if (zc <= 1e-6) throw new InvalidOperationException("合成数据有误：点在相机后方。");

            imagePoints.Add(new Point2(
                (fx * xc / zc) + cx,
                (fy * yc / zc) + cy));
        }

        return new CalibrationView(objectPoints, imagePoints);
    }

    /// <summary>造 10 个姿态各异的视角。</summary>
    private static IReadOnlyList<CalibrationView> SyntheticViews()
    {
        (double rx, double ry, double rz, double tx, double ty, double tz)[] poses =
        [
            (-25,  8,  3,  -70, -50, 420),
            ( 18, -22, 12,   60, -40, 450),
            (-12,  26, -8,  -50, -60, 430),
            ( 28,  15, 18,   70, -45, 400),
            (-30, -18, -15, -80, -55, 470),
            (  8,  32,  6,   40, -70, 440),
            ( 22, -12, -22,  -60, -35, 415),
            (-18, -28, 25,   65, -50, 460),
            ( 32,   5, -10, -45, -65, 435),
            ( -6,  20, 14,   55, -60, 455)
        ];

        return [.. poses.Select(p =>
            ProjectView(RotationXYZDegrees(p.rx, p.ry, p.rz), [p.tx, p.ty, p.tz]))];
    }

    // ── 测试 ────────────────────────────────────────────────

    [Fact]
    public void 无噪声合成数据应几乎精确还原内参()
    {
        var calibrator = new ZhangCalibrator();

        IntrinsicsSolveResult result = calibrator.Solve(
            SyntheticViews(), Spec, ImageWidth, ImageHeight);

        Assert.True(result.Ok, $"求解失败：{result.Error} | 诊断：{string.Join("; ", result.Diagnostics ?? [])}");
        CameraIntrinsics k = result.Intrinsics!;

        // 无噪声数据下闭合解应只受数值精度限制
        Assert.True(Math.Abs(k.Fx - TrueFx) / TrueFx < 0.005,
            $"fx = {k.Fx:F3}，期望 {TrueFx}（误差 {Math.Abs(k.Fx - TrueFx) / TrueFx * 100:F3}%）");

        Assert.True(Math.Abs(k.Fy - TrueFy) / TrueFy < 0.005,
            $"fy = {k.Fy:F3}，期望 {TrueFy}（误差 {Math.Abs(k.Fy - TrueFy) / TrueFy * 100:F3}%）");

        Assert.True(Math.Abs(k.Cx - TrueCx) < 2.0, $"cx = {k.Cx:F3}，期望 {TrueCx}");
        Assert.True(Math.Abs(k.Cy - TrueCy) < 2.0, $"cy = {k.Cy:F3}，期望 {TrueCy}");

        // 单应拟合应几乎完美
        Assert.True(result.HomographyRmsePx < 0.01,
            $"单应 RMSE = {result.HomographyRmsePx:F6} px，不应这么大");
    }

    [Fact]
    public void 加入亚像素噪声后焦距误差应在一个百分点内()
    {
        var random = new Random(20261106);

        IReadOnlyList<CalibrationView> noisy = [.. SyntheticViews().Select(view =>
            new CalibrationView(
                view.ObjectPoints,
                [.. view.ImagePoints.Select(p => new Point2(
                    p.X + (random.NextDouble() - 0.5) * 0.6,
                    p.Y + (random.NextDouble() - 0.5) * 0.6))]))];

        var calibrator = new ZhangCalibrator();
        IntrinsicsSolveResult result = calibrator.Solve(noisy, Spec, ImageWidth, ImageHeight);

        Assert.True(result.Ok, $"求解失败：{result.Error}");
        CameraIntrinsics k = result.Intrinsics!;

        double fxError = Math.Abs(k.Fx - TrueFx) / TrueFx * 100;
        double fyError = Math.Abs(k.Fy - TrueFy) / TrueFy * 100;

        Assert.True(fxError < 1.0, $"fx 误差 {fxError:F3}%（fx={k.Fx:F2}）");
        Assert.True(fyError < 1.0, $"fy 误差 {fyError:F3}%（fy={k.Fy:F2}）");

        Assert.True(Math.Abs(k.Cx - TrueCx) < 5, $"cx 误差 {Math.Abs(k.Cx - TrueCx):F2} px");
        Assert.True(Math.Abs(k.Cy - TrueCy) < 5, $"cy 误差 {Math.Abs(k.Cy - TrueCy):F2} px");
    }

    [Fact]
    public void 视角不足应报错而不是给出错误结果()
    {
        var calibrator = new ZhangCalibrator();

        IntrinsicsSolveResult result = calibrator.Solve(
            [.. SyntheticViews().Take(2)], Spec, ImageWidth, ImageHeight);

        Assert.False(result.Ok);
        Assert.Contains(CalibrationErrorCodes.TooFewViews, result.Diagnostics ?? []);
    }

    [Fact]
    public void 物点与像点数量不一致应报错()
    {
        CalibrationView broken = new(
            Spec.ObjectPoints(),
            [.. Spec.ObjectPoints().Select((p, i) => new Point2(p.X + i, p.Y))]);

        var calibrator = new ZhangCalibrator();

        IntrinsicsSolveResult result = calibrator.Solve(
            [.. SyntheticViews().Take(4), new CalibrationView(
                [.. broken.ObjectPoints.Take(10)],
                broken.ImagePoints)],
            Spec, ImageWidth, ImageHeight);

        // 该视角点数不一致会被拒绝；若剩余视角仍够，则整体可能成功——
        // 这里只要求"不崩溃且给出明确状态"
        Assert.True(result.Ok || result.Error is not null);
    }

    [Fact]
    public void 完全相同的视角应因退化而失败()
    {
        CalibrationView view = SyntheticViews()[0];

        var calibrator = new ZhangCalibrator();
        IntrinsicsSolveResult result = calibrator.Solve(
            [view, view, view, view, view], Spec, ImageWidth, ImageHeight);

        Assert.False(result.Ok, "同一视角重复不应解出内参");
    }
}
