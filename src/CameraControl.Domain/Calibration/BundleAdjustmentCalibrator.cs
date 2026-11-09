namespace CameraControl.Domain.Calibration;

/// <summary>
/// 联合光束法平差（Bundle Adjustment）标定器。
///
/// 与 <see cref="RefiningCalibrator"/> 的区别：
/// 后者用"交替优化"（先位姿、再相机参数），位姿误差会被固化；
/// 这里把**相机参数 9 个 + 每个视角位姿 6 个**放进同一个参数向量，
/// 一次性做 Levenberg-Marquardt，这才是教科书意义上的 BA。
///
/// 参数化：
/// - 相机：<c>[fx, fy, cx, cy, k1, k2, p1, p2, k3]</c>
/// - 视角 i：<c>[ω(3), Δt(3)]</c>，位姿按 <c>R_i = R0_i·exp([ω]×)</c>、<c>t_i = t0_i + Δt</c> 求值
///   （R0/t0 是单应分解得到的初值，参数全零即初值，避免增量累积漂移）
///
/// 效率：每个残差只依赖 15 个参数（9 相机 + 6 本视角位姿），
/// 因此正规方程稀疏累加即可，19 视角每轮也只有几十万次运算，不需要通用稀疏矩阵库。
/// </summary>
public sealed class BundleAdjustmentCalibrator : ICameraCalibrator
{
    private const int CameraParamCount = 9;
    private const int PoseParamCount = 6;
    private const int RowParamCount = CameraParamCount + PoseParamCount;

    private readonly ZhangCalibrator _closedForm;
    private readonly int _maxIterations;
    private readonly int _maxViews;

    /// <param name="maxIterations">LM 最大迭代次数。</param>
    /// <param name="maxViews">最多用多少个视角（0 = 不限）。</param>
    public BundleAdjustmentCalibrator(int maxIterations = 40, int maxViews = 0)
    {
        _closedForm = new ZhangCalibrator();
        _maxIterations = Math.Clamp(maxIterations, 1, 200);
        _maxViews = maxViews;
    }

