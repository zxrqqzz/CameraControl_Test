namespace CameraControl.Domain.Calibration;

/// <summary>
/// 镜头畸变模型（Brown-Conrady / OpenCV 约定）。
///
/// 归一化坐标 (x, y) → 畸变后 (x', y')：
/// <code>
/// r² = x² + y²
/// radial   = 1 + k1·r² + k2·r⁴ + k3·r⁶
/// x' = x·radial + 2·p1·x·y + p2·(r² + 2x²)
/// y' = y·radial + p1·(r² + 2y²) + 2·p2·x·y
/// </code>
/// 与 OpenCV 的 <c>distCoeffs = [k1, k2, p1, p2, k3]</c> 一致，便于与其它工具对照。
/// </summary>
/// <param name="K1">径向一阶。</param>
/// <param name="K2">径向二阶。</param>
/// <param name="P1">切向一阶。</param>
/// <param name="P2">切向二阶。</param>
/// <param name="K3">径向三阶（默认不估计）。</param>
public sealed record DistortionModel(
    double K1 = 0,
    double K2 = 0,
    double P1 = 0,
    double P2 = 0,
    double K3 = 0)
{
    public static readonly DistortionModel Zero = new();

    public bool IsZero => K1 == 0 && K2 == 0 && P1 == 0 && P2 == 0 && K3 == 0;

    /// <summary>归一化坐标 → 畸变后归一化坐标。</summary>
    public (double X, double Y) Distort(double x, double y)
    {
        double r2 = (x * x) + (y * y);
        double radial = 1 + (K1 * r2) + (K2 * r2 * r2) + (K3 * r2 * r2 * r2);

        double xd = (x * radial) + (2 * P1 * x * y) + (P2 * (r2 + (2 * x * x)));
        double yd = (y * radial) + (P1 * (r2 + (2 * y * y))) + (2 * P2 * x * y);

        return (xd, yd);
    }

    /// <summary>
    /// 畸变后归一化坐标 → 无畸变归一化坐标（牛顿迭代，5 次足够）。
    /// </summary>
    public (double X, double Y) Undistort(double xd, double yd)
    {
        double x = xd;
        double y = yd;

        for (int i = 0; i < 5; i++)
        {
            double r2 = (x * x) + (y * y);
            double radial = 1 + (K1 * r2) + (K2 * r2 * r2) + (K3 * r2 * r2 * r2);

            double dx = (2 * P1 * x * y) + (P2 * (r2 + (2 * x * x)));
            double dy = (P1 * (r2 + (2 * y * y))) + (2 * P2 * x * y);

            x = (xd - dx) / radial;
            y = (yd - dy) / radial;
        }

        return (x, y);
    }

    public override string ToString() =>
        $"k1={K1:F6} k2={K2:F6} p1={P1:F6} p2={P2:F6} k3={K3:F6}";
}
