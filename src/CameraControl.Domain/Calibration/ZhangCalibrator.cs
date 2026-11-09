namespace CameraControl.Domain.Calibration;

/// <summary>
/// 张正友平面标定（闭合解）：由多张棋盘格图像求相机内参。
///
/// 步骤：
/// 1. 每个视角用归一化 DLT 求单应矩阵 H（物平面 → 像平面）；
/// 2. 用 H 的列向量构造 Zhang 约束 v12·b = 0、(v11−v22)·b = 0；
/// 3. 解 b（绝对二次曲线像 B 的 6 个元素）→ 提取 fx/fy/cx/cy；
/// 4. 合理性校验（焦距为正、主点落在合理范围）。
///
/// 当前版本**只求内参，不含畸变系数、不做非线性优化**。
/// 无畸变或畸变很小的镜头结果可信；广角/鱼眼必须先做畸变估计（见 TODO-REALDATA）。
/// </summary>
public sealed class ZhangCalibrator : ICameraCalibrator
{
    public IntrinsicsSolveResult Solve(
        IReadOnlyList<CalibrationView> views,
        ChessboardSpec spec,
        int imageWidth,
        int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(spec);

        if (imageWidth <= 0 || imageHeight <= 0)
            return IntrinsicsSolveResult.Failure("图像尺寸无效。");

        // 每个视角的真实自由度只有 6 个（R,t 去掉 z 方向），3 个视角是理论上限的最小值；
        // 实际工程强烈建议 ≥8 张，这里只做硬性下限检查。
        IReadOnlyList<CalibrationView> usable = [.. views.Where(v => v.Count >= 4)];

        if (usable.Count < 3)
        {
            return IntrinsicsSolveResult.Failure(
                $"至少需要 3 个有效视角（每个视角 ≥4 个点），当前 {usable.Count} 个。",
                [CalibrationErrorCodes.TooFewViews]);
        }

        var diagnostics = new List<string>();
        var homographies = new List<double[,]>(usable.Count);

        foreach (CalibrationView view in usable)
        {
            if (view.Count != view.ObjectPoints.Count || view.Count != view.ImagePoints.Count)
            {
                return IntrinsicsSolveResult.Failure(
                    "物点与图像点数量不一致。",
                    [CalibrationErrorCodes.PointCountMismatch]);
            }

            double[,]? h = ComputeHomography(view);
            if (h is null)
            {
                diagnostics.Add($"某个视角单应求解失败（点数 {view.Count}），已跳过。");
                continue;
            }

            homographies.Add(h);
        }

        if (homographies.Count < 3)
        {
            return IntrinsicsSolveResult.Failure(
                $"有效单应只有 {homographies.Count} 个，不足以求解内参。",
                [.. diagnostics, CalibrationErrorCodes.Degenerate]);
        }

        // ── 构造 2N×6 约束矩阵，最小特征向量即 b ──────────────
        int rows = homographies.Count * 2;
        var ata = new double[6, 6];

        foreach (double[,] h in homographies)
        {
            double[] v12 = BuildV(h, 0, 1);
            double[] v11 = BuildV(h, 0, 0);
            double[] v22 = BuildV(h, 1, 1);

            Accumulate(ata, v12, 1.0);
            Accumulate(ata, Subtract(v11, v22), 1.0);
        }

        double[]? b = SymmetricEigen.SmallestEigenvector(ata);
        if (b is null || !b.All(double.IsFinite))
        {
            return IntrinsicsSolveResult.Failure(
                "内参求解失败（矩阵退化或数值异常）。",
                [CalibrationErrorCodes.NumericFailure]);
        }

        // ── 由 b 提取内参 ─────────────────────────────────────
        double b11 = b[0], b12 = b[1], b22 = b[2], b13 = b[3], b23 = b[4], b33 = b[5];

        double denominator = (b11 * b22) - (b12 * b12);
        if (Math.Abs(denominator) < 1e-18 || Math.Abs(b11) < 1e-18)
        {
            return IntrinsicsSolveResult.Failure(
                "内参求解失败（B 矩阵退化，常见原因：所有视角几乎共面/未转动）。",
                [CalibrationErrorCodes.Degenerate]);
        }

        double v0 = (b12 * b13 - b11 * b23) / denominator;
        double lambda = b33 - ((b13 * b13) + (v0 * (b12 * b13 - b11 * b23))) / b11;

        if (lambda / b11 <= 0 || (lambda * b11) / denominator <= 0)
        {
            return IntrinsicsSolveResult.Failure(
                "焦距求解出现非实数（视角分布不足，需更多不同角度的图片）。",
                [CalibrationErrorCodes.ImplausibleResult]);
        }

        double fx = Math.Sqrt(lambda / b11);
        double fy = Math.Sqrt((lambda * b11) / denominator);
        double skew = -b12 * fx * fx * fy / lambda;
        double cx = (skew * v0 / fy) - (b13 * fx * fx / lambda);
        double cy = v0;

        if (!double.IsFinite(fx) || !double.IsFinite(fy) || !double.IsFinite(cx) || !double.IsFinite(cy)
            || fx <= 0 || fy <= 0)
        {
            return IntrinsicsSolveResult.Failure(
                "内参不是合理的正数，标定失败。",
                [CalibrationErrorCodes.ImplausibleResult]);
        }

        var intrinsics = new CameraIntrinsics(fx, fy, cx, cy, imageWidth, imageHeight);

        if (intrinsics.LooksTransposed)
            diagnostics.Add($"fx/fy 差异过大（{fx:F1} / {fy:F1}），检查棋盘行列约定是否弄反。");

        if (cx < -imageWidth || cx > imageWidth * 2 || cy < -imageHeight || cy > imageHeight * 2)
            diagnostics.Add($"主点 ({cx:F1}, {cy:F1}) 明显偏离图像范围，结果可疑。");

        double rmse = AverageHomographyRmse(usable, homographies);

        return new IntrinsicsSolveResult(true, intrinsics, rmse, null, diagnostics);
    }

