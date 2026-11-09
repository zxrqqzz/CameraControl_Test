namespace CameraControl.Domain.Calibration;

/// <summary>一个手眼标定采样：机器人位姿 + 该位姿下的相机位姿。</summary>
/// <param name="GripperInBase">末端法兰在机器人基坐标系的位姿。</param>
/// <param name="TargetToCamera">标定板（世界/靶标坐标系）到相机的变换，即 BA 输出的位姿。</param>
public sealed record HandEyeSample(Transform3D GripperInBase, Transform3D TargetToCamera);

/// <summary>手眼标定结果。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="CameraInGripper">相机相对末端法兰的变换（要求的 X）。</param>
/// <param name="RotationErrorDeg">各采样对旋转残差的最大值（度）。</param>
/// <param name="TranslationErrorMm">各采样对平移残差的最大值（mm）。</param>
/// <param name="PairCount">参与求解的运动对数量。</param>
/// <param name="Error">失败原因。</param>
/// <param name="Diagnostics">诊断信息。</param>
public sealed record HandEyeResult(
    bool Ok,
    Transform3D? CameraInGripper,
    double RotationErrorDeg,
    double TranslationErrorMm,
    int PairCount,
    string? Error = null,
    IReadOnlyList<string>? Diagnostics = null)
{
    public static HandEyeResult Failure(string error, IReadOnlyList<string>? diagnostics = null) =>
        new(false, null, double.NaN, double.NaN, 0, error, diagnostics);
}

/// <summary>
/// 手眼标定：求解 AX = XB 中的 X（相机相对末端法兰的变换）。
///
/// 记号：
/// - <c>A_ij = T_robot_j⁻¹ · T_robot_i</c> —— 末端法兰的相对运动；
/// - <c>B_ij = D_j · D_i⁻¹</c> —— 相机的相对运动（D 为"靶标→相机"变换，正是 BA 的输出）；
/// - <c>X</c> —— 待求的相机在法兰上的位姿。
///
/// 解法（Tsai/Lenz 思路，四元数版）：
/// 1. 旋转：<c>q_A ⊗ q_X = q_X ⊗ q_B</c> 是关于 q_X 的**线性**齐次方程，
///    用 <c>(L(q_A) − R(q_B)) q_X = 0</c> 堆叠后取最小特征向量；
/// 2. 平移：由 <c>A X = X B</c> 得 <c>(R_A − I) t_X = R_X t_B − t_A</c>，最小二乘求解。
///
/// 为什么用四元数而不是轴角：四元数形式对噪声更稳，且不用处理 180° 奇点。
/// </summary>
public static class HandEyeCalibrator
{
    /// <summary>相对旋转小于此角度（度）的运动对信息量太低，直接丢弃。</summary>
    private const double MinRotationDegrees = 3.0;

    public static HandEyeResult Calibrate(IReadOnlyList<HandEyeSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count < 3)
            return HandEyeResult.Failure($"至少需要 3 个采样（建议 10 个以上不同姿态），当前 {samples.Count} 个。");

        var diagnostics = new List<string>();

        // ── 构造运动对 ──────────────────────────────────────
        var rotationsA = new List<double[,]>();
        var translationsA = new List<double[]>();
        var rotationsB = new List<double[,]>();
        var translationsB = new List<double[]>();

        for (int i = 0; i < samples.Count; i++)
        {
            for (int j = i + 1; j < samples.Count; j++)
            {
                Transform3D a = samples[j].GripperInBase.Inverse().Multiply(samples[i].GripperInBase);
                Transform3D b = samples[j].TargetToCamera.Multiply(samples[i].TargetToCamera.Inverse());

                // 旋转太小 → 该对几乎不含旋转信息，会污染解
                double angle = RotationAngleDegrees(a.Rotation);
                if (angle < MinRotationDegrees) continue;

                rotationsA.Add(a.Rotation);
                translationsA.Add(a.Translation);
                rotationsB.Add(b.Rotation);
                translationsB.Add(b.Translation);
            }
        }

        if (rotationsA.Count < 3)
        {
            return HandEyeResult.Failure(
                $"有效运动对不足（{rotationsA.Count} 对）。请让机器人做**旋转差异大**的姿态变化。",
                diagnostics);
        }

        // ── 1. 旋转：解 (L(qA) − R(qB)) qX = 0 ───────────────
        var ata = new double[4, 4];

