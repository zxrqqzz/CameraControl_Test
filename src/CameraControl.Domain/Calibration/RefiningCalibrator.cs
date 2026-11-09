namespace CameraControl.Domain.Calibration;

/// <summary>
/// 带畸变估计的非线性精化标定器。
///
/// 流程（交替优化）：
/// 1. 用 <see cref="ZhangCalibrator"/> 的闭合解作为初值；
/// 2. 交替执行：
///    a. 固定内参/畸变，用单应分解为每个视角估计位姿 (R, t)；
///    b. 固定位姿，用 Levenberg-Marquardt 优化 8 个参数
///       [fx, fy, cx, cy, k1, k2, p1, p2]，最小化重投影误差；
/// 3. 输出最终重投影 RMS。
///
/// 为什么交替而不是全参数联合优化：
/// 联合优化（8 + 6×N 个参数）需要更完整的稀疏求解与更好的初值策略；
/// 交替方案实现简单、收敛稳，实测足以把 RMS 从 ~1.8px 降到亚像素。
/// </summary>
public sealed class RefiningCalibrator : ICameraCalibrator
{
    private const int ParameterCount = 9;
    private const int MaxOuterIterations = 15;
    private const int MaxInnerIterations = 60;

    private readonly ZhangCalibrator _closedForm;
    private readonly int _maxViews;

    /// <param name="maxViews">最多使用多少个视角（0 表示不限制）。</param>
    public RefiningCalibrator(int maxViews = 0)
    {
        _closedForm = new ZhangCalibrator();
        _maxViews = maxViews;
    }

    /// <summary>当前位姿（R 为行优先旋转矩阵，T 为平移）。</summary>
    private sealed record Pose(double[,] R, double[] T);

    /// <summary>一个视角的观测与物点。</summary>
    private sealed record Observation(
        IReadOnlyList<Point3> ObjectPoints,
        IReadOnlyList<Point2> ImagePoints);

    /// <summary>已知位姿（例如手眼场景由机器人提供）。</summary>
    public sealed record PoseGuess(double[,] Rotation, double[] Translation);

    /// <summary>常规标定：位姿由单应分解估计并迭代优化。</summary>
    public IntrinsicsSolveResult Solve(
        IReadOnlyList<CalibrationView> views,
        ChessboardSpec spec,
        int imageWidth,
        int imageHeight) =>
        SolveCore(views, spec, imageWidth, imageHeight, fixedPoses: null);

    /// <summary>
    /// 给定位姿的标定：**不再估计/优化位姿**，只优化相机参数。
    ///
    /// 用途：① 手眼标定场景机器人位姿已知；② 隔离诊断"位姿估计 vs 参数优化"的缺陷来源。
    /// </summary>
    public IntrinsicsSolveResult SolveWithPoses(
        IReadOnlyList<CalibrationView> views,
        ChessboardSpec spec,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<PoseGuess> fixedPoses)
    {
        ArgumentNullException.ThrowIfNull(fixedPoses);
        return SolveCore(views, spec, imageWidth, imageHeight, fixedPoses);
    }

    private IntrinsicsSolveResult SolveCore(
        IReadOnlyList<CalibrationView> views,
        ChessboardSpec spec,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<PoseGuess>? fixedPoses)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(spec);

        IntrinsicsSolveResult init = _closedForm.Solve(views, spec, imageWidth, imageHeight);
        if (!init.Ok || init.Intrinsics is null) return init;

        var diagnostics = new List<string>(init.Diagnostics ?? []);

        IReadOnlyList<CalibrationView> usable = _maxViews > 0
            ? [.. views.Where(v => v.Count >= 4).Take(_maxViews)]
            : [.. views.Where(v => v.Count >= 4)];

        if (usable.Count < 3)
        {
            diagnostics.Add("可用视角不足，跳过非线性精化。");
            return init with { Diagnostics = diagnostics };
        }

        // 参数向量：fx, fy, cx, cy, k1, k2, p1, p2
        var parameters = new double[ParameterCount];
        parameters[0] = init.Intrinsics.Fx;
        parameters[1] = init.Intrinsics.Fy;
        parameters[2] = init.Intrinsics.Cx;
        parameters[3] = init.Intrinsics.Cy;
        parameters[4] = 0;
        parameters[5] = 0;
        parameters[6] = 0;
        parameters[7] = 0;

