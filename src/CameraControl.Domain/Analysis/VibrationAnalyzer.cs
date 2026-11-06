namespace CameraControl.Domain.Analysis;

/// <summary>
/// 帧间位移测量（振动分析内核）。
///
/// 方法：以参考帧 ROI 为模板，在目标帧的搜索窗内做**归一化互相关（NCC）**，
/// 峰值处用**抛物线拟合**取得亚像素位移。
///
/// 为什么不直接做相位相关（FFT）：零依赖、实现可被逐行审计，
/// 在纯平移场景精度已足够；后续若需旋转/缩放再换。
/// </summary>
public static class VibrationAnalyzer
{
    /// <summary>
    /// 测量目标帧相对参考帧的位移。
    /// </summary>
    /// <param name="reference">参考帧（模板来源）。</param>
    /// <param name="target">目标帧。</param>
    /// <param name="roi">参考帧中的模板区域。</param>
    /// <param name="searchRadius">搜索半径（像素）。</param>
    /// <param name="minScore">最低可信相关分数；低于此值返回 Invalid。</param>
    public static OffsetMeasurement Measure(
        GrayImage reference,
        GrayImage target,
        Roi roi,
        int searchRadius = 12,
        double minScore = 0.3)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(searchRadius, 1);

        if (reference.Width != target.Width || reference.Height != target.Height)
            throw new ArgumentException("参考帧与目标帧尺寸必须一致。");

        int w = roi.Width;
        int h = roi.Height;
        if (w < 8 || h < 8)
            throw new ArgumentException("ROI 太小（至少 8×8）。", nameof(roi));

        int size = (2 * searchRadius) + 1;
        var scores = new double[size, size];
        double bestScore = double.NegativeInfinity;
        int bestIx = -1;
        int bestIy = -1;

        for (int iy = 0; iy < size; iy++)
        {
            int dy = iy - searchRadius;
            for (int ix = 0; ix < size; ix++)
            {
                int dx = ix - searchRadius;

                double score = NormalizedCrossCorrelation(reference, target, roi, dx, dy);
                scores[ix, iy] = score;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIx = ix;
                    bestIy = iy;
                }
            }
        }

        if (bestIx < 0 || bestScore < minScore)
            return OffsetMeasurement.Invalid;

        // 抛物线亚像素拟合（需要左右/上下邻居都在边界内）
        double subX = 0;
        double subY = 0;

        if (bestIx > 0 && bestIx < size - 1)
            subX = ParabolaPeak(scores[bestIx - 1, bestIy], bestScore, scores[bestIx + 1, bestIy]);

        if (bestIy > 0 && bestIy < size - 1)
            subY = ParabolaPeak(scores[bestIx, bestIy - 1], bestScore, scores[bestIx, bestIy + 1]);

        double resultDx = (bestIx - searchRadius) + subX;
        double resultDy = (bestIy - searchRadius) + subY;
        double amplitude = Math.Sqrt((resultDx * resultDx) + (resultDy * resultDy));

        return new OffsetMeasurement(resultDx, resultDy, amplitude, bestScore);
    }

    /// <summary>
    /// 归一化互相关：把模板放到参考位置后与目标帧在 (dx,dy) 偏移处比较。
    /// 越界返回 -1（不可信）。
    /// </summary>
    private static double NormalizedCrossCorrelation(
        GrayImage reference,
        GrayImage target,
        Roi roi,
        int dx,
        int dy)
    {
        int x0 = roi.X + dx;
        int y0 = roi.Y + dy;

        if (x0 < 0 || y0 < 0) return -1;
        if (x0 + roi.Width > target.Width || y0 + roi.Height > target.Height) return -1;

        double sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
        long n = (long)roi.Width * roi.Height;

        for (int y = 0; y < roi.Height; y++)
        {
            int ry = roi.Y + y;
            int ty = y0 + y;

            for (int x = 0; x < roi.Width; x++)
            {
                double a = reference.At(roi.X + x, ry);
                double b = target.At(x0 + x, ty);

                sumA += a;
                sumB += b;
                sumAA += a * a;
                sumBB += b * b;
                sumAB += a * b;
            }
        }

        double numerator = (n * sumAB) - (sumA * sumB);
        double varA = (n * sumAA) - (sumA * sumA);
        double varB = (n * sumBB) - (sumB * sumB);

        if (varA <= 0 || varB <= 0) return -1;

        return numerator / Math.Sqrt(varA * varB);
    }

    /// <summary>三点抛物线峰值偏移量，范围 (-0.5, 0.5)。</summary>
    private static double ParabolaPeak(double left, double center, double right)
    {
        double denominator = left - (2 * center) + right;
        double scale = Math.Abs(left) + Math.Abs(center) + Math.Abs(right) + 1e-12;

        // 峰顶过于平坦（或相关面完全对称）时，插值结果只是数值噪声，直接归零
        if (Math.Abs(denominator) < 1e-6 * scale) return 0;

        double delta = 0.5 * (left - right) / denominator;
        return Math.Clamp(delta, -0.5, 0.5);
    }
}
