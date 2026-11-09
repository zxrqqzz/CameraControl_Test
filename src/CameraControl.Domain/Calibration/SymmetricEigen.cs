namespace CameraControl.Domain.Calibration;

/// <summary>
/// 对称矩阵特征分解（循环 Jacobi）。
///
/// 为什么自己写而不用矩阵库：
/// - 领域层零依赖（见 Directory.Build.props 的约束）；
/// - 只处理 6×6 / 9×9 这种小矩阵，Jacobi 足够稳，且可逐行审计。
/// </summary>
internal static class SymmetricEigen
{
    /// <summary>返回特征值（升序）与对应特征向量（按列存放）。</summary>
    public static (double[] Values, double[,] Vectors) Decompose(double[,] input)
    {
        ArgumentNullException.ThrowIfNull(input);

        int n = input.GetLength(0);
        if (n != input.GetLength(1)) throw new ArgumentException("必须是方阵。", nameof(input));

        var a = (double[,])input.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++) v[i, i] = 1;

        const int maxSweeps = 100;
        const double epsilon = 1e-14;

        for (int sweep = 0; sweep < maxSweeps; sweep++)
        {
            double offDiagonal = 0;
            for (int p = 0; p < n - 1; p++)
            {
                for (int q = p + 1; q < n; q++)
                    offDiagonal += a[p, q] * a[p, q];
            }

            if (offDiagonal < epsilon) break;

            for (int p = 0; p < n - 1; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-18) continue;

                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt((theta * theta) + 1));
                    if (theta == 0) t = 1;

                    double c = 1 / Math.Sqrt((t * t) + 1);
                    double s = t * c;

                    for (int k = 0; k < n; k++)
                    {
                        double akp = a[k, p];
                        double akq = a[k, q];
                        a[k, p] = (c * akp) - (s * akq);
                        a[k, q] = (s * akp) + (c * akq);
                    }

                    for (int k = 0; k < n; k++)
                    {
                        double apk = a[p, k];
                        double aqk = a[q, k];
                        a[p, k] = (c * apk) - (s * aqk);
                        a[q, k] = (s * apk) + (c * aqk);
                    }

                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p];
                        double vkq = v[k, q];
                        v[k, p] = (c * vkp) - (s * vkq);
                        v[k, q] = (s * vkp) + (c * vkq);
                    }
                }
            }
        }

        var values = new double[n];
        for (int i = 0; i < n; i++) values[i] = a[i, i];

        // 按特征值升序排列（同时调整特征向量列）
        var order = Enumerable.Range(0, n).OrderBy(i => values[i]).ToArray();
        var sortedValues = new double[n];
        var sortedVectors = new double[n, n];

        for (int dst = 0; dst < n; dst++)
        {
            int src = order[dst];
            sortedValues[dst] = values[src];

            for (int row = 0; row < n; row++)
                sortedVectors[row, dst] = v[row, src];
        }

        return (sortedValues, sortedVectors);
    }

    /// <summary>取最小特征值对应的特征向量。</summary>
    public static double[]? SmallestEigenvector(double[,] symmetricMatrix)
    {
        (double[] values, double[,] vectors) = Decompose(symmetricMatrix);

        if (values.Length == 0 || !double.IsFinite(values[0])) return null;

        int n = values.Length;
        var result = new double[n];

        for (int i = 0; i < n; i++) result[i] = vectors[i, 0];

        // 归一化，避免后续公式受尺度影响
        double norm = 0;
        for (int i = 0; i < n; i++) norm += result[i] * result[i];

        norm = Math.Sqrt(norm);
        if (norm < 1e-15) return null;

        for (int i = 0; i < n; i++) result[i] /= norm;
        return result;
    }

    /// <summary>把 9 个元素按行优先装成 3×3。</summary>
    public static double[,] ToMatrix3(double[] h) => new double[3, 3]
    {
        { h[0], h[1], h[2] },
        { h[3], h[4], h[5] },
        { h[6], h[7], h[8] }
    };

    public static double[] ToVector9(double[,] m) =>
        [m[0, 0], m[0, 1], m[0, 2], m[1, 0], m[1, 1], m[1, 2], m[2, 0], m[2, 1], m[2, 2]];

    public static double[,] Multiply3(double[,] a, double[,] b)
    {
        var r = new double[3, 3];

        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++) sum += a[i, k] * b[k, j];
                r[i, j] = sum;
            }
        }

        return r;
    }
}
