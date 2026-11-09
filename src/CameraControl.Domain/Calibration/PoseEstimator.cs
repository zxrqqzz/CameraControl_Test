namespace CameraControl.Domain.Calibration;

/// <summary>
/// 位姿初值估计：由单应矩阵分解出 (R, t)。
///
/// 数学依据：平面目标（Z=0）时 H = s·K[r1 r2 t]，
/// 因此把观测点去畸变并归一化后拟合单应，可直接拆出旋转的前两列，
/// 第三列由叉积补齐，平移由第三列得到。
/// </summary>
internal static class PoseEstimator
{
    /// <summary>
    /// 估计一个视角的位姿。返回的 R 为行优先 3×3，T 为 3 维平移（单位与物点一致）。
    /// 失败返回 null（点共线、深度为负、数值退化等）。
    /// </summary>
    public static (double[,] R, double[] T)? Estimate(
        CalibrationView view,
        CameraIntrinsics intrinsics,
        DistortionModel distortion)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(intrinsics);
        ArgumentNullException.ThrowIfNull(distortion);

        int n = view.Count;
        if (n < 4) return null;

        var objectPlane = new List<Point2>(n);
        var normalized = new List<Point2>(n);

        for (int i = 0; i < n; i++)
        {
            Point3 p = view.ObjectPoints[i];
            objectPlane.Add(new Point2(p.X, p.Y));

            Point2 uv = view.ImagePoints[i];
            double xn = (uv.X - intrinsics.Cx) / intrinsics.Fx;
            double yn = (uv.Y - intrinsics.Cy) / intrinsics.Fy;

            (double ux, double uy) = distortion.Undistort(xn, yn);
            normalized.Add(new Point2(ux, uy));
        }

        Homography? h = Homography.Compute(objectPlane, normalized);
        if (h is null) return null;

        double[,] m = h.Matrix;

        double[] h1 = [m[0, 0], m[1, 0], m[2, 0]];
        double[] h2 = [m[0, 1], m[1, 1], m[2, 1]];
        double[] h3 = [m[0, 2], m[1, 2], m[2, 2]];

        double norm1 = Length(h1);
        double norm2 = Length(h2);

        if (norm1 < 1e-12 || norm2 < 1e-12) return null;

        double lambda = 2.0 / (norm1 + norm2);

        double[] r1 = Normalize([h1[0] * lambda, h1[1] * lambda, h1[2] * lambda]);
        double[] r2Raw = [h2[0] * lambda, h2[1] * lambda, h2[2] * lambda];

        double dot = Dot(r1, r2Raw);
        double[] r2 = Normalize([
            r2Raw[0] - (dot * r1[0]),
            r2Raw[1] - (dot * r1[1]),
            r2Raw[2] - (dot * r1[2])
        ]);

        double[] r3 = Cross(r1, r2);
        double[] t = [h3[0] * lambda, h3[1] * lambda, h3[2] * lambda];

        // 保证右手系（det = +1）且棋盘在相机前方
        double det = Dot(r1, Cross(r2, r3));

        if (det < 0)
        {
            r2 = [-r2[0], -r2[1], -r2[2]];
            r3 = Cross(r1, r2);
            t = [-t[0], -t[1], -t[2]];
        }

        if (t[2] <= 0) return null;

        var rotation = new double[3, 3];
        rotation[0, 0] = r1[0]; rotation[0, 1] = r2[0]; rotation[0, 2] = r3[0];
        rotation[1, 0] = r1[1]; rotation[1, 1] = r2[1]; rotation[1, 2] = r3[1];
        rotation[2, 0] = r1[2]; rotation[2, 1] = r2[2]; rotation[2, 2] = r3[2];

        return (rotation, t);
    }

    /// <summary>罗德里格斯公式：轴角 → 旋转矩阵。</summary>
    public static double[,] Rodrigues(double[] omega)
    {
        ArgumentNullException.ThrowIfNull(omega);

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

    /// <summary>把一个物点用给定模型投到像素坐标。</summary>
    public static (double U, double V) Project(
        Point3 point,
        double[,] rotation,
        double[] translation,
        CameraIntrinsics intrinsics,
        DistortionModel distortion)
    {
        double xc = (rotation[0, 0] * point.X) + (rotation[0, 1] * point.Y) + (rotation[0, 2] * point.Z) + translation[0];
        double yc = (rotation[1, 0] * point.X) + (rotation[1, 1] * point.Y) + (rotation[1, 2] * point.Z) + translation[1];
        double zc = (rotation[2, 0] * point.X) + (rotation[2, 1] * point.Y) + (rotation[2, 2] * point.Z) + translation[2];

        if (Math.Abs(zc) < 1e-9) return (double.NaN, double.NaN);

        (double xd, double yd) = distortion.Distort(xc / zc, yc / zc);

        return ((intrinsics.Fx * xd) + intrinsics.Cx, (intrinsics.Fy * yd) + intrinsics.Cy);
    }

    private static double Length(double[] v) => Math.Sqrt((v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]));

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Cross(double[] a, double[] b) =>
    [
        (a[1] * b[2]) - (a[2] * b[1]),
        (a[2] * b[0]) - (a[0] * b[2]),
        (a[0] * b[1]) - (a[1] * b[0])
    ];

    private static double[] Normalize(double[] v)
    {
        double length = Length(v);
        return length < 1e-12 ? v : [v[0] / length, v[1] / length, v[2] / length];
    }
}
