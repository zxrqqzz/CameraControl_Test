namespace CameraControl.Domain.Calibration;

/// <summary>
/// 小规模稠密线性方程组求解（高斯消元 + 部分主元）。
///
/// 只用于 LM 的 8×8 正规方程，不追求通用性；
/// 秩不足（主元接近 0）时返回 null，由调用方加大阻尼重试。
/// </summary>
internal static class LinearSolver
{
    public static double[]? Solve(double[,] a, double[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        int n = b.Length;
        if (a.GetLength(0) != n || a.GetLength(1) != n) throw new ArgumentException("维度不匹配。");

        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();

        for (int col = 0; col < n; col++)
        {
            // 部分主元
            int pivot = col;
            double best = Math.Abs(m[col, col]);

            for (int row = col + 1; row < n; row++)
            {
                double value = Math.Abs(m[row, col]);
                if (value > best)
                {
                    best = value;
                    pivot = row;
                }
            }

            if (best < 1e-14) return null;

            if (pivot != col)
            {
                for (int k = 0; k < n; k++)
                    (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);

                (x[col], x[pivot]) = (x[pivot], x[col]);
            }

            double divisor = m[col, col];

            for (int row = col + 1; row < n; row++)
            {
                double factor = m[row, col] / divisor;
                if (factor == 0) continue;

                for (int k = col; k < n; k++)
                    m[row, k] -= factor * m[col, k];

                x[row] -= factor * x[col];
            }
        }

        // 回代
        var result = new double[n];

        for (int row = n - 1; row >= 0; row--)
        {
            double sum = x[row];

            for (int k = row + 1; k < n; k++)
                sum -= m[row, k] * result[k];

            result[row] = sum / m[row, row];

            if (!double.IsFinite(result[row])) return null;
        }

        return result;
    }
}
