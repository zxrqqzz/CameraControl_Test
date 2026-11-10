using CameraControl.Domain.Ports;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Calibration;

namespace CameraControl.Application.Calibration;

/// <summary>标定会话状态（内存态）。</summary>
/// <param name="CameraId">相机。</param>
/// <param name="GridCols">棋盘内角点列数。</param>
/// <param name="GridRows">棋盘内角点行数。</param>
/// <param name="SquareMm">方格边长（mm）。</param>
/// <param name="ViewCount">已采集视角数。</param>
/// <param name="LastResult">最近一次求解结果（未求解为 null）。</param>
public sealed record CalibrationSessionState(
    string CameraId,
    int GridCols,
    int GridRows,
    double SquareMm,
    int ViewCount,
    IntrinsicsSolveResult? LastResult);

/// <summary>
/// 标定会话：累积视角 → 求解 → 落盘。
///
/// 为什么需要它：`calibration.solveIntrinsics` 是"一次性传全部视角"的批处理，
/// 而实际标定是**交互式**的（采一张、看残差、再采一张）。
/// 会话服务把中间状态放在内存里，避免每次调用都重复传图。
/// </summary>
public interface ICalibrationSession
{
    /// <summary>读取或创建某相机的会话。</summary>
    CalibrationSessionState GetState(string cameraId);

    /// <summary>配置棋盘规格（会清空已有视角）。</summary>
    CalibrationSessionState Configure(string cameraId, int cols, int rows, double squareMm);

    /// <summary>添加一个视角（图像点，按棋盘顺序）。</summary>
    CalibrationSessionState AddView(string cameraId, IReadOnlyList<Point2> imagePoints);

    /// <summary>移除指定视角。</summary>
    CalibrationSessionState RemoveView(string cameraId, int index);

    /// <summary>清空视角（保留棋盘规格）。</summary>
    CalibrationSessionState Reset(string cameraId);

    /// <summary>求解并记录结果。</summary>
    IntrinsicsSolveResult Solve(string cameraId, int imageWidth, int imageHeight);

    /// <summary>取走会话里的全部视角（用于批量求解）。</summary>
    IReadOnlyList<CalibrationView> GetViews(string cameraId);
}

/// <summary>标定会话实现。</summary>
public sealed class CalibrationSessionService : ICalibrationSession
{
    private sealed class Session
    {
        public int Cols = 12;
        public int Rows = 13;
        public double SquareMm = 30;
        public readonly List<CalibrationView> Views = [];
        public IntrinsicsSolveResult? LastResult;
    }

    private readonly ICameraCalibrator _calibrator;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public CalibrationSessionService(ICameraCalibrator calibrator)
    {
        ArgumentNullException.ThrowIfNull(calibrator);
        _calibrator = calibrator;
    }

    public CalibrationSessionState GetState(string cameraId)
    {
        lock (_sync) return Snapshot(cameraId, GetOrCreate(cameraId));
    }

    public CalibrationSessionState Configure(string cameraId, int cols, int rows, double squareMm)
    {
        if (cols < 3 || rows < 3)
            throw new CameraOperationException(
                CalibrationErrorCodes.ImplausibleResult,
                "棋盘内角点至少要 3×3。");

        if (squareMm <= 0)
            throw new CameraOperationException(
                CalibrationErrorCodes.ImplausibleResult,
                "方格边长必须大于 0。");

        lock (_sync)
        {
            Session session = GetOrCreate(cameraId);

            session.Cols = cols;
            session.Rows = rows;
            session.SquareMm = squareMm;
            session.Views.Clear();
            session.LastResult = null;

            return Snapshot(cameraId, session);
        }
    }

    public CalibrationSessionState AddView(string cameraId, IReadOnlyList<Point2> imagePoints)
    {
        ArgumentNullException.ThrowIfNull(imagePoints);

        lock (_sync)
        {
            Session session = GetOrCreate(cameraId);

            var spec = new ChessboardSpec(session.Cols, session.Rows, session.SquareMm);
            IReadOnlyList<Point3> objectPoints = spec.ObjectPoints();

            if (imagePoints.Count != objectPoints.Count)
            {
                throw new CameraOperationException(
                    CalibrationErrorCodes.ImplausibleResult,
                    $"图像点数 {imagePoints.Count} 与棋盘点数 {objectPoints.Count} 不一致" +
                    $"（{session.Cols}×{session.Rows}）。");
            }

            session.Views.Add(new CalibrationView(objectPoints, imagePoints));

            return Snapshot(cameraId, session);
        }
    }

    public CalibrationSessionState RemoveView(string cameraId, int index)
    {
        lock (_sync)
        {
            Session session = GetOrCreate(cameraId);

            if (index < 0 || index >= session.Views.Count)
            {
                throw new CameraOperationException(
                    CalibrationErrorCodes.ImplausibleResult,
                    $"视角索引 {index} 超出范围（共 {session.Views.Count} 个）。");
            }

            session.Views.RemoveAt(index);

            return Snapshot(cameraId, session);
        }
    }

    public CalibrationSessionState Reset(string cameraId)
    {
        lock (_sync)
        {
            Session session = GetOrCreate(cameraId);

            session.Views.Clear();
            session.LastResult = null;

            return Snapshot(cameraId, session);
        }
    }

    public IntrinsicsSolveResult Solve(string cameraId, int imageWidth, int imageHeight)
    {
        IReadOnlyList<CalibrationView> views;
        ChessboardSpec spec;

        lock (_sync)
        {
            Session session = GetOrCreate(cameraId);

            if (session.Views.Count < 3)
            {
                return IntrinsicsSolveResult.Failure(
                    $"视角不足（{session.Views.Count} 个），至少需要 3 个不同姿态。",
                    [CalibrationErrorCodes.TooFewViews]);
            }

            views = [.. session.Views];
            spec = new ChessboardSpec(session.Cols, session.Rows, session.SquareMm);
        }

        IntrinsicsSolveResult result = _calibrator.Solve(views, spec, imageWidth, imageHeight);

        lock (_sync)
        {
            Session session = GetOrCreate(cameraId);
            session.LastResult = result;
        }

        return result;
    }

    public IReadOnlyList<CalibrationView> GetViews(string cameraId)
    {
        lock (_sync) return [.. GetOrCreate(cameraId).Views];
    }

    private Session GetOrCreate(string cameraId)
    {
        string key = cameraId ?? string.Empty;

        if (!_sessions.TryGetValue(key, out Session? session))
        {
            session = new Session();
            _sessions[key] = session;
        }

        return session;
    }

    private static CalibrationSessionState Snapshot(string cameraId, Session session) =>
        new(cameraId, session.Cols, session.Rows, session.SquareMm, session.Views.Count, session.LastResult);
}
