using CameraControl.Application.Commands;
using CameraControl.Domain.Calibration;

namespace CameraControl.Application.Capabilities;

/// <summary>读取某台相机的标定结果。</summary>
public sealed record CalibrationGetCommand(string CameraId) : ICommand
{
    public const string Id = "calibration.getIntrinsics";

    public string CapabilityId => Id;
}

/// <summary>列出全部标定结果。</summary>
public sealed record CalibrationListCommand : ICommand
{
    public const string Id = "calibration.list";

    public string CapabilityId => Id;
}

/// <summary>删除某台相机的标定结果。</summary>
public sealed record CalibrationRemoveCommand(string CameraId) : ICommand
{
    public const string Id = "calibration.remove";

    public string CapabilityId => Id;
}

/// <summary>
/// 由已检测的角点求解内参并保存。
///
/// **注意分工**：角点检测（图像 → 点）不在本能力里，
/// 由界面或外部工具提供。这样求解内核不依赖任何图像库。
/// </summary>
/// <param name="CameraId">相机 id。</param>
/// <param name="GridCols">棋盘内角点列数。</param>
/// <param name="GridRows">棋盘内角点行数。</param>
/// <param name="SquareMm">方格边长（毫米）。</param>
/// <param name="ImageWidth">标定图像宽度。</param>
/// <param name="ImageHeight">标定图像高度。</param>
/// <param name="Views">每个视角的角点（行优先，数量必须等于 cols×rows）。</param>
/// <param name="Note">备注（标定人/环境）。</param>
public sealed record CalibrationSolveCommand(
    string CameraId,
    int GridCols,
    int GridRows,
    double SquareMm,
    int ImageWidth,
    int ImageHeight,
    IReadOnlyList<IReadOnlyList<Point2>> Views,
    string? Note = null) : ICommand
{
    public const string Id = "calibration.solveIntrinsics";

    public string CapabilityId => Id;
}

/// <summary>标定结果投影。</summary>
public sealed record CalibrationDto(
    string CameraId,
    double Fx,
    double Fy,
    double Cx,
    double Cy,
    int ImageWidth,
    int ImageHeight,
    int ViewCount,
    double HomographyRmsePx,
    DateTimeOffset SolvedAtUtc,
    string GridDescription,
    string? Note,
    string DistortionNote,
    double K1,
    double K2,
    double P1,
    double P2,
    double K3,
    double? ReprojectionRmsePx);

/// <summary>标定结果读取实现。</summary>
public sealed class CalibrationGetCapability : ICapabilityHandler<CalibrationGetCommand>
{
    private readonly ICalibrationStore _store;

    public CalibrationGetCapability(ICalibrationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CalibrationGetCommand.Id,
        Title: "读取相机标定结果",
        Description: "返回指定相机的内参（fx/fy/cx/cy）与元数据。未标定时返回 null 并说明如何标定。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CalibrationGetCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": { "cameraId": { "type": "string", "description": "相机 id，可用 device.list 查询" } },
          "required": ["cameraId"]
        }
        """);

    public async Task<object?> HandleAsync(
        CalibrationGetCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        CalibrationRecord? record = await _store
            .GetAsync(command.CameraId, cancellationToken)
            .ConfigureAwait(false);

        return record is null ? null : ToDto(record);
    }

    internal static CalibrationDto ToDto(CalibrationRecord record) => new(
        record.CameraId,
        record.Intrinsics.Fx,
        record.Intrinsics.Fy,
        record.Intrinsics.Cx,
        record.Intrinsics.Cy,
        record.Intrinsics.Width,
        record.Intrinsics.Height,
        record.ViewCount,
        Math.Round(record.HomographyRmsePx, 4),
        record.SolvedAtUtc,
        record.GridDescription,
        record.Note,
        record.DistortionNote,
        record.Distortion?.K1 ?? 0,
        record.Distortion?.K2 ?? 0,
        record.Distortion?.P1 ?? 0,
        record.Distortion?.P2 ?? 0,
        record.Distortion?.K3 ?? 0,
        record.ReprojectionRmsePx);
}

/// <summary>标定结果列表实现。</summary>
public sealed class CalibrationListCapability : ICapabilityHandler<CalibrationListCommand>
{
    private readonly ICalibrationStore _store;

    public CalibrationListCapability(ICalibrationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CalibrationListCommand.Id,
        Title: "列出全部标定结果",
        Description: "返回所有已标定相机及其内参摘要。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CalibrationListCommand),
        ParametersJsonSchema: """{ "type": "object", "properties": {} }""");

    public async Task<object?> HandleAsync(
        CalibrationListCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CalibrationRecord> records = await _store
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new
        {
            count = records.Count,
            items = records.Select(CalibrationGetCapability.ToDto).ToArray()
        };
    }
}

/// <summary>标定结果删除实现（可逆：重新标定即可恢复）。</summary>
public sealed class CalibrationRemoveCapability : ICapabilityHandler<CalibrationRemoveCommand>
{
    private readonly ICalibrationStore _store;

    public CalibrationRemoveCapability(ICalibrationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CalibrationRemoveCommand.Id,
        Title: "删除相机标定结果",
        Description: "删除指定相机的内参记录。可逆操作：重新标定即可恢复。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CalibrationRemoveCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": { "cameraId": { "type": "string" } },
          "required": ["cameraId"]
        }
        """);

    public async Task<object?> HandleAsync(
        CalibrationRemoveCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        bool removed = await _store
            .RemoveAsync(command.CameraId, cancellationToken)
            .ConfigureAwait(false);

        return new { removed, cameraId = command.CameraId };
    }
}