        for (int k = 0; k < rotationsA.Count; k++)
        {
            double[] qA = new Transform3D(rotationsA[k], [0, 0, 0]).ToQuaternion();
            double[] qB = new Transform3D(rotationsB[k], [0, 0, 0]).ToQuaternion();

            double[,] l = Transform3D.LeftQuaternionMatrix(qA);
            double[,] r = Transform3D.RightQuaternionMatrix(qB);

            var diff = new double[4, 4];
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    diff[row, col] = l[row, col] - r[row, col];

            // 累加 diffᵀ·diff
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 4; col++)
                {
                    double sum = 0;
                    for (int t = 0; t < 4; t++) sum += diff[t, row] * diff[t, col];
                    ata[row, col] += sum;
                }
            }
        }

        double[]? qX = SymmetricEigen.SmallestEigenvector(ata);

        if (qX is null)
            return HandEyeResult.Failure("旋转求解失败（运动对退化，请增加姿态多样性）。", diagnostics);

        // 约定 w ≥ 0，避免同一旋转出现两个符号解
        if (qX[0] < 0) qX = [-qX[0], -qX[1], -qX[2], -qX[3]];

        double[,] rotationX = Transform3D.QuaternionToMatrix(qX);

        // ── 2. 平移：(R_A − I) t_X = R_X t_B − t_A ───────────
        var normal = new double[3, 3];
        var rhs = new double[3];

        for (int k = 0; k < rotationsA.Count; k++)
        {
            double[,] m = SubtractIdentity(rotationsA[k]);

            double[] rXtB =
            [
                (rotationX[0, 0] * translationsB[k][0]) + (rotationX[0, 1] * translationsB[k][1]) + (rotationX[0, 2] * translationsB[k][2]),
                (rotationX[1, 0] * translationsB[k][0]) + (rotationX[1, 1] * translationsB[k][1]) + (rotationX[1, 2] * translationsB[k][2]),
                (rotationX[2, 0] * translationsB[k][0]) + (rotationX[2, 1] * translationsB[k][1]) + (rotationX[2, 2] * translationsB[k][2])
            ];

            double[] right =
            [
                rXtB[0] - translationsA[k][0],
                rXtB[1] - translationsA[k][1],
                rXtB[2] - translationsA[k][2]
            ];

            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    double sum = 0;
                    for (int t = 0; t < 3; t++) sum += m[t, row] * m[t, col];
                    normal[row, col] += sum;
                }

                double gradient = 0;
                for (int t = 0; t < 3; t++) gradient += m[t, row] * right[t];
                rhs[row] += gradient;
            }
        }

        double[]? tX = LinearSolver.Solve(normal, rhs);

        if (tX is null)
            return HandEyeResult.Failure("平移求解失败（方程退化，需要更多不同方向的平移运动）。", diagnostics);

        var x = new Transform3D(rotationX, tX);

        // ── 3. 残差评估 ──────────────────────────────────────
        double maxRotationError = 0;
        double maxTranslationError = 0;

        for (int k = 0; k < rotationsA.Count; k++)
        {
            // 残差：A X 与 X B 的差
            Transform3D left = new Transform3D(rotationsA[k], translationsA[k]).Multiply(x);
            Transform3D right = x.Multiply(new Transform3D(rotationsB[k], translationsB[k]));

            Transform3D delta = left.Multiply(right.Inverse());

            maxRotationError = Math.Max(maxRotationError, RotationAngleDegrees(delta.Rotation));

            double translationNorm = Math.Sqrt(
                (delta.Translation[0] * delta.Translation[0])
                + (delta.Translation[1] * delta.Translation[1])
                + (delta.Translation[2] * delta.Translation[2]));

            maxTranslationError = Math.Max(maxTranslationError, translationNorm);
        }

        diagnostics.Add($"{rotationsA.Count} 个有效运动对；最大残差 {maxRotationError:F4}° / {maxTranslationError:F4} mm");

        if (maxRotationError > 2.0)
            diagnostics.Add("旋转残差偏大（>2°），可能采样姿态过于单一或机器人位姿有误。");

        return new HandEyeResult(true, x, maxRotationError, maxTranslationError, rotationsA.Count, null, diagnostics);
    }

    /// <summary>旋转矩阵对应的旋转角（度）。</summary>
    public static double RotationAngleDegrees(double[,] rotation)
    {
        double trace = rotation[0, 0] + rotation[1, 1] + rotation[2, 2];
        double cosine = Math.Clamp((trace - 1) / 2, -1.0, 1.0);

        return Math.Acos(cosine) * 180 / Math.PI;
    }

    private static double[,] SubtractIdentity(double[,] rotation) => new double[3, 3]
    {
        { rotation[0, 0] - 1, rotation[0, 1], rotation[0, 2] },
        { rotation[1, 0], rotation[1, 1] - 1, rotation[1, 2] },
        { rotation[2, 0], rotation[2, 1], rotation[2, 2] - 1 }
    };
}
