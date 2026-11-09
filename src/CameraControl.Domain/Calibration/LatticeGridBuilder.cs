using CameraControl.Domain.Analysis;

namespace CameraControl.Domain.Calibration;

/// <summary>检出的棋盘格点阵（行优先）。</summary>
/// <param name="Rows">行数（内角点）。</param>
/// <param name="Columns">列数（内角点）。</param>
/// <param name="Points">按 (row, col) 行优先排列的亚像素坐标。</param>
public sealed record DetectedGrid(int Rows, int Columns, IReadOnlyList<Point2> Points)
{
    public int Count => Points.Count;

    public Point2 At(int row, int col) => Points[(row * Columns) + col];
}

/// <summary>点阵构建结果。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Grid">点阵（成功时非空）。</param>
/// <param name="Error">失败原因。</param>
/// <param name="Notes">附加说明。</param>
/// <param name="Coverage">覆盖比例（0–1），用于诊断尺寸假设是否正确。</param>
public sealed record LatticeResult(
    bool Ok,
    DetectedGrid? Grid,
    string? Error = null,
    IReadOnlyList<string>? Notes = null,
    double Coverage = 0)
{
    public static LatticeResult Failure(string error, IReadOnlyList<string>? notes = null, double coverage = 0) =>
        new(false, null, error, notes, coverage);
}

/// <summary>
/// 把散乱角点整理成规则点阵（棋盘格）。
///
/// ⚠ **实验状态：真实图像上尚不可用**（见 docs/09-真实数据验证.md）。
/// 合成数据可用，但真实图像的伪角点会让"极值四角"初值失效，
/// 覆盖率只能到 ~26%。修好之前不要把它当作可靠组件引用。
/// 正确的下一步是 RANSAC 式全局拟合，而不是这里用的极值/生长策略。
///
/// 现有做法（合成数据可用）：
/// 1. 对每个点取最近的若干个邻居，统计"近邻向量"的众数——得到两个格基方向 b1、b2；
/// 2. 从若干强角点出发做 BFS，沿 ±b1 / ±b2 找最近未分配点并赋整数坐标；
/// 3. 取最大的连通分量，归一化后要求**恰好填满**一个完整矩形，且至少 4×4。
///
/// 为什么要"恰好填满 + 最小尺寸"：缺角或几点的"点阵"会让对象点与图像点错位。
/// 与其给出一个悄悄错掉的结果，不如明确失败。
/// </summary>
public static class LatticeGridBuilder
{
    /// <summary>邻居匹配容差（相对格距）。</summary>
    private const double NeighborTolerance = 0.30;

    /// <summary>认为有效的最小格距（像素）。</summary>
    private const double MinSpacing = 8.0;

    /// <summary>点阵最小边长（内角点数），低于此视为检测不完整。</summary>
    private const int MinGridSide = 4;