    public IntrinsicsSolveResult Solve(
        IReadOnlyList<CalibrationView> views,
        ChessboardSpec spec,
        int imageWidth,
        int imageHeight)
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
            diagnostics.Add("可用视角不足 3 个，跳过 BA。");
            return init with { Diagnostics = diagnostics };
        }

        // ── 初值：相机取闭合解，位姿由单应分解 ─────────────────
        var camera = new double[CameraParamCount];
        camera[0] = init.Intrinsics.Fx;
        camera[1] = init.Intrinsics.Fy;
        camera[2] = init.Intrinsics.Cx;
        camera[3] = init.Intrinsics.Cy;

        var baseRotations = new double[usable.Count][,];
        var baseTranslations = new double[usable.Count][];

        CameraIntrinsics initK = init.Intrinsics;

        for (int i = 0; i < usable.Count; i++)
        {
            (double[,] R, double[] T)? pose = PoseEstimator.Estimate(usable[i], initK, DistortionModel.Zero);

            if (pose is null)
            {
                diagnostics.Add($"第 {i + 1} 个视角位姿初始化失败，跳过 BA。");
                return init with { Diagnostics = diagnostics };
            }

            baseRotations[i] = pose.Value.R;
            baseTranslations[i] = pose.Value.T;
        }

        // ── 参数向量 ──────────────────────────────────────────
        int parameterCount = CameraParamCount + (PoseParamCount * usable.Count);
        var x = new double[parameterCount];
        Array.Copy(camera, x, CameraParamCount);

        var problems = new List<Problem>(usable.Count);

        for (int i = 0; i < usable.Count; i++)
            problems.Add(new Problem(usable[i], baseRotations[i], baseTranslations[i]));

        double cost = ComputeCost(x, problems, imageWidth, imageHeight);

        if (!double.IsFinite(cost))
        {
            diagnostics.Add("初值代价异常，跳过 BA。");
            return init with { Diagnostics = diagnostics };
        }

        // ── LM ────────────────────────────────────────────────
        double damping = 1e-3;
        int accepted = 0;

        for (int iteration = 0; iteration < _maxIterations; iteration++)
        {
            (double[,] normal, double[] gradient) = BuildNormalEquations(x, problems, imageWidth, imageHeight);

            for (int a = 0; a < parameterCount; a++)
                normal[a, a] += damping * Math.Max(normal[a, a], 1e-12);

            double[] rhs = [.. gradient.Select(g => -g)];

            double[]? delta = LinearSolver.Solve(normal, rhs);

            if (delta is null)
            {
                damping *= 10;
                if (damping > 1e12) break;
                continue;
            }

            var candidate = new double[parameterCount];
            for (int a = 0; a < parameterCount; a++) candidate[a] = x[a] + delta[a];

            if (!IsPlausibleCamera(candidate, imageWidth, imageHeight))
            {
                damping *= 10;
                continue;
            }

            double candidateCost = ComputeCost(candidate, problems, imageWidth, imageHeight);

            if (candidateCost < cost)
            {
                Array.Copy(candidate, x, parameterCount);
                cost = candidateCost;
                damping = Math.Max(damping / 3, 1e-10);
                accepted++;

                // 增量足够小即认为收敛
                double maxDelta = 0;
                for (int a = 0; a < parameterCount; a++) maxDelta = Math.Max(maxDelta, Math.Abs(delta[a]));

                if (maxDelta < 1e-9) break;
            }
            else
            {
                damping *= 8;
                if (damping > 1e12) break;
            }
        }

        if (!IsPlausibleCamera(x, imageWidth, imageHeight))
        {
            diagnostics.Add("BA 结果不合理，回退闭合解。");
            return init with { Diagnostics = diagnostics };
        }

        var intrinsics = new CameraIntrinsics(x[0], x[1], x[2], x[3], imageWidth, imageHeight);

        var distortion = new DistortionModel(
            K1: x[4],
            K2: x[5],
            P1: x[6],
            P2: x[7],
            K3: x[8]);

        double rms = Math.Sqrt(Math.Max(cost, 0));

        diagnostics.Add(
            $"BA：{usable.Count} 视角 / {parameterCount} 参数 / {accepted} 次接受，" +
            $"重投影 RMSE {rms:F3}px（闭合解单应 RMSE {init.HomographyRmsePx:F3}px）");

        return new IntrinsicsSolveResult(
            Ok: true,
            Intrinsics: intrinsics,
            HomographyRmsePx: init.HomographyRmsePx,
            Error: null,
            Diagnostics: diagnostics,
            Distortion: distortion,
            ReprojectionRmsePx: rms);
    }

    /// <summary>
    /// 详细结果：内参 + 畸变 + **每个视角的位姿**。
    /// 手眼标定需要这些位姿（B = D_j·D_i⁻¹）。
    /// </summary>
    /// <param name="Intrinsics">内参。</param>
    /// <param name="Distortion">畸变。</param>
    /// <param name="Poses">各视角的位姿（靶标→相机），顺序与输入视角一致。</param>
    /// <param name="ReprojectionRmsePx">重投影 RMS（按坐标分量归一化）。</param>
    /// <param name="Diagnostics">诊断信息。</param>
    public sealed record DetailedCalibrationResult(
        CameraIntrinsics Intrinsics,
        DistortionModel Distortion,
        IReadOnlyList<Transform3D> Poses,
        double ReprojectionRmsePx,
        IReadOnlyList<string> Diagnostics);

    /// <summary>
    /// 求解并返回每个视角的位姿。
    ///
    /// 位姿由**最终标定参数**重新分解单应得到（而非 BA 内部迭代值），
    /// 因此与返回的内参/畸变是自洽的。
    /// </summary>
    public DetailedCalibrationResult? SolveDetailed(
        IReadOnlyList<CalibrationView> views,
        ChessboardSpec spec,
        int imageWidth,
        int imageHeight)
    {
        IntrinsicsSolveResult result = Solve(views, spec, imageWidth, imageHeight);

        if (!result.Ok || result.Intrinsics is null) return null;

        DistortionModel distortion = result.Distortion ?? DistortionModel.Zero;
        var poses = new List<Transform3D>(views.Count);

        foreach (CalibrationView view in views)
        {
            if (view.Count < 4) continue;

            (double[,] R, double[] T)? pose = PoseEstimator.Estimate(view, result.Intrinsics, distortion);
            if (pose is null) continue;

            poses.Add(new Transform3D(pose.Value.R, pose.Value.T));
        }

        return new DetailedCalibrationResult(
            result.Intrinsics,
            distortion,
            poses,
            result.ReprojectionRmsePx,
            result.Diagnostics ?? []);
    }
    /// <summary>一个视角的静态信息（观测 + 位姿初值）。</summary>
    private sealed record Problem(CalibrationView View, double[,] BaseRotation, double[] BaseTranslation);

    // ── 模型求值 ────────────────────────────────────────────

    private static (double[,] R, double[] T) PoseOf(double[] x, Problem problem, int viewIndex)
    {
        int offset = CameraParamCount + (PoseParamCount * viewIndex);

        double[] omega = [x[offset], x[offset + 1], x[offset + 2]];
        double[,] r = PoseEstimator.Multiply3(problem.BaseRotation, PoseEstimator.Rodrigues(omega));

        double[] t =
        [
            problem.BaseTranslation[0] + x[offset + 3],
            problem.BaseTranslation[1] + x[offset + 4],
            problem.BaseTranslation[2] + x[offset + 5]
        ];

        return (r, t);
    }

    private static CameraIntrinsics CameraOf(double[] x) =>
        new(x[0], x[1], x[2], x[3], 0, 0);

    private static DistortionModel DistortionOf(double[] x) =>
        new(K1: x[4], K2: x[5], P1: x[6], P2: x[7], K3: x[8]);

    private static double ComputeCost(
        double[] x,
        IReadOnlyList<Problem> problems,
        int width,
        int height)
    {
        CameraIntrinsics k = CameraOf(x);
        DistortionModel d = DistortionOf(x);

        double sum = 0;
        long count = 0;

        for (int i = 0; i < problems.Count; i++)
        {
            Problem problem = problems[i];
            (double[,] r, double[] t) = PoseOf(x, problem, i);

            for (int j = 0; j < problem.View.Count; j++)
            {
                (double u, double v) = PoseEstimator.Project(problem.View.ObjectPoints[j], r, t, k, d);

                double dx = u - problem.View.ImagePoints[j].X;
                double dy = v - problem.View.ImagePoints[j].Y;

                sum += (dx * dx) + (dy * dy);
                count += 2;
            }
        }

        return count == 0 ? double.NaN : sum / count;
    }

    /// <summary>
    /// 稀疏累加正规方程 JᵀJ 与 Jᵀr。
    ///
    /// 每个残差行只涉及 15 个参数（相机 9 + 本视角位姿 6），
    /// 所以每行只需 15 个数值偏导，累加到对应位置即可。
    /// </summary>
    private static (double[,] Normal, double[] Gradient) BuildNormalEquations(
        double[] x,
        IReadOnlyList<Problem> problems,
        int width,
        int height)
    {
        int parameterCount = x.Length;
        var normal = new double[parameterCount, parameterCount];
        var gradient = new double[parameterCount];

        CameraIntrinsics k = CameraOf(x);
        DistortionModel d = DistortionOf(x);

        var localIndices = new int[RowParamCount];
        var partialU = new double[RowParamCount];
        var partialV = new double[RowParamCount];

        for (int a = 0; a < CameraParamCount; a++) localIndices[a] = a;

        for (int i = 0; i < problems.Count; i++)
        {
            Problem problem = problems[i];
            (double[,] r, double[] t) = PoseOf(x, problem, i);

            int poseOffset = CameraParamCount + (PoseParamCount * i);
            for (int a = 0; a < PoseParamCount; a++) localIndices[CameraParamCount + a] = poseOffset + a;

            for (int j = 0; j < problem.View.Count; j++)
            {
                Point3 point = problem.View.ObjectPoints[j];
                Point2 observed = problem.View.ImagePoints[j];

                for (int a = 0; a < RowParamCount; a++)
                {
                    int index = localIndices[a];
                    double step = StepFor(index, x[index]);

                    // 只扰动单参数，重建模型
                    x[index] += step;
                    (double up, double vp) = EvaluatePoint(x, problem, i, point);
                    x[index] -= 2 * step;
                    (double um, double vm) = EvaluatePoint(x, problem, i, point);
                    x[index] += step;

                    partialU[a] = (up - um) / (2 * step);
                    partialV[a] = (vp - vm) / (2 * step);
                }

                double residualU = 0, residualV = 0;

                {
                    (double u, double v) = PoseEstimator.Project(point, r, t, k, d);
                    residualU = u - observed.X;
                    residualV = v - observed.Y;
                }

                for (int a = 0; a < RowParamCount; a++)
                {
                    int ia = localIndices[a];

                    gradient[ia] += (partialU[a] * residualU) + (partialV[a] * residualV);

                    for (int b = a; b < RowParamCount; b++)
                    {
                        int ib = localIndices[b];

                        double value = (partialU[a] * partialU[b]) + (partialV[a] * partialV[b]);
                        normal[ia, ib] += value;

                        if (ia != ib) normal[ib, ia] += value;
                    }
                }
            }
        }

        return (normal, gradient);
    }

    /// <summary>用当前参数向量投影单个点（供数值偏导用）。</summary>
    private static (double U, double V) EvaluatePoint(
        double[] x,
        Problem problem,
        int viewIndex,
        Point3 point)
    {
        (double[,] r, double[] t) = PoseOf(x, problem, viewIndex);
        return PoseEstimator.Project(point, r, t, CameraOf(x), DistortionOf(x));
    }

    /// <summary>不同参数类型用不同量级的差分步长。</summary>
    private static double StepFor(int index, double value)
    {
        if (index < 4)
        {
            // 焦距/主点：相对步长
            return (Math.Abs(value) * 1e-6) + 1e-7;
        }

        if (index < CameraParamCount)
        {
            // 畸变系数
            return 1e-7;
        }

        int poseIndex = (index - CameraParamCount) % PoseParamCount;

        return poseIndex < 3
            ? 1e-7      // 旋转增量（弧度）
            : 1e-5;     // 平移增量（mm）
    }

    private static bool IsPlausibleCamera(double[] x, int width, int height)
    {
        double fx = x[0], fy = x[1], cx = x[2], cy = x[3];

        if (!double.IsFinite(fx) || !double.IsFinite(fy) || fx <= 1 || fy <= 1) return false;

        double maxSide = Math.Max(width, height);
        if (fx > maxSide * 50 || fy > maxSide * 50) return false;

        if (Math.Abs(cx) > width * 2 || Math.Abs(cy) > height * 2) return false;

        for (int i = 4; i < CameraParamCount; i++)
        {
            if (!double.IsFinite(x[i])) return false;
            if (Math.Abs(x[i]) > 10) return false;
        }

        return true;
    }
}
