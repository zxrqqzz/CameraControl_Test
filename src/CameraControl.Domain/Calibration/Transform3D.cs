namespace CameraControl.Domain.Calibration;

/// <summary>刚体变换（旋转 + 平移），一律表示"把点从 A 系变到 B 系"的 4×4 变换。</summary>
public sealed record Transform3D(double[,] Rotation, double[] Translation)
{
    public static Transform3D Identity => new(
        new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } },
        [0, 0, 0]);

    /// <summary>
    /// 机器人欧拉角约定。
    ///
    /// 工业机器人常见 ZYX（先绕 X 滚转、再绕 Y 俯仰、最后绕 Z 偏航）：
    /// <c>R = Rz(rz)·Ry(ry)·Rx(rx)</c>。
    /// 不同品牌约定不同，接具体机器人时必须核对，否则手眼结果会整机偏转。
    /// </summary>
    public enum EulerConvention
    {
        /// <summary>R = Rz·Ry·Rx（默认，多数六轴机器人）。</summary>
        Zyx = 0,

        /// <summary>R = Rx·Ry·Rz。</summary>
        Xyz = 1
    }

    /// <summary>由欧拉角（度）+ 平移构造。</summary>
    public static Transform3D FromEulerDegrees(
        double x, double y, double z,
        double rx, double ry, double rz,
        EulerConvention convention = EulerConvention.Zyx)
    {
        double a = rx * Math.PI / 180, b = ry * Math.PI / 180, c = rz * Math.PI / 180;

        double[,] rX =
        {
            { 1, 0, 0 },
            { 0, Math.Cos(a), -Math.Sin(a) },
            { 0, Math.Sin(a), Math.Cos(a) }
        };

        double[,] rY =
        {
            { Math.Cos(b), 0, Math.Sin(b) },
            { 0, 1, 0 },
            { -Math.Sin(b), 0, Math.Cos(b) }
        };

        double[,] rZ =
        {
            { Math.Cos(c), -Math.Sin(c), 0 },
            { Math.Sin(c), Math.Cos(c), 0 },
            { 0, 0, 1 }
        };

        double[,] rotation = convention == EulerConvention.Zyx
            ? Multiply(rZ, Multiply(rY, rX))
            : Multiply(rX, Multiply(rY, rZ));

        return new Transform3D(rotation, [x, y, z]);
    }

    /// <summary>复合：先应用 this，再应用 other（即 other·this）。</summary>
    public Transform3D Multiply(Transform3D other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new Transform3D(
            Multiply(Rotation, other.Rotation),
            [
                (Rotation[0, 0] * other.Translation[0]) + (Rotation[0, 1] * other.Translation[1]) + (Rotation[0, 2] * other.Translation[2]) + Translation[0],
                (Rotation[1, 0] * other.Translation[0]) + (Rotation[1, 1] * other.Translation[1]) + (Rotation[1, 2] * other.Translation[2]) + Translation[1],
                (Rotation[2, 0] * other.Translation[0]) + (Rotation[2, 1] * other.Translation[1]) + (Rotation[2, 2] * other.Translation[2]) + Translation[2]
            ]);
    }

    /// <summary>逆变换。</summary>
    public Transform3D Inverse()
    {
        double[,] rt = Transpose(Rotation);

        return new Transform3D(rt,
        [
            -((rt[0, 0] * Translation[0]) + (rt[0, 1] * Translation[1]) + (rt[0, 2] * Translation[2])),
            -((rt[1, 0] * Translation[0]) + (rt[1, 1] * Translation[1]) + (rt[1, 2] * Translation[2])),
            -((rt[2, 0] * Translation[0]) + (rt[2, 1] * Translation[1]) + (rt[2, 2] * Translation[2]))
        ]);
    }

    // ── 四元数互转（手眼标定用四元数解旋转，最稳）─────────────

    /// <summary>旋转矩阵 → 单位四元数 (w, x, y, z)，用 Shepperd 方法保证数值稳定。</summary>
    public double[] ToQuaternion()
    {
        double[,] r = Rotation;
        double trace = r[0, 0] + r[1, 1] + r[2, 2];

        double w, x, y, z;

        if (trace > 0)
        {
            double s = Math.Sqrt(trace + 1.0) * 2;
            w = 0.25 * s;
            x = (r[2, 1] - r[1, 2]) / s;
            y = (r[0, 2] - r[2, 0]) / s;
            z = (r[1, 0] - r[0, 1]) / s;
        }
        else if (r[0, 0] > r[1, 1] && r[0, 0] > r[2, 2])
        {
            double s = Math.Sqrt(1.0 + r[0, 0] - r[1, 1] - r[2, 2]) * 2;
            w = (r[2, 1] - r[1, 2]) / s;
            x = 0.25 * s;
            y = (r[0, 1] + r[1, 0]) / s;
            z = (r[0, 2] + r[2, 0]) / s;
        }
        else if (r[1, 1] > r[2, 2])
        {
            double s = Math.Sqrt(1.0 + r[1, 1] - r[0, 0] - r[2, 2]) * 2;
            w = (r[0, 2] - r[2, 0]) / s;
            x = (r[0, 1] + r[1, 0]) / s;
            y = 0.25 * s;
            z = (r[1, 2] + r[2, 1]) / s;
        }
        else
        {
            double s = Math.Sqrt(1.0 + r[2, 2] - r[0, 0] - r[1, 1]) * 2;
            w = (r[1, 0] - r[0, 1]) / s;
            x = (r[0, 2] + r[2, 0]) / s;
            y = (r[1, 2] + r[2, 1]) / s;
            z = 0.25 * s;
        }

        double norm = Math.Sqrt((w * w) + (x * x) + (y * y) + (z * z));
        if (norm < 1e-12) return [1, 0, 0, 0];

        return [w / norm, x / norm, y / norm, z / norm];
    }

    /// <summary>单位四元数 (w, x, y, z) → 旋转矩阵。</summary>
    public static double[,] QuaternionToMatrix(double[] q)
    {
        ArgumentNullException.ThrowIfNull(q);
        if (q.Length != 4) throw new ArgumentException("四元数必须是 4 个分量。", nameof(q));

        double w = q[0], x = q[1], y = q[2], z = q[3];
        double norm = Math.Sqrt((w * w) + (x * x) + (y * y) + (z * z));

        if (norm < 1e-12) return new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

        w /= norm; x /= norm; y /= norm; z /= norm;

        return new double[3, 3]
        {
            { 1 - (2 * ((y * y) + (z * z))), 2 * ((x * y) - (z * w)), 2 * ((x * z) + (y * w)) },
            { 2 * ((x * y) + (z * w)), 1 - (2 * ((x * x) + (z * z))), 2 * ((y * z) - (x * w)) },
            { 2 * ((x * z) - (y * w)), 2 * ((y * z) + (x * w)), 1 - (2 * ((x * x) + (y * y))) }
        };
    }

    /// <summary>四元数左乘矩阵 L(q)：使 q ⊗ p = L(q)·p。</summary>
    public static double[,] LeftQuaternionMatrix(double[] q) => new double[4, 4]
    {
        { q[0], -q[1], -q[2], -q[3] },
        { q[1], q[0], -q[3], q[2] },
        { q[2], q[3], q[0], -q[1] },
        { q[3], -q[2], q[1], q[0] }
    };

    /// <summary>四元数右乘矩阵 R(q)：使 p ⊗ q = R(q)·p。</summary>
    public static double[,] RightQuaternionMatrix(double[] q) => new double[4, 4]
    {
        { q[0], -q[1], -q[2], -q[3] },
        { q[1], q[0], q[3], -q[2] },
        { q[2], -q[3], q[0], q[1] },
        { q[3], q[2], -q[1], q[0] }
    };

    // ── 矩阵工具 ────────────────────────────────────────────

    public static double[,] Multiply(double[,] a, double[,] b)
    {
        int rows = a.GetLength(0), cols = b.GetLength(1), inner = a.GetLength(1);
        var r = new double[rows, cols];

        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                double sum = 0;
                for (int k = 0; k < inner; k++) sum += a[i, k] * b[k, j];
                r[i, j] = sum;
            }
        }

        return r;
    }

    public static double[,] Transpose(double[,] m)
    {
        int rows = m.GetLength(0), cols = m.GetLength(1);
        var r = new double[cols, rows];

        for (int i = 0; i < rows; i++)
            for (int j = 0; j < cols; j++)
                r[j, i] = m[i, j];

        return r;
    }
}
