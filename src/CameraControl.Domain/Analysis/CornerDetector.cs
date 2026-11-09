namespace CameraControl.Domain.Analysis;

/// <summary>角点候选。</summary>
/// <param name="X">亚像素 X。</param>
/// <param name="Y">亚像素 Y。</param>
/// <param name="Response">Harris 响应强度。</param>
public readonly record struct CornerCandidate(double X, double Y, double Response);

/// <summary>
/// Harris 角点检测 + 亚像素精化。
///
/// 为什么自己写：领域层零依赖（不引 OpenCV）。
/// 坐标约定：像素中心为整数坐标（与 OpenCV 一致），左上像素为 (0,0)。
/// </summary>
public static class CornerDetector
{
    private const double HarrisK = 0.04;

    /// <summary>
    /// 检测角点。
    /// </summary>
    /// <param name="image">灰度图。</param>
    /// <param name="windowRadius">结构张量窗口半径（1 → 3×3）。</param>
    /// <param name="nmsRadius">非极大抑制半径。</param>
    /// <param name="thresholdPercentile">保留响应最强的比例（0.98 表示前 2%）。</param>
    /// <param name="maxCandidates">最多返回多少个。</param>
    public static IReadOnlyList<CornerCandidate> Detect(
        GrayImage image,
        int windowRadius = 2,
        int nmsRadius = 3,
        double thresholdPercentile = 0.98,
        int maxCandidates = 400)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowRadius, 1);

        int width = image.Width;
        int height = image.Height;

        // ── 1. Sobel 梯度 ──────────────────────────────────
        var gx = new double[width * height];
        var gy = new double[width * height];

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                double a = image.At(x - 1, y - 1), b = image.At(x, y - 1), c = image.At(x + 1, y - 1);
                double d = image.At(x - 1, y), f = image.At(x + 1, y);
                double g = image.At(x - 1, y + 1), h = image.At(x, y + 1), i = image.At(x + 1, y + 1);

                gx[(y * width) + x] = ((c + (2 * f) + i) - (a + (2 * d) + g)) / 8.0;
                gy[(y * width) + x] = ((g + (2 * h) + i) - (a + (2 * b) + c)) / 8.0;
            }
        }

        // ── 2. 结构张量与 Harris 响应 ──────────────────────
        var response = new double[width * height];

        for (int y = windowRadius; y < height - windowRadius; y++)
        {
            for (int x = windowRadius; x < width - windowRadius; x++)
            {
                double sxx = 0, syy = 0, sxy = 0;

                for (int dy = -windowRadius; dy <= windowRadius; dy++)
                {
                    for (int dx = -windowRadius; dx <= windowRadius; dx++)
                    {
                        int idx = ((y + dy) * width) + x + dx;
                        double ix = gx[idx], iy = gy[idx];

                        sxx += ix * ix;
                        syy += iy * iy;
                        sxy += ix * iy;
                    }
                }

                double det = (sxx * syy) - (sxy * sxy);
                double trace = sxx + syy;
                response[(y * width) + x] = det - (HarrisK * trace * trace);
            }
        }

        // ── 3. 自适应阈值（分位数）────────────────────────
        double threshold = PercentileThreshold(response, thresholdPercentile);

        // ── 4. 非极大抑制 ──────────────────────────────────
        var candidates = new List<CornerCandidate>();

        for (int y = nmsRadius; y < height - nmsRadius; y++)
        {
            for (int x = nmsRadius; x < width - nmsRadius; x++)
            {
                double value = response[(y * width) + x];
                if (value <= threshold || value <= 0) continue;

                bool isMax = true;

                for (int dy = -nmsRadius; dy <= nmsRadius && isMax; dy++)
                {
                    for (int dx = -nmsRadius; dx <= nmsRadius; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;

                        if (response[((y + dy) * width) + x + dx] > value)
                        {
                            isMax = false;
                            break;
                        }
                    }
                }

                if (!isMax) continue;

                (double sx, double sy) = RefineSubpixel(response, width, height, x, y);
                candidates.Add(new CornerCandidate(sx, sy, value));
            }
        }

        // 取最强的若干个
        candidates.Sort((a, b) => b.Response.CompareTo(a.Response));

        if (candidates.Count > maxCandidates)
            candidates.RemoveRange(maxCandidates, candidates.Count - maxCandidates);

        return candidates;
    }

    /// <summary>
    /// 用 3×3 Harris 响应的二次曲面拟合求亚像素偏移。
    /// </summary>
    private static (double X, double Y) RefineSubpixel(double[] response, int width, int height, int x, int y)
    {
        if (x < 1 || y < 1 || x >= width - 1 || y >= height - 1) return (x, y);

        double c = response[(y * width) + x];
        double left = response[(y * width) + x - 1];
        double right = response[(y * width) + x + 1];
        double up = response[((y - 1) * width) + x];
        double down = response[((y + 1) * width) + x];

        double dx = SubpixelOffset(left, c, right);
        double dy = SubpixelOffset(up, c, down);

        // 偏移超过半个像素说明拟合不可信，退回整数坐标
        if (Math.Abs(dx) > 0.5) dx = 0;
        if (Math.Abs(dy) > 0.5) dy = 0;

        return (x + dx, y + dy);
    }

    private static double SubpixelOffset(double left, double center, double right)
    {
        double denominator = left - (2 * center) + right;
        if (Math.Abs(denominator) < 1e-12) return 0;

        return 0.5 * (left - right) / denominator;
    }

    /// <summary>取响应数组中为正值的分位数作为阈值。</summary>
    private static double PercentileThreshold(double[] response, double percentile)
    {
        var positive = new List<double>(response.Length / 2);

        foreach (double v in response)
        {
            if (v > 0) positive.Add(v);
        }

        if (positive.Count == 0) return double.MaxValue;

        positive.Sort();
        int index = (int)Math.Clamp(percentile * (positive.Count - 1), 0, positive.Count - 1);
        return positive[index];
    }
}