        Pose?[] poses = new Pose?[usable.Count];
        double previousCost = double.MaxValue;

        // 分阶段放开参数：先只估内参，再逐个放开畸变系数。
        //
        // 为什么必须分阶段：焦距与径向畸变在数学上强耦合（都能把四周像素"拉"进来），
        // 一次性放开 9 个参数很容易掉进"焦距偏大 + k 偏负"的谷底出不来。
        // 分阶段相当于给优化器一条连续的路。
        int[][] stages =
        [
            [0, 1, 2, 3],                    // fx, fy, cx, cy
            [0, 1, 2, 3, 4],                 // + k1
            [0, 1, 2, 3, 4, 5],              // + k2
            [0, 1, 2, 3, 4, 5, 6, 7],        // + p1, p2
            [0, 1, 2, 3, 4, 5, 6, 7, 8]      // + k3
        ];

        foreach (int[] active in stages)
        {
            for (int outer = 0; outer < MaxOuterIterations; outer++)
            {
            // ── a. 固定参数，估计每个视角的位姿 ──────────
            for (int i = 0; i < usable.Count; i++)
            {
                if (fixedPoses is not null)
                {
                    // 已知位姿：完全不动位姿
                    poses[i] = new Pose(fixedPoses[i].Rotation, [.. fixedPoses[i].Translation]);
                    continue;
                }

                Pose? estimated = EstimatePose(usable[i], parameters)
                                  ?? (outer > 0 ? poses[i] : null);

                // 关键：仅靠单应分解是有偏的（会把畸变误差吸收进位姿），
                // 必须再对位姿做 LM 优化，否则整体解会收敛到有偏的局部最优。
                if (estimated is not null)
                {
                    (CameraIntrinsics kNow, DistortionModel dNow) = Unpack(parameters);
                    estimated = OptimizePose(usable[i], estimated, kNow, dNow);
                }

                poses[i] = estimated;

                if (poses[i] is null)
                {
                    diagnostics.Add($"第 {i + 1} 个视角位姿估计失败，已跳过。");
                }
            }

            bool allPosesReady = poses.All(p => p is not null);
            if (!allPosesReady)
            {
                diagnostics.Add("存在无法估计位姿的视角，放弃精化，仅返回闭合解。");
                return init with { Diagnostics = diagnostics };
            }

                // ── b. 固定位姿，LM 优化（仅活跃参数）──────────
                double cost = OptimizeParameters(
                    usable, poses!, parameters, imageWidth, imageHeight, active);

                if (!double.IsFinite(cost))
                {
                    diagnostics.Add("精化过程中数值异常，保留上一步结果。");
                    break;
                }

                if (Math.Abs(previousCost - cost) < 1e-10) break;
                previousCost = cost;
            }
        }

        if (!IsPlausible(parameters, imageWidth, imageHeight))
        {
            diagnostics.Add("精化结果不合理（焦距/畸变超出合理范围），回退到闭合解。");
            return init with { Diagnostics = diagnostics };
        }

        var intrinsics = new CameraIntrinsics(
            parameters[0], parameters[1], parameters[2], parameters[3], imageWidth, imageHeight);

        var distortion = new DistortionModel(
            K1: parameters[4],
            K2: parameters[5],
            P1: parameters[6],
            P2: parameters[7],
            K3: parameters[8]);

        double rms = Math.Sqrt(Math.Max(previousCost, 0));

        diagnostics.Add(
            $"闭合解单应 RMSE {init.HomographyRmsePx:F3}px → 精化重投影 RMSE {rms:F3}px");