    /// <summary>Zhang 约束里用到的 v 向量（6 维）。</summary>
    private static double[] BuildV(double[,] h, int i, int j)
    {
        // 取 H 的第 i、j 列（0 基）
        double a0 = h[0, i], a1 = h[1, i], a2 = h[2, i];
        double b0 = h[0, j], b1 = h[1, j], b2 = h[2, j];

        return
        [
            a0 * b0,
            (a0 * b1) + (a1 * b0),
            a1 * b1,
            (a2 * b0) + (a0 * b2),
            (a2 * b1) + (a1 * b2),
            a2 * b2
        ];
    }

    private static void Accumulate(double[,] ata, double[] v, double weight)
    {
        for (int r = 0; r < 6; r++)
        {
            for (int c = 0; c < 6; c++)
                ata[r, c] += weight * v[r] * v[c];
        }
    }

    private static double[] Subtract(double[] a, double[] b)
    {
        var r = new double[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = a[i] - b[i];
        return r;
    }

    /// <summary>归一化 DLT 求单应矩阵。</summary>
    private static double[,]? ComputeHomography(CalibrationView view)
    {
        int n = view.Count;
        if (n < 4) return null;

        // 归一化：把物点、像点分别平移到质心并把平均距离缩放到 √2
        (double[,] tObj, double[] objX, double[] objY) = Normalize(
            [.. view.ObjectPoints.Take(n).Select(p => (p.X, p.Y))]);

        (double[,] tImg, double[] imgU, double[] imgV) = Normalize(
            [.. view.ImagePoints.Take(n).Select(p => (p.X, p.Y))]);

        var ata = new double[9, 9];

        for (int k = 0; k < n; k++)
        {
            double x = objX[k], y = objY[k], u = imgU[k], v = imgV[k];

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

        double[,] hnMatrix = SymmetricEigen.ToMatrix3(hn);

        // H = T_img⁻¹ · Hn · T_obj
        double[,]? tImgInverse = Invert3(tImg);
        if (tImgInverse is null) return null;

        double[,] h = SymmetricEigen.Multiply3(
            SymmetricEigen.Multiply3(tImgInverse, hnMatrix),
            tObj);

        // 尺度归一：让 h[2,2] = 1（若接近 0 则用范数归一）
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

        return h;
    }

    private static (double[,] Transform, double[] X, double[] Y) Normalize((double X, double Y)[] points)
    {
        int n = points.Length;

        double meanX = points.Average(p => p.X);
        double meanY = points.Average(p => p.Y);

        double meanDistance = points.Average(p =>
            Math.Sqrt(((p.X - meanX) * (p.X - meanX)) + ((p.Y - meanY) * (p.Y - meanY))));

        double scale = meanDistance < 1e-12 ? 1.0 : Math.Sqrt(2) / meanDistance;

        var transform = new double[3, 3];
        transform[0, 0] = scale;
        transform[1, 1] = scale;
        transform[0, 2] = -scale * meanX;
        transform[1, 2] = -scale * meanY;
        transform[2, 2] = 1;

        var xs = new double[n];
        var ys = new double[n];

        for (int i = 0; i < n; i++)
        {
            xs[i] = (scale * points[i].X) - (scale * meanX);
            ys[i] = (scale * points[i].Y) - (scale * meanY);
        }

        return (transform, xs, ys);
    }

    private static double[,]? Invert3(double[,] m)
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

    /// <summary>单应重投影的均方根误差，用于衡量角点质量。</summary>
    private static double AverageHomographyRmse(
        IReadOnlyList<CalibrationView> views,
        IReadOnlyList<double[,]> homographies)
    {
        double total = 0;
        int counted = 0;

        for (int i = 0; i < homographies.Count && i < views.Count; i++)
        {
            double[,] h = homographies[i];
            CalibrationView view = views[i];
            int n = view.Count;

            double sum = 0;
            for (int k = 0; k < n; k++)
            {
                Point3 p = view.ObjectPoints[k];
                double w = (h[2, 0] * p.X) + (h[2, 1] * p.Y) + h[2, 2];
                if (Math.Abs(w) < 1e-12) continue;

                double u = ((h[0, 0] * p.X) + (h[0, 1] * p.Y) + h[0, 2]) / w;
                double v = ((h[1, 0] * p.X) + (h[1, 1] * p.Y) + h[1, 2]) / w;

                double dx = u - view.ImagePoints[k].X;
                double dy = v - view.ImagePoints[k].Y;
                sum += (dx * dx) + (dy * dy);
            }

            double rmse = Math.Sqrt(sum / n);
            if (double.IsFinite(rmse))
            {
                total += rmse;
                counted++;
            }
        }

        return counted == 0 ? double.NaN : total / counted;
    }
}