/// <summary>
/// 内参求解实现。
///
/// 求解本身是纯计算（<see cref="ZhangCalibrator"/>），风险归为 Reversible：
/// 它只写标定记录文件，不动物理设备。
/// </summary>
public sealed class CalibrationSolveCapability : ICapabilityHandler<CalibrationSolveCommand>
{
    private readonly ICameraCalibrator _calibrator;
    private readonly ICalibrationStore _store;
    private readonly Domain.Ports.IClock _clock;

    public CalibrationSolveCapability(
        ICameraCalibrator calibrator,
        ICalibrationStore store,
        Domain.Ports.IClock clock)
    {
        ArgumentNullException.ThrowIfNull(calibrator);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);

        _calibrator = calibrator;
        _store = store;
        _clock = clock;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CalibrationSolveCommand.Id,
        Title: "求解相机内参并保存",
        Description: "输入各视角已检测好的棋盘角点（行优先，每视角 cols×rows 个点），" +
                     "用张氏标定闭合解求 fx/fy/cx/cy 并保存。不含畸变估计。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CalibrationSolveCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "cameraId": { "type": "string" },
            "gridCols": { "type": "integer", "minimum": 4, "maximum": 30 },
            "gridRows": { "type": "integer", "minimum": 4, "maximum": 30 },
            "squareMm": { "type": "number", "exclusiveMinimum": 0 },
            "imageWidth": { "type": "integer", "minimum": 1 },
            "imageHeight": { "type": "integer", "minimum": 1 },
            "views": {
              "type": "array",
              "minItems": 3,
              "description": "每个视角一个点数组，长度必须等于 gridCols×gridRows",
              "items": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": { "x": { "type": "number" }, "y": { "type": "number" } },
                  "required": ["x", "y"]
                }
              }
            },
            "note": { "type": "string" }
          },
          "required": ["cameraId", "gridCols", "gridRows", "squareMm", "imageWidth", "imageHeight", "views"]
        }
        """);

    public async Task<object?> HandleAsync(
        CalibrationSolveCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.CameraId))
            throw new Domain.Ports.CameraOperationException(
                CalibrationStoreErrorCodes.InvalidArguments, "cameraId 不能为空。");

        if (command.GridCols < 4 || command.GridRows < 4)
            throw new Domain.Ports.CameraOperationException(
                CalibrationStoreErrorCodes.InvalidArguments, "棋盘内角点行列数至少为 4。");

        if (command.SquareMm <= 0)
            throw new Domain.Ports.CameraOperationException(
                CalibrationStoreErrorCodes.InvalidArguments, "方格边长必须为正数。");

        int expected = command.GridCols * command.GridRows;

        var views = new List<CalibrationView>(command.Views.Count);

        for (int i = 0; i < command.Views.Count; i++)
        {
            IReadOnlyList<Point2> points = command.Views[i];

            if (points.Count != expected)
            {
                throw new Domain.Ports.CameraOperationException(
                    CalibrationStoreErrorCodes.InvalidArguments,
                    $"第 {i + 1} 个视角有 {points.Count} 个点，应为 {expected} 个（{command.GridRows}×{command.GridCols}）。");
            }

            var objectPoints = new List<Point3>(expected);

            for (int row = 0; row < command.GridRows; row++)
            {
                for (int col = 0; col < command.GridCols; col++)
                    objectPoints.Add(new Point3(col * command.SquareMm, row * command.SquareMm, 0));
            }

            views.Add(new CalibrationView(objectPoints, points));
        }

        var spec = new ChessboardSpec(command.GridCols, command.GridRows, command.SquareMm);

        IntrinsicsSolveResult result = _calibrator.Solve(
            views, spec, command.ImageWidth, command.ImageHeight);

        if (!result.Ok || result.Intrinsics is null)
        {
            throw new Domain.Ports.CameraOperationException(
                CalibrationStoreErrorCodes.SolveFailed,
                $"标定求解失败：{result.Error}");
        }

        var record = new CalibrationRecord(
            CameraId: command.CameraId,
            Intrinsics: result.Intrinsics,
            ViewCount: views.Count,
            HomographyRmsePx: result.HomographyRmsePx,
            SolvedAtUtc: _clock.UtcNow,
            GridDescription: $"{command.GridCols}x{command.GridRows}@{command.SquareMm}mm",
            Note: command.Note,
            Distortion: result.Distortion,
            ReprojectionRmsePx: result.ReprojectionRmsePx,
            DistortionNote: result.Distortion is null
                ? "未估计畸变（闭合解）。"
                : $"已估计畸变 k1={result.Distortion.K1:F4} k2={result.Distortion.K2:F4} " +
                  $"k3={result.Distortion.K3:F4}；重投影 RMSE {result.ReprojectionRmsePx:F3}px。");

        await _store.SaveAsync(record, cancellationToken).ConfigureAwait(false);

        return new
        {
            saved = true,
            calibration = CalibrationGetCapability.ToDto(record),
            diagnostics = result.Diagnostics ?? []
        };
    }
}
