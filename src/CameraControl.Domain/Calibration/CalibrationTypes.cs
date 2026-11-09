namespace CameraControl.Domain.Calibration;

/// <summary>二维点（像素坐标）。</summary>
public readonly record struct Point2(double X, double Y)
{
    public double DistanceTo(Point2 other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}

/// <summary>三维点（毫米）。</summary>
public readonly record struct Point3(double X, double Y, double Z);

/// <summary>
/// 棋盘格规格。
/// </summary>
/// <param name="Columns">内角点列数（不含边框）。</param>
/// <param name="Rows">内角点行数（不含边框）。</param>
/// <param name="SquareSizeMm">方格边长（毫米）。</param>
public sealed record ChessboardSpec(int Columns, int Rows, double SquareSizeMm)
{
    /// <summary>内角点总数。</summary>
    public int Count => Columns * Rows;

    /// <summary>
    /// 生成棋盘格物点（Z=0 平面，单位毫米）。
    /// 约定：X 沿列方向，Y 沿行方向，原点在第一个内角点。
    /// </summary>
    public IReadOnlyList<Point3> ObjectPoints()
    {
        var points = new List<Point3>(Count);

        for (int row = 0; row < Rows; row++)
        {
            for (int col = 0; col < Columns; col++)
                points.Add(new Point3(col * SquareSizeMm, row * SquareSizeMm, 0));
        }

        return points;
    }
}

/// <summary>一个标定视角：物点与对应的图像点（顺序必须一一对应）。</summary>
public sealed record CalibrationView(
    IReadOnlyList<Point3> ObjectPoints,
    IReadOnlyList<Point2> ImagePoints)
{
    public int Count => Math.Min(ObjectPoints.Count, ImagePoints.Count);
}

/// <summary>
/// 相机内参。
/// </summary>
/// <param name="Fx">X 方向焦距（像素）。</param>
/// <param name="Fy">Y 方向焦距（像素）。</param>
/// <param name="Cx">主点 X（像素）。</param>
/// <param name="Cy">主点 Y（像素）。</param>
/// <param name="Width">标定时使用的图像宽度。</param>
/// <param name="Height">标定时使用的图像高度。</param>
public sealed record CameraIntrinsics(double Fx, double Fy, double Cx, double Cy, int Width, int Height)
{
    /// <summary>平均焦距。</summary>
    public double FocalMean => (Fx + Fy) / 2;

    /// <summary>是否需要转置（纵横比异常通常意味着行列约定弄反了）。</summary>
    public bool LooksTransposed => Fx > Fy * 3 || Fy > Fx * 3;

    public override string ToString() =>
        $"fx={Fx:F3} fy={Fy:F3} cx={Cx:F3} cy={Cy:F3} ({Width}×{Height})";
}

/// <summary>标定求解结果。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Intrinsics">内参（成功时非空）。</param>
/// <param name="HomographyRmsePx">各视角单应对齐的均方根误差（像素），反映角点质量。</param>
/// <param name="Error">失败原因。</param>
/// <param name="Diagnostics">诊断信息（如视角过少、退化配置）。</param>
public sealed record IntrinsicsSolveResult(
    bool Ok,
    CameraIntrinsics? Intrinsics,
    double HomographyRmsePx,
    string? Error = null,
    IReadOnlyList<string>? Diagnostics = null)
{
    public static IntrinsicsSolveResult Failure(string error, IReadOnlyList<string>? diagnostics = null) =>
        new(false, null, double.NaN, error, diagnostics);
}

/// <summary>
/// 相机内参标定器端口。
/// </summary>
public interface ICameraCalibrator
{
    IntrinsicsSolveResult Solve(
        IReadOnlyList<CalibrationView> views,
        ChessboardSpec spec,
        int imageWidth,
        int imageHeight);
}

/// <summary>标定错误码。</summary>
public static class CalibrationErrorCodes
{
    public const string TooFewViews = "CALIB_TOO_FEW_VIEWS";
    public const string PointCountMismatch = "CALIB_POINT_COUNT_MISMATCH";
    public const string Degenerate = "CALIB_DEGENERATE_CONFIGURATION";
    public const string NumericFailure = "CALIB_NUMERIC_FAILURE";
    public const string ImplausibleResult = "CALIB_IMPLAUSIBLE_RESULT";
}