        return new IntrinsicsSolveResult(
            Ok: true,
            Intrinsics: intrinsics,
            HomographyRmsePx: init.HomographyRmsePx,
            Error: null,
            Diagnostics: diagnostics,
            Distortion: distortion,
            ReprojectionRmsePx: rms);
    }

    // ── 位姿估计 ────────────────────────────────────────────

    private Pose? EstimatePose(CalibrationView view, double[] parameters)
    {
        (CameraIntrinsics k, DistortionModel distortion) = Unpack(parameters);

        int n = view.Count;
        var objectPlane = new List<Point2>(n);
        var normalized = new List<Point2>(n);

        for (int i = 0; i < n; i++)
        {
            Point3 p = view.ObjectPoints[i];
            objectPlane.Add(new Point2(p.X, p.Y));

            Point2 uv = view.ImagePoints[i];
            double xn = (uv.X - k.Cx) / k.Fx;
            double yn = (uv.Y - k.Cy) / k.Fy;

            (double ux, double uy) = distortion.Undistort(xn, yn);
            normalized.Add(new Point2(ux, uy));
        }

        Homography? h = Homography.Compute(objectPlane, normalized);
        if (h is null) return null;

        double[,] m = h.Matrix;

        // 列向量
        double[] h1 = [m[0, 0], m[1, 0], m[2, 0]];
        double[] h2 = [m[0, 1], m[1, 1], m[2, 1]];
        double[] h3 = [m[0, 2], m[1, 2], m[2, 2]];

        double norm1 = Length(h1);
        double norm2 = Length(h2);

        if (norm1 < 1e-12 || norm2 < 1e-12) return null;

        double lambda = 2.0 / (norm1 + norm2);

        double[] r1 = [h1[0] * lambda, h1[1] * lambda, h1[2] * lambda];
        double[] r2 = [h2[0] * lambda, h2[1] * lambda, h2[2] * lambda];
        double[] t = [h3[0] * lambda, h3[1] * lambda, h3[2] * lambda];

        // 正交化
        r1 = Normalize(r1);
        double dot = Dot(r1, r2);
        r2 = Normalize([r2[0] - (dot * r1[0]), r2[1] - (dot * r1[1]), r2[2] - (dot * r1[2])]);

        double[] r3 = Cross(r1, r2);

        // 行列式应为 +1
        double det = (r1[0] * ((r2[1] * r3[2]) - (r2[2] * r3[1])))
                     - (r1[1] * ((r2[0] * r3[2]) - (r2[2] * r3[0])))
                     + (r1[2] * ((r2[0] * r3[1]) - (r2[1] * r3[0])));

        if (det < 0)
        {
            r2 = [-r2[0], -r2[1], -r2[2]];
            r3 = Cross(r1, r2);
            t = [-t[0], -t[1], -t[2]];
        }

        // 转成行优先矩阵：R[i,j] 表示第 i 行第 j 列
        var rotation = new double[3, 3];
        rotation[0, 0] = r1[0]; rotation[0, 1] = r2[0]; rotation[0, 2] = r3[0];
        rotation[1, 0] = r1[1]; rotation[1, 1] = r2[1]; rotation[1, 2] = r3[1];
        rotation[2, 0] = r1[2]; rotation[2, 1] = r2[2]; rotation[2, 2] = r3[2];

        if (t[2] <= 0) return null;   // 棋盘在相机后方

        return new Pose(rotation, t);
    }

    // ── 位姿 LM 优化 ────────────────────────────────────────

    /// <summary>
    /// 固定内参/畸变，优化单个视角的位姿 (R, t)。
    /// 增量参数化为 [ω(3), Δt(3)]：R ← R·exp([ω]×)，t ← t + Δt。
    /// </summary>
    private static Pose OptimizePose(
        CalibrationView view,
        Pose initial,
        CameraIntrinsics k,
        DistortionModel distortion)
    {
        const int dof = 6;

        double[,] rotation = initial.R;
        double[] translation = [.. initial.T];

        double damping = 1e-3;
        double cost = PoseCost(view, rotation, translation, k, distortion);

        for (int iteration = 0; iteration < 15; iteration++)
        {
            double[] residuals = PoseResiduals(view, rotation, translation, k, distortion);
            var normal = new double[dof, dof];
            var gradient = new double[dof];

            for (int a = 0; a < dof; a++)
            {
                double step = 1e-6;

                var plus = new double[dof];
                var minus = new double[dof];
                plus[a] = step;
                minus[a] = -step;

                double[] rPlus = ApplyDeltaResiduals(view, rotation, translation, plus, k, distortion);
                double[] rMinus = ApplyDeltaResiduals(view, rotation, translation, minus, k, distortion);

                for (int r = 0; r < residuals.Length; r++)
                {
                    double derivative = (rPlus[r] - rMinus[r]) / (2 * step);
                    gradient[a] += derivative * residuals[r];
                }

                // 二阶项单独算（保持清晰，避免上面循环里重复计算）
                for (int b = a; b < dof; b++)
                {
                    var pb = new double[dof];
                    var mb = new double[dof];
                    pb[b] = step;
                    mb[b] = -step;

                    double[] rpb = ApplyDeltaResiduals(view, rotation, translation, pb, k, distortion);
                    double[] rmb = ApplyDeltaResiduals(view, rotation, translation, mb, k, distortion);

                    double sum = 0;
                    for (int r = 0; r < residuals.Length; r++)
                    {
                        double da = (rPlus[r] - rMinus[r]) / (2 * step);
                        double db = (rpb[r] - rmb[r]) / (2 * step);
                        sum += da * db;
                    }

                    normal[a, b] = sum;
                    normal[b, a] = sum;
                }
            }

            for (int a = 0; a < dof; a++)
                normal[a, a] += damping * Math.Max(normal[a, a], 1e-9);

            double[]? delta = LinearSolver.Solve(normal, [.. gradient.Select(g => -g)]);
            if (delta is null)
            {
                damping *= 10;
                if (damping > 1e10) break;
                continue;
            }

            (double[,] candidateR, double[] candidateT) = ApplyDelta(rotation, translation, delta);
            double candidateCost = PoseCost(view, candidateR, candidateT, k, distortion);

            if (candidateCost < cost)
            {
                rotation = candidateR;
                translation = candidateT;
                cost = candidateCost;
                damping = Math.Max(damping / 3, 1e-9);

                if (MaxAbsolute(delta) < 1e-10) break;
            }
            else
            {
                damping *= 8;
                if (damping > 1e10) break;
            }
        }

        return new Pose(rotation, translation);
    }

    private static double[] ApplyDeltaResiduals(
        CalibrationView view,
        double[,] rotation,
        double[] translation,
        double[] delta,
        CameraIntrinsics k,
        DistortionModel distortion)
    {
        (double[,] r, double[] t) = ApplyDelta(rotation, translation, delta);
        return PoseResiduals(view, r, t, k, distortion);
    }

    private static (double[,] R, double[] T) ApplyDelta(
        double[,] rotation,
        double[] translation,
        double[] delta)
    {
        double[,] increment = Rodrigues([delta[0], delta[1], delta[2]]);
        double[,] r = Multiply3(rotation, increment);

        double[] t = [translation[0] + delta[3], translation[1] + delta[4], translation[2] + delta[5]];

        return (r, t);
    }

    private static double[] PoseResiduals(
        CalibrationView view,
        double[,] rotation,
        double[] translation,
        CameraIntrinsics k,
        DistortionModel distortion)
    {
        var pose = new Pose(rotation, translation);
        var residuals = new double[view.Count * 2];

        for (int j = 0; j < view.Count; j++)
        {
            (double u, double v) = Project(view.ObjectPoints[j], pose, k, distortion);
            residuals[(j * 2) + 0] = u - view.ImagePoints[j].X;
            residuals[(j * 2) + 1] = v - view.ImagePoints[j].Y;
        }

        return residuals;
    }

    private static double PoseCost(
        CalibrationView view,
        double[,] rotation,
        double[] translation,
        CameraIntrinsics k,
        DistortionModel distortion)
    {
        double[] residuals = PoseResiduals(view, rotation, translation, k, distortion);

        double sum = 0;
        foreach (double r in residuals) sum += r * r;

        return residuals.Length == 0 ? double.NaN : sum / residuals.Length;
    }

    /// <summary>罗德里格斯公式：轴角 → 旋转矩阵。</summary>
    private static double[,] Rodrigues(double[] omega)
    {
        double theta = Math.Sqrt((omega[0] * omega[0]) + (omega[1] * omega[1]) + (omega[2] * omega[2]));

        if (theta < 1e-12)
        {
            return new double[3, 3]
            {
                { 1, -omega[2], omega[1] },
                { omega[2], 1, -omega[0] },
                { -omega[1], omega[0], 1 }
            };
        }

        double kx = omega[0] / theta, ky = omega[1] / theta, kz = omega[2] / theta;
        double c = Math.Cos(theta), s = Math.Sin(theta), v = 1 - c;

        return new double[3, 3]
        {
            { (kx * kx * v) + c, (kx * ky * v) - (kz * s), (kx * kz * v) + (ky * s) },
            { (ky * kx * v) + (kz * s), (ky * ky * v) + c, (ky * kz * v) - (kx * s) },
            { (kz * kx * v) - (ky * s), (kz * ky * v) + (kx * s), (kz * kz * v) + c }
        };
    }

    private static double[,] Multiply3(double[,] a, double[,] b)
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
    // ── LM 参数优化 ─────────────────────────────────────────

    private static double OptimizeParameters(
        IReadOnlyList<CalibrationView> views,
        Pose?[] poses,
        double[] parameters,
        int width,
        int height,
        IReadOnlyList<int>? active = null)
    {
        int[] indices = active is null ? [.. Enumerable.Range(0, ParameterCount)] : [.. active];
        int dof = indices.Length;

        double damping = 1e-3;
        double cost = ComputeCost(views, poses, parameters);

        for (int iteration = 0; iteration < MaxInnerIterations; iteration++)
        {
            double[,] jacobian = BuildJacobian(views, poses, parameters);
            double[] residuals = BuildResiduals(views, poses, parameters);

            int rows = residuals.Length;
            var normal = new double[dof, dof];
            var gradient = new double[dof];

            for (int r = 0; r < rows; r++)
            {
                for (int a = 0; a < dof; a++)
                {
                    double ja = jacobian[r, indices[a]];
                    if (ja == 0) continue;

                    gradient[a] += ja * residuals[r];

                    for (int b = a; b < dof; b++)
                        normal[a, b] += ja * jacobian[r, indices[b]];
                }
            }

            for (int a = 0; a < dof; a++)
                for (int b = 0; b < a; b++)
                    normal[a, b] = normal[b, a];

            for (int a = 0; a < dof; a++)
                normal[a, a] += damping * Math.Max(normal[a, a], 1e-9);

            double[] rhs = [.. gradient.Select(g => -g)];

            double[]? delta = LinearSolver.Solve(normal, rhs);
            if (delta is null)
            {
                damping *= 10;
                if (damping > 1e10) break;
                continue;
            }

            var candidate = (double[])parameters.Clone();
            for (int a = 0; a < dof; a++) candidate[indices[a]] = parameters[indices[a]] + delta[a];

            if (!IsPlausible(candidate, width, height))
            {
                damping *= 10;
                continue;
            }

            double candidateCost = ComputeCost(views, poses, candidate);

            if (candidateCost < cost)
            {
                Array.Copy(candidate, parameters, ParameterCount);
                cost = candidateCost;
                damping = Math.Max(damping / 3, 1e-9);

                if (Math.Abs(MaxAbsolute(delta)) < 1e-9) break;
            }
            else
            {
                damping *= 8;
                if (damping > 1e10) break;
            }
        }

        return cost;
    }

    private static double[] BuildResiduals(
        IReadOnlyList<CalibrationView> views,
        Pose?[] poses,
        double[] parameters)
    {
        var residuals = new List<double>(views.Sum(v => v.Count) * 2);
        (CameraIntrinsics k, DistortionModel d) = Unpack(parameters);

        for (int i = 0; i < views.Count; i++)
        {
            Pose pose = poses[i]!;
            CalibrationView view = views[i];

            for (int j = 0; j < view.Count; j++)
            {
                (double u, double v) = Project(view.ObjectPoints[j], pose, k, d);
                residuals.Add(u - view.ImagePoints[j].X);
                residuals.Add(v - view.ImagePoints[j].Y);
            }
        }

        return [.. residuals];
    }

    private static double[,] BuildJacobian(
        IReadOnlyList<CalibrationView> views,
        Pose?[] poses,
        double[] parameters)
    {
        double[] baseline = BuildResiduals(views, poses, parameters);
        var jacobian = new double[baseline.Length, ParameterCount];

        for (int a = 0; a < ParameterCount; a++)
        {
            double step = Math.Abs(parameters[a]) * 1e-6 + 1e-7;

            var forward = (double[])parameters.Clone();
            var backward = (double[])parameters.Clone();

            forward[a] += step;
            backward[a] -= step;

            double[] rf = BuildResiduals(views, poses, forward);
            double[] rb = BuildResiduals(views, poses, backward);

            for (int r = 0; r < baseline.Length; r++)
                jacobian[r, a] = (rf[r] - rb[r]) / (2 * step);
        }

        return jacobian;
    }

    private static double ComputeCost(
        IReadOnlyList<CalibrationView> views,
        Pose?[] poses,
        double[] parameters)
    {
        (CameraIntrinsics k, DistortionModel d) = Unpack(parameters);

        double sum = 0;
        long count = 0;

        for (int i = 0; i < views.Count; i++)
        {
            Pose pose = poses[i]!;
            CalibrationView view = views[i];

            for (int j = 0; j < view.Count; j++)
            {
                (double u, double v) = Project(view.ObjectPoints[j], pose, k, d);

                double dx = u - view.ImagePoints[j].X;
                double dy = v - view.ImagePoints[j].Y;

                sum += (dx * dx) + (dy * dy);
                count += 2;
            }
        }

        return count == 0 ? double.NaN : sum / count;
    }

    /// <summary>用完整模型把物点投影到像素坐标。</summary>
    private static (double U, double V) Project(
        Point3 point,
        Pose pose,
        CameraIntrinsics k,
        DistortionModel distortion)
    {
        double xc = (pose.R[0, 0] * point.X) + (pose.R[0, 1] * point.Y) + (pose.R[0, 2] * point.Z) + pose.T[0];
        double yc = (pose.R[1, 0] * point.X) + (pose.R[1, 1] * point.Y) + (pose.R[1, 2] * point.Z) + pose.T[1];
        double zc = (pose.R[2, 0] * point.X) + (pose.R[2, 1] * point.Y) + (pose.R[2, 2] * point.Z) + pose.T[2];

        if (Math.Abs(zc) < 1e-9) return (double.NaN, double.NaN);

        double xn = xc / zc;
        double yn = yc / zc;

        (double xd, double yd) = distortion.Distort(xn, yn);

        return ((k.Fx * xd) + k.Cx, (k.Fy * yd) + k.Cy);
    }

    private static (CameraIntrinsics Intrinsics, DistortionModel Distortion) Unpack(double[] parameters)
    {
        var k = new CameraIntrinsics(parameters[0], parameters[1], parameters[2], parameters[3], 0, 0);

        var d = new DistortionModel(
            K1: parameters[4],
            K2: parameters[5],
            P1: parameters[6],
            P2: parameters[7],
            K3: parameters[8]);

        return (k, d);
    }
    private static bool IsPlausible(double[] parameters, int width, int height)
    {
        double fx = parameters[0], fy = parameters[1], cx = parameters[2], cy = parameters[3];

        if (!double.IsFinite(fx) || !double.IsFinite(fy)) return false;
        if (fx <= 1 || fy <= 1) return false;

        // 焦距相对图像尺寸的合理范围
        double maxSide = Math.Max(width, height);
        if (fx > maxSide * 50 || fy > maxSide * 50) return false;

        // 主点不应飞出图像太远
        if (Math.Abs(cx) > width * 2 || Math.Abs(cy) > height * 2) return false;

        // 畸变系数不应爆炸
        for (int i = 4; i < ParameterCount; i++)
        {
            if (!double.IsFinite(parameters[i])) return false;
            if (Math.Abs(parameters[i]) > 10) return false;
        }

        return true;
    }

    // ── 向量工具 ────────────────────────────────────────────

    private static double Length(double[] v) => Math.Sqrt((v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]));

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Normalize(double[] v)
    {
        double length = Length(v);
        return length < 1e-12 ? v : [v[0] / length, v[1] / length, v[2] / length];
    }

    private static double[] Cross(double[] a, double[] b) =>
    [
        (a[1] * b[2]) - (a[2] * b[1]),
        (a[2] * b[0]) - (a[0] * b[2]),
        (a[0] * b[1]) - (a[1] * b[0])
    ];

    private static double MaxAbsolute(double[] values)
    {
        double max = 0;

        foreach (double v in values)
        {
            double mapped = Math.Abs(v);
            if (mapped > max) max = mapped;
        }

        return max;
    }
}
