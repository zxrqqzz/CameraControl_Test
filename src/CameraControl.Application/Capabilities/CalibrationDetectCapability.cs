using CameraControl.Application.Calibration;
using CameraControl.Application.Capture;
using CameraControl.Application.Commands;
using CameraControl.Domain.Analysis;
using CameraControl.Domain.Calibration;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Capabilities;

/// <summary>calibration.detectCorners：从相机取一帧并检测棋盘角点。</summary>
public sealed record CalibrationDetectCornersCommand(
    string CameraId,
    bool AddToSession = true) : ICommand
{
    public const string Id = "calibration.detectCorners";

    public string CapabilityId => Id;
}

/// <summary>calibration.detectCorners 实现。</summary>
public sealed class CalibrationDetectCornersCapability
    : ICapabilityHandler<CalibrationDetectCornersCommand>
{
    private readonly ICameraSessionRegistry _registry;
    private readonly ICalibrationSession _session;
    private readonly ICalibrationStatistics _statistics;

    public CalibrationDetectCornersCapability(
        ICameraSessionRegistry registry,
        ICalibrationSession session,
        ICalibrationStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(statistics);

        _registry = registry;
        _session = session;
        _statistics = statistics;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CalibrationDetectCornersCommand.Id,
        Title: "检测棋盘角点",
        Description:
            "取一帧并检测棋盘内角点，成功则追加到标定会话。" +
            "检测基于内置 Harris + 点阵重建，对光照和拍摄质量敏感；" +
            "coverage 偏低时不要直接采用。也可用 calibration.addPoint 手工提供角点。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CalibrationDetectCornersCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "cameraId": { "type": "string" },
            "addToSession": { "type": "boolean", "default": true }
          },
          "required": ["cameraId"]
        }
        """);

    public async Task<object?> HandleAsync(
        CalibrationDetectCornersCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        var id = new CameraId(command.CameraId);

        ICameraSession camera = _registry.Find(id)
            ?? throw new CameraOperationException(
                CaptureErrorCodes.CameraNotOpen,
                $"相机 {command.CameraId} 未打开，请先调用 camera.open。");

        Domain.Capture.Frame frame = await camera
            .CaptureSingleAsync(options: null, cancellationToken)
            .ConfigureAwait(false);

        GrayImage gray = GrayImage.FromFrame(frame.Image.Width, frame.Image.Height, frame.Image.Stride, frame.Image.PixelFormat, frame.Image.Pixels.Span);

        IReadOnlyList<CornerCandidate> candidates = CornerDetector.Detect(gray);

        LatticeResult lattice = LatticeGridBuilder.Build(candidates);

        CalibrationSessionState state = _session.GetState(command.CameraId);

        if (!lattice.Ok || lattice.Grid is null)
        {
            _statistics.RecordDetection(command.CameraId, false, 0, lattice.Coverage);

            return new
            {
                ok = false,
                candidates = candidates.Count,
                coverage = lattice.Coverage,
                error = lattice.Error,
                notes = lattice.Notes,
                hint = "换更好的光照/更清晰的棋盘，或用 calibration.addPoint 手工提供角点。"
            };
        }

        IReadOnlyList<Point2> points = lattice.Grid.Points;

        bool matchesSpec = points.Count == state.GridCols * state.GridRows;

        if (!matchesSpec)
        {
            _statistics.RecordDetection(command.CameraId, false, points.Count, lattice.Coverage);

            return new
            {
                ok = false,
                candidates = candidates.Count,
                detected = points.Count,
                expected = state.GridCols * state.GridRows,
                coverage = lattice.Coverage,
                error = $"检测到 {points.Count} 点，但棋盘规格要求 {state.GridCols}×{state.GridRows}。" +
                        "请确认格子数，或先 calibration.configure。"
            };
        }

        CalibrationSessionState updated = command.AddToSession
            ? _session.AddView(command.CameraId, points)
            : state;

        _statistics.RecordDetection(command.CameraId, true, points.Count, lattice.Coverage);

        return new
        {
            ok = true,
            candidates = candidates.Count,
            detected = points.Count,
            coverage = lattice.Coverage,
            viewCount = updated.ViewCount,
            added = command.AddToSession,
            points
        };
    }
}

/// <summary>标定检测统计（内存态）。</summary>
public sealed record CalibrationDetectionStats(
    long Attempts,
    long Successes,
    double LastCoverage,
    DateTimeOffset? LastAt);

/// <summary>标定检测统计接口。</summary>
public interface ICalibrationStatistics
{
    void RecordDetection(string cameraId, bool ok, int pointCount, double coverage);

    CalibrationDetectionStats Get(string cameraId);
}

/// <summary>内存态实现。</summary>
public sealed class CalibrationStatisticsService : ICalibrationStatistics
{
    private sealed class Counter
    {
        public long Attempts;
        public long Successes;
        public double LastCoverage;
        public DateTimeOffset? LastAt;
    }

    private readonly Dictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public void RecordDetection(string cameraId, bool ok, int pointCount, double coverage)
    {
        lock (_sync)
        {
            string key = cameraId ?? string.Empty;

            if (!_counters.TryGetValue(key, out Counter? counter))
            {
                counter = new Counter();
                _counters[key] = counter;
            }

            counter.Attempts++;
            if (ok) counter.Successes++;
            counter.LastCoverage = coverage;
            counter.LastAt = DateTimeOffset.UtcNow;
        }
    }

    public CalibrationDetectionStats Get(string cameraId)
    {
        lock (_sync)
        {
            string key = cameraId ?? string.Empty;

            if (!_counters.TryGetValue(key, out Counter? counter))
                return new CalibrationDetectionStats(0, 0, 0, null);

            return new CalibrationDetectionStats(
                counter.Attempts, counter.Successes, counter.LastCoverage, counter.LastAt);
        }
    }
}
