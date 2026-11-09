namespace CameraControl.Domain.Calibration;

/// <summary>
/// 2D 单应变换（3×3，齐次坐标）。
///
/// 独立出来是因为它有两个用途：
/// 1. 标定核心：物平面 → 像平面（Zhang 第一步）；
/// 2. 点阵整理：格点索引 → 像平面（吸附与离群点剔除）。
/// </summary>
public sealed class Homography
{
    private readonly double[,] _m;

    private Homography(double[,] matrix) => _m = matrix;

    /// <summary>3×3 矩阵（行优先，m[2,2] 已归一化为 1）。</summary>
    public double[,] Matrix => _m;

    /// <summary>
    /// 归一化 DLT 求单应：至少 4 对点。
    /// </summary>
    public static Homography? Compute(IReadOnlyList<Point2> from, IReadOnlyList<Point2> to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        int n = Math.Min(from.Count, to.Count);
        if (n < 4) return null;

        // 退化检测：点（近似）共线时单应不唯一，必须明确失败而不是返回任意矩阵
        if (!HasSpread(from, n) || !HasSpread(to, n)) return null;

        (double[,] tFrom, double[] ax, double[] ay) = Normalize(from, n);
        (double[,] tTo, double[] bx, double[] by) = Normalize(to, n);

        var ata = new double[9, 9];

        for (int k = 0; k < n; k++)
        {
            double x = ax[k], y = ay[k], u = bx[k], v = by[k];

            double[] row1 = [-x, -y, -1, 0, 0, 0, u * x, u * y, u];
            double[] row2 = [0, 0, 0, -x, -y, -1, v * x, v * y, v];

            for (int r = 0; r < 9; r++)
            {
                for (int c = 0; c < 9; c++)
                    ata[r, c] += (row1[r] * row1[c]) + (row2[r] * row2[c]);
            }
        }

        double[]? hn = SymmetricEigen.SmallestEigenvector(ata);
        if (hn is null) return null;

        double[,]? tToInverse = Invert3(tTo);
        if (tToInverse is null) return null;

        double[,] h = SymmetricEigen.Multiply3(
            SymmetricEigen.Multiply3(tToInverse, SymmetricEigen.ToMatrix3(hn)),
            tFrom);

        // 归一化到 m[2,2]=1；若接近 0 则按范数归一
        double scale = h[2, 2];
        if (Math.Abs(scale) < 1e-12)
        {
            double norm = 0;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) norm += h[i, j] * h[i, j];

            norm = Math.Sqrt(norm);
            if (norm < 1e-15) return null;
            scale = norm;
        }

        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) h[i, j] /= scale;

        return new Homography(h);
    }

    /// <summary>应用变换。</summary>
    public Point2 Apply(Point2 p)
    {
        double w = (_m[2, 0] * p.X) + (_m[2, 1] * p.Y) + _m[2, 2];
        if (Math.Abs(w) < 1e-12) return new Point2(double.NaN, double.NaN);

        return new Point2(
            ((_m[0, 0] * p.X) + (_m[0, 1] * p.Y) + _m[0, 2]) / w,
            ((_m[1, 0] * p.X) + (_m[1, 1] * p.Y) + _m[1, 2]) / w);
    }

    /// <summary>求逆；退化返回 null。</summary>
    public Homography? Inverse()
    {
        double[,]? inverse = Invert3(_m);
        return inverse is null ? null : new Homography(inverse);
    }

    /// <summary>
    /// 判断点集是否有二维展开（非共线）。
    ///
    /// 用协方差矩阵的各向同性度量：4·det / trace²。
    /// 各向同性分布 ≈ 1，完全共线 → 0。阈值取 1e-4，能挡住"差不多一条线"的退化配置。
    /// </summary>
    private static bool HasSpread(IReadOnlyList<Point2> points, int count)
    {
        double meanX = 0, meanY = 0;
        for (int i = 0; i < count; i++)
        {
            meanX += points[i].X;
            meanY += points[i].Y;
        }

        meanX /= count;
        meanY /= count;

        double sxx = 0, syy = 0, sxy = 0;
        for (int i = 0; i < count; i++)
        {
            double dx = points[i].X - meanX;
            double dy = points[i].Y - meanY;

            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
        }

        double trace = sxx + syy;
        if (trace <= 1e-18) return false;

        double determinant = (sxx * syy) - (sxy * sxy);
        double isotropy = 4 * determinant / (trace * trace);

        return isotropy > 1e-4;
    }

    private static (double[,] Transform, double[] X, double[] Y) Normalize(
        IReadOnlyList<Point2> points,
        int count)
    {
        double meanX = 0, meanY = 0;
        for (int i = 0; i < count; i++)
        {
            meanX += points[i].X;
            meanY += points[i].Y;
        }

        meanX /= count;
        meanY /= count;

        double meanDistance = 0;
        for (int i = 0; i < count; i++)
        {
            double dx = points[i].X - meanX;
            double dy = points[i].Y - meanY;
            meanDistance += Math.Sqrt((dx * dx) + (dy * dy));
        }

        meanDistance /= count;

        double scale = meanDistance < 1e-12 ? 1.0 : Math.Sqrt(2) / meanDistance;

        var transform = new double[3, 3];
        transform[0, 0] = scale;
        transform[1, 1] = scale;
        transform[0, 2] = -scale * meanX;
        transform[1, 2] = -scale * meanY;
        transform[2, 2] = 1;

        var xs = new double[count];
        var ys = new double[count];

        for (int i = 0; i < count; i++)
        {
            xs[i] = (scale * points[i].X) - (scale * meanX);
            ys[i] = (scale * points[i].Y) - (scale * meanY);
        }

        return (transform, xs, ys);
    }

    internal static double[,]? Invert3(double[,] m)
    {
        double det =
            (m[0, 0] * ((m[1, 1] * m[2, 2]) - (m[1, 2] * m[2, 1])))
            - (m[0, 1] * ((m[1, 0] * m[2, 2]) - (m[1, 2] * m[2, 0])))
            + (m[0, 2] * ((m[1, 0] * m[2, 1]) - (m[1, 1] * m[2, 0])));

        if (Math.Abs(det) < 1e-15) return null;

        double inv = 1.0 / det;
        var r = new double[3, 3];

        r[0, 0] = ((m[1, 1] * m[2, 2]) - (m[1, 2] * m[2, 1])) * inv;
        r[0, 1] = ((m[0, 2] * m[2, 1]) - (m[0, 1] * m[2, 2])) * inv;
        r[0, 2] = ((m[0, 1] * m[1, 2]) - (m[0, 2] * m[1, 1])) * inv;
        r[1, 0] = ((m[1, 2] * m[2, 0]) - (m[1, 0] * m[2, 2])) * inv;
        r[1, 1] = ((m[0, 0] * m[2, 2]) - (m[0, 2] * m[2, 0])) * inv;
        r[1, 2] = ((m[0, 2] * m[1, 0]) - (m[0, 0] * m[1, 2])) * inv;
        r[2, 0] = ((m[1, 0] * m[2, 1]) - (m[1, 1] * m[2, 0])) * inv;
        r[2, 1] = ((m[0, 1] * m[2, 0]) - (m[0, 0] * m[2, 1])) * inv;
        r[2, 2] = ((m[0, 0] * m[1, 1]) - (m[0, 1] * m[1, 0])) * inv;

        return r;
    }
}