    public static LatticeResult Build(
        IReadOnlyList<CornerCandidate> candidates,
        int maxPoints = 400)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count < MinGridSide * MinGridSide)
            return LatticeResult.Failure($"角点太少（{candidates.Count} 个），无法构成点阵。");

        IReadOnlyList<CornerCandidate> points = candidates.Count > maxPoints
            ? [.. candidates.Take(maxPoints)]
            : candidates;

        (double b1x, double b1y, double b2x, double b2y)? basis = EstimateBasis(points);
        if (basis is null)
            return LatticeResult.Failure("无法估计格基（角点分布不支持规则点阵）。");

        (double b1x, double b1y, double b2x, double b2y) b = basis.Value;
        double spacing = Math.Min(Hypot(b.b1x, b.b1y), Hypot(b.b2x, b.b2y));

        if (spacing < MinSpacing)
            return LatticeResult.Failure($"格距过小（{spacing:F2}px），可能是误检。");

        Dictionary<int, (int I, int J)> assigned = AssignLattice(points, b, spacing);
        if (assigned.Count == 0)
            return LatticeResult.Failure("BFS 未能连通任何点阵。");

        // ── 用单应把整数索引吸附到真实位置，修正 BFS 的累积漂移 ──
        assigned = RefineWithHomography(points, assigned, spacing);

        if (assigned.Count == 0)
            return LatticeResult.Failure("单应吸附后没有剩余点。");

        // 归一化
        int minI = assigned.Values.Min(c => c.I);
        int minJ = assigned.Values.Min(c => c.J);
        int width = assigned.Values.Max(c => c.I) - minI + 1;
        int height = assigned.Values.Max(c => c.J) - minJ + 1;

        if (width < MinGridSide || height < MinGridSide)
        {
            return LatticeResult.Failure(
                $"点阵过小（{height}×{width}），检测不完整或格基估计错误。");
        }

        if ((long)width * height != assigned.Count)
        {
            return LatticeResult.Failure(
                $"点阵不完整：覆盖 {width}×{height}={width * height} 个格点，实际连通 {assigned.Count} 个。");
        }

        // 行优先收集
        var grid = new Point2[height * width];

        foreach ((int index, (int i, int j)) in assigned)
        {
            int row = j - minJ;
            int col = i - minI;
            CornerCandidate p = points[index];
            grid[(row * width) + col] = new Point2(p.X, p.Y);
        }

        return new LatticeResult(true, new DetectedGrid(height, width, grid));
    }

    /// <summary>
    /// 估计两个格基向量。
    ///
    /// 关键：**按出现次数选**。棋盘格上每个点都有沿 b1、b2 的邻居，
    /// 所以真实格距向量的出现次数远高于任何伪角点造成的短向量。
    /// </summary>
    private static (double b1x, double b1y, double b2x, double b2y)? EstimateBasis(
        IReadOnlyList<CornerCandidate> points)
    {
        const int neighborsPerPoint = 6;
        var buckets = new Dictionary<(int, int), int>();

        // 复用缓冲区（放在循环外，避免 stackalloc-in-loop 告警与重复分配）
        var bestDistance = new double[neighborsPerPoint];
        var bestIndex = new int[neighborsPerPoint];

        for (int i = 0; i < points.Count; i++)
        {
            Array.Fill(bestDistance, double.MaxValue);
            Array.Fill(bestIndex, -1);

            // 找 i 的最近若干邻居（不用全对，避免跨格向量污染统计）
            for (int j = 0; j < points.Count; j++)
            {
                if (i == j) continue;

                double dx = points[j].X - points[i].X;
                double dy = points[j].Y - points[i].Y;
                double distance = Hypot(dx, dy);

                // 插入排序进 top-k
                for (int k = 0; k < neighborsPerPoint; k++)
                {
                    if (distance < bestDistance[k])
                    {
                        for (int m = neighborsPerPoint - 1; m > k; m--)
                        {
                            bestDistance[m] = bestDistance[m - 1];
                            bestIndex[m] = bestIndex[m - 1];
                        }

                        bestDistance[k] = distance;
                        bestIndex[k] = j;
                        break;
                    }
                }
            }

            for (int k = 0; k < neighborsPerPoint; k++)
            {
                int j = bestIndex[k];
                if (j < 0) continue;

                double dx = points[j].X - points[i].X;
                double dy = points[j].Y - points[i].Y;

                if (bestDistance[k] < 3) continue;   // 同一角点的重复检测

                // 方向规范化到半平面，使 (dx,dy) 与 (-dx,-dy) 归为同一类
                if (dx < 0 || (Math.Abs(dx) < 1e-9 && dy < 0))
                {
                    dx = -dx;
                    dy = -dy;
                }

                var key = ((int)Math.Round(dx), (int)Math.Round(dy));
                buckets[key] = buckets.GetValueOrDefault(key) + 1;
            }
        }

        if (buckets.Count == 0) return null;

        int minCount = Math.Max(4, points.Count / 10);

        List<(int Dx, int Dy, int Count)> frequent = [.. buckets
            .Where(kv => kv.Value >= minCount && Hypot(kv.Key.Item1, kv.Key.Item2) >= MinSpacing)
            .Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value))
            .OrderByDescending(t => t.Value)
            .ThenBy(t => Hypot(t.Item1, t.Item2))];

        // 计数不足时放宽（小棋盘或点子较少）
        if (frequent.Count == 0)
        {
            frequent = [.. buckets
                .Where(kv => Hypot(kv.Key.Item1, kv.Key.Item2) >= MinSpacing)
                .Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value))
                .OrderByDescending(t => t.Value)
                .ThenBy(t => Hypot(t.Item1, t.Item2))];
        }

        if (frequent.Count == 0) return null;

        (int Dx, int Dy, int Count) first = frequent[0];
        double firstLength = Hypot(first.Dx, first.Dy);

        foreach ((int Dx, int Dy, int Count) second in frequent)
        {
            if (second.Dx == first.Dx && second.Dy == first.Dy) continue;

            double secondLength = Hypot(second.Dx, second.Dy);

            // 两基向量长度应当同量级（棋盘格是方格）
            if (secondLength < firstLength * 0.6 || secondLength > firstLength * 1.7) continue;

            double cross = Math.Abs((first.Dx * second.Dy) - (first.Dy * second.Dx));
            if (cross / (firstLength * secondLength) > 0.35)
                return (first.Dx, first.Dy, second.Dx, second.Dy);
        }

        return null;
    }

    /// <summary>
    /// 从多个种子尝试 BFS，取最大连通分量。
    ///
    /// 单种子容易落在伪角点上，多种子 + 取最大分量显著更稳。
    /// </summary>
    private static Dictionary<int, (int I, int J)> AssignLattice(
        IReadOnlyList<CornerCandidate> points,
        (double b1x, double b1y, double b2x, double b2y) basis,
        double spacing)
    {
        double tolerance = spacing * NeighborTolerance;
        int seedCount = Math.Min(8, points.Count);

        Dictionary<int, (int I, int J)> best = [];

        for (int seed = 0; seed < seedCount; seed++)
        {
            Dictionary<int, (int I, int J)> component = Flood(points, basis, tolerance, seed);

            if (component.Count > best.Count) best = component;

            // 已经连成足够大的点阵就不必再试
            if (component.Count >= points.Count * 0.6) break;
        }

        return best;
    }

    private static Dictionary<int, (int I, int J)> Flood(
        IReadOnlyList<CornerCandidate> points,
        (double b1x, double b1y, double b2x, double b2y) basis,
        double tolerance,
        int seed)
    {
        var assigned = new Dictionary<int, (int I, int J)> { [seed] = (0, 0) };
        var usedCoordinates = new HashSet<(int, int)> { (0, 0) };
        var queue = new Queue<int>();
        queue.Enqueue(seed);

        Span<(double Dx, double Dy, int Di, int Dj)> steps =
        [
            (basis.b1x, basis.b1y, 1, 0),
            (-basis.b1x, -basis.b1y, -1, 0),
            (basis.b2x, basis.b2y, 0, 1),
            (-basis.b2x, -basis.b2y, 0, -1)
        ];

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            (int i, int j) = assigned[current];
            CornerCandidate p = points[current];

            foreach ((double dx, double dy, int di, int dj) in steps)
            {
                double targetX = p.X + dx;
                double targetY = p.Y + dy;

                int bestIndex = -1;
                double bestDistance = tolerance;

                for (int k = 0; k < points.Count; k++)
                {
                    if (assigned.ContainsKey(k)) continue;

                    double distance = Hypot(points[k].X - targetX, points[k].Y - targetY);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = k;
                    }
                }

                if (bestIndex < 0) continue;

                // 同一格点坐标只能属于一个角点，否则会出现"连通数 > 矩形格数"
                if (!usedCoordinates.Add((i + di, j + dj))) continue;

                assigned[bestIndex] = (i + di, j + dj);
                queue.Enqueue(bestIndex);
            }
        }

        return assigned;
    }

    /// <summary>
    /// 用单应把整数索引"吸附"到真实角点位置。
    ///
    /// BFS 用全局固定格基，透视会让它逐步漂移（出现 16×13 这种不存在的尺寸，
    /// 甚至把两个点分到同一格）。这里改成：
    /// 拟合 (i,j)→(x,y) 单应 → 剔除残差大的 → 反复几次 → 再用**逆单应**
    /// 把每个候选点吸附到最近的整数格点。
    /// </summary>
    private static Dictionary<int, (int I, int J)> RefineWithHomography(
        IReadOnlyList<CornerCandidate> points,
        Dictionary<int, (int I, int J)> initial,
        double spacing)
    {
        Dictionary<int, (int I, int J)> current = new(initial);

        for (int iteration = 0; iteration < 4; iteration++)
        {
            Homography? h = FitLattice(current, points);
            if (h is null) break;

            double tolerance = Math.Max(1.5, spacing * 0.2);
            var inliers = new Dictionary<int, (int I, int J)>();

            foreach ((int index, (int i, int j)) in current)
            {
                Point2 predicted = h.Apply(new Point2(i, j));
                double dx = predicted.X - points[index].X;
                double dy = predicted.Y - points[index].Y;

                if (!double.IsNaN(dx) && Math.Sqrt((dx * dx) + (dy * dy)) <= tolerance)
                    inliers[index] = (i, j);
            }

            if (inliers.Count < 6) break;
            if (inliers.Count == current.Count) break;

            current = inliers;
        }

        // ── 最终吸附：把所有候选点映射到格坐标并取整 ────────
        Homography? final = FitLattice(current, points);
        Homography? inverse = final?.Inverse();
        if (inverse is null) return current;

        double snapTolerance = 0.35;
        double residualTolerance = Math.Max(1.5, spacing * 0.35);

        var used = new HashSet<(int, int)>();
        var snapped = new Dictionary<int, (int I, int J)>();

        for (int index = 0; index < points.Count; index++)
        {
            Point2 lattice = inverse.Apply(new Point2(points[index].X, points[index].Y));
            if (double.IsNaN(lattice.X) || double.IsNaN(lattice.Y)) continue;

            int i = (int)Math.Round(lattice.X);
            int j = (int)Math.Round(lattice.Y);

            if (Math.Abs(lattice.X - i) > snapTolerance || Math.Abs(lattice.Y - j) > snapTolerance) continue;

            Point2 predicted = final!.Apply(new Point2(i, j));
            double dx = predicted.X - points[index].X;
            double dy = predicted.Y - points[index].Y;

            if (Math.Sqrt((dx * dx) + (dy * dy)) > residualTolerance) continue;

            // 同一格只保留一个点（先到先得，候选按响应强度排序）
            if (!used.Add((i, j))) continue;

            snapped[index] = (i, j);
        }

        return snapped.Count >= current.Count ? snapped : current;
    }

    private static Homography? FitLattice(
        Dictionary<int, (int I, int J)> assignment,
        IReadOnlyList<CornerCandidate> points)
    {
        var from = new List<Point2>(assignment.Count);
        var to = new List<Point2>(assignment.Count);

        foreach ((int index, (int i, int j)) in assignment)
        {
            from.Add(new Point2(i, j));
            to.Add(new Point2(points[index].X, points[index].Y));
        }

        return from.Count < 4 ? null : Homography.Compute(from, to);
    }

    /// <summary>
    /// 已知棋盘尺寸时的点阵构建（推荐路径）。
    ///
    /// 用"点集四个极端角"作为初始单应的四对点（尝试 8 种角点对应关系），
    /// 再迭代吸附全部角点。比从格基 BFS 生长稳健得多——
    /// 因为初始单应来自**全局极值**，不会像 BFS 那样逐步漂移。
    /// </summary>
    /// <param name="candidates">角点候选（建议按响应强度降序）。</param>
    /// <param name="columns">内角点列数。</param>
    /// <param name="rows">内角点行数。</param>
    /// <param name="maxPoints">参与计算的最大点数。</param>
    public static LatticeResult BuildFromDimensions(
        IReadOnlyList<CornerCandidate> candidates,
        int columns,
        int rows,
        int maxPoints = 400)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (columns < MinGridSide || rows < MinGridSide)
            return LatticeResult.Failure($"棋盘尺寸过小：{rows}×{columns}（至少 {MinGridSide}×{MinGridSide}）。");

        IReadOnlyList<CornerCandidate> points = candidates.Count > maxPoints
            ? [.. candidates.Take(maxPoints)]
            : candidates;

        if (points.Count < 8)
            return LatticeResult.Failure($"角点太少（{points.Count} 个）。");

        // ── 找四个极端角 ──────────────────────────────────
        int ia = 0, ib = 0, ic = 0, id = 0;
        double minSum = double.MaxValue, maxSum = double.MinValue;
        double minDiff = double.MaxValue, maxDiff = double.MinValue;

        for (int i = 0; i < points.Count; i++)
        {
            double sum = points[i].X + points[i].Y;
            double diff = points[i].X - points[i].Y;

            if (sum < minSum) { minSum = sum; ia = i; }
            if (sum > maxSum) { maxSum = sum; ib = i; }
            if (diff < minDiff) { minDiff = diff; ic = i; }
            if (diff > maxDiff) { maxDiff = diff; id = i; }
        }

        int[] corners = [ia, ib, ic, id];

        if (corners.Distinct().Count() < 4)
            return LatticeResult.Failure("四个极端角点有重复，点云形状异常。");

        // 按绕质心的角度排序，得到环形顺序
        double cx = points.Average(p => p.X);
        double cy = points.Average(p => p.Y);

        corners = [.. corners.OrderBy(i => Math.Atan2(points[i].Y - cy, points[i].X - cx))];

        // 目标格点四角（顺序与上式同向）
        Point2[] latticeCorners =
        [
            new(0, 0),
            new(columns - 1, 0),
            new(columns - 1, rows - 1),
            new(0, rows - 1)
        ];

        double spacingEstimate = EstimateSpacing(points);

        Dictionary<int, (int I, int J)>? bestAssignment = null;
        int bestCount = 0;

        // 8 种对应：4 个起点 × 2 个绕行方向
        for (int start = 0; start < 4; start++)
        {
            for (int direction = 0; direction < 2; direction++)
            {
                var from = new List<Point2>(4);
                var to = new List<Point2>(4);

                for (int k = 0; k < 4; k++)
                {
                    int imageIndex = corners[Mod(start + (direction == 0 ? k : -k), 4)];
                    from.Add(latticeCorners[k]);
                    to.Add(new Point2(points[imageIndex].X, points[imageIndex].Y));
                }

                Homography? hypothesis = Homography.Compute(from, to);
                if (hypothesis is null) continue;

                Dictionary<int, (int I, int J)> snapped = SnapWithHomography(
                    points, hypothesis, columns, rows, spacingEstimate);

                if (snapped.Count > bestCount)
                {
                    bestCount = snapped.Count;
                    bestAssignment = snapped;
                }
            }
        }

        if (bestAssignment is null || bestCount < 8)
            return LatticeResult.Failure("初值假设全部失败（可能棋盘未完整入镜）。");

        // ── 迭代精化 ──────────────────────────────────────
        Dictionary<int, (int I, int J)> current = bestAssignment;

        for (int iteration = 0; iteration < 5; iteration++)
        {
            Homography? h = FitLattice(current, points);
            if (h is null) break;

            Dictionary<int, (int I, int J)> next = SnapWithHomography(
                points, h, columns, rows, spacingEstimate, seed: current);

            if (next.Count <= current.Count) break;
            current = next;
        }

        // ── 检查覆盖 ──────────────────────────────────────
        int expected = columns * rows;
        double coverage = (double)current.Count / expected;

        if (coverage < 0.9)
        {
            return LatticeResult.Failure(
                $"点阵覆盖不足：{current.Count}/{expected}（{coverage * 100:F0}%），可能棋盘部分出框。",
                coverage: coverage);
        }

        var grid = new Point2[expected];
        var seen = new bool[expected];
        int filled = 0;

        foreach ((int index, (int i, int j)) in current)
        {
            if (i < 0 || i >= columns || j < 0 || j >= rows) continue;

            int slot = (j * columns) + i;
            if (seen[slot]) continue;

            seen[slot] = true;
            grid[slot] = new Point2(points[index].X, points[index].Y);
            filled++;
        }

        if (filled < expected)
        {
            int holes = expected - filled;
            if (holes > expected / 10)
                return LatticeResult.Failure($"点阵有 {holes} 个空洞，跳过该视角。");
        }

        // 有洞时用单应补齐（这些点是插值出来的，精度略低但可用）
        if (filled < expected)
        {
            Homography? h = FitLattice(current, points);
            if (h is null) return LatticeResult.Failure("无法为空洞补点。");

            for (int slot = 0; slot < expected; slot++)
            {
                if (seen[slot]) continue;

                int i = slot % columns;
                int j = slot / columns;
                grid[slot] = h.Apply(new Point2(i, j));
            }
        }

        return new LatticeResult(true, new DetectedGrid(rows, columns, grid), Coverage: coverage);
    }

    /// <summary>用单应反投影把点吸附到整数格点；同一格只保留一个点。</summary>
    private static Dictionary<int, (int I, int J)> SnapWithHomography(
        IReadOnlyList<CornerCandidate> points,
        Homography latticeToImage,
        int columns,
        int rows,
        double spacing,
        Dictionary<int, (int I, int J)>? seed = null)
    {
        Homography? inverse = latticeToImage.Inverse();
        if (inverse is null) return seed is null ? [] : new Dictionary<int, (int I, int J)>(seed);

        double residualTolerance = Math.Max(2.0, spacing * 0.35);
        double snapTolerance = 0.45;

        var used = new HashSet<(int, int)>();
        var result = new Dictionary<int, (int I, int J)>();

        // 有种子的先放进去，保证不退化
        if (seed is not null)
        {
            foreach ((int index, (int i, int j)) in seed)
            {
                if (used.Add((i, j))) result[index] = (i, j);
            }
        }

        for (int index = 0; index < points.Count; index++)
        {
            Point2 lattice = inverse.Apply(new Point2(points[index].X, points[index].Y));
            if (double.IsNaN(lattice.X) || double.IsNaN(lattice.Y)) continue;

            int i = (int)Math.Round(lattice.X);
            int j = (int)Math.Round(lattice.Y);

            if (i < 0 || i >= columns || j < 0 || j >= rows) continue;
            if (Math.Abs(lattice.X - i) > snapTolerance || Math.Abs(lattice.Y - j) > snapTolerance) continue;

            Point2 predicted = latticeToImage.Apply(new Point2(i, j));
            double dx = predicted.X - points[index].X;
            double dy = predicted.Y - points[index].Y;

            if (Math.Sqrt((dx * dx) + (dy * dy)) > residualTolerance) continue;
            if (!used.Add((i, j))) continue;

            result[index] = (i, j);
        }

        return result;
    }

    private static double EstimateSpacing(IReadOnlyList<CornerCandidate> points)
    {
        // 用最近邻距离的中位数粗略估计格距
        var nearest = new List<double>(points.Count);

        for (int i = 0; i < points.Count; i++)
        {
            double best = double.MaxValue;

            for (int j = 0; j < points.Count; j++)
            {
                if (i == j) continue;
                double d = Hypot(points[j].X - points[i].X, points[j].Y - points[i].Y);
                if (d > 2 && d < best) best = d;
            }

            if (best < double.MaxValue) nearest.Add(best);
        }

        if (nearest.Count == 0) return 20;
        nearest.Sort();
        return nearest[nearest.Count / 2];
    }

    private static int Mod(int value, int modulus) => ((value % modulus) + modulus) % modulus;

    private static double Hypot(double x, double y) => Math.Sqrt((x * x) + (y * y));
}
