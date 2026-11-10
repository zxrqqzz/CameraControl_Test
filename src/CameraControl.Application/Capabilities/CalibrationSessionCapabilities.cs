using CameraControl.Application.Calibration;
using CameraControl.Application.Commands;
using CameraControl.Domain.Calibration;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Capabilities;

// ── 命令 ────────────────────────────────────────────────

/// <summary>calibration.getState：读取标定会话状态。</summary>
public sealed record CalibrationGetStateCommand(string CameraId) : ICommand
{
    public const string Id = "calibration.getState";

    public string CapabilityId => Id;
}

/// <summary>calibration.configure：设置棋盘规格。</summary>
public sealed record CalibrationConfigureCommand(
    string CameraId,
    int Cols = 12,
    int Rows = 13,
    double SquareMm = 30) : ICommand
{
    public const string Id = "calibration.configure";

    public string CapabilityId => Id;
}

/// <summary>calibration.addPoint：追加一个视角的角点。</summary>
public sealed record CalibrationAddPointCommand(
    string CameraId,
    IReadOnlyList<Point2> ImagePoints) : ICommand
{
    public const string Id = "calibration.addPoint";

    public string CapabilityId => Id;
}

/// <summary>calibration.removePoint：移除一个视角。</summary>
public sealed record CalibrationRemovePointCommand(string CameraId, int Index) : ICommand
{
    public const string Id = "calibration.removePoint";

    public string CapabilityId => Id;
}

/// <summary>calibration.reset：清空视角。</summary>
public sealed record CalibrationResetCommand(string CameraId) : ICommand
{
    public const string Id = "calibration.reset";

    public string CapabilityId => Id;
}

/// <summary>calibration.apply：求解并落盘。</summary>
public sealed record CalibrationApplyCommand(
    string CameraId,
    int ImageWidth,
    int ImageHeight,
    string? Note = null) : ICommand
{
    public const string Id = "calibration.apply";

    public string CapabilityId => Id;
}

/// <summary>calibration.export：导出 JSON。</summary>
public sealed record CalibrationExportCommand(string CameraId) : ICommand
{
    public const string Id = "calibration.export";

    public string CapabilityId => Id;
}

/// <summary>一个手眼采样（机器人位姿 + 相机位姿）。</summary>
public sealed record HandEyeSampleDto(Transform3D RobotPose, Transform3D CameraPose);

/// <summary>calibration.solveHandEye：手眼标定 AX=XB。</summary>
public sealed record CalibrationSolveHandEyeCommand(
    IReadOnlyList<HandEyeSampleDto> Samples) : ICommand
{
    public const string Id = "calibration.solveHandEye";

    public string CapabilityId => Id;
}

// ── 实现 ────────────────────────────────────────────────

/// <summary>标定求解结果摘要（给界面/AI 显示）。</summary>
public sealed record CalibrationSolveSummary(
    bool Ok,
    string? Error,
    double? Fx,
    double? Fy,
    double? Cx,
    double? Cy,
    double? K1,
    double? K2,
    double? K3,
    double? ReprojectionRmsePx,
    double? HomographyRmsePx,
    int ViewCount,
    bool Saved)
{
    /// <summary>单行摘要，便于直接显示。</summary>
    public string ToSummary() => Ok
        ? $"fx={Fx:F2} fy={Fy:F2} cx={Cx:F2} cy={Cy:F2}；" +
          $"k1={K1:F4} k2={K2:F4} k3={K3:F4}；" +
          $"重投影 RMSE={ReprojectionRmsePx:F3}px（{ViewCount} 视角）"
        : $"失败：{Error}";
}

/// <summary>calibration.getState 实现。</summary>
public sealed class CalibrationGetStateCapability : ICapabilityHandler<CalibrationGetStateCommand>
{
    private readonly ICalibrationSession _session;

    public CalibrationGetStateCapability(ICalibrationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CalibrationGetStateCommand.Id,
        Title: "标定会话状态",
        Description: "返回棋盘规格、已采集视角数与最近一次求解结果。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CalibrationGetStateCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "cameraId": { "type": "string" } }, "required": ["cameraId"] }
        """);

    public Task<object?> HandleAsync(
        CalibrationGetStateCommand command,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>(_session.GetState(command.CameraId));
}

/// <summary>calibration.configure 实现。</summary>
public sealed class CalibrationConfigureCapability : ICapabilityHandler<CalibrationConfigureCommand>
{
    private readonly ICalibrationSession _session;

    public CalibrationConfigureCapability(ICalibrationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CalibrationConfigureCommand.Id,
        Title: "配置棋盘",
        Description: "设置棋盘内角点行列数与方格边长。**会清空已采集视角**。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CalibrationConfigureCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "cameraId": { "type": "string" },
            "cols": { "type": "integer", "default": 12, "minimum": 3 },
            "rows": { "type": "integer", "default": 13, "minimum": 3 },
            "squareMm": { "type": "number", "default": 30, "exclusiveMinimum": 0 }
          },
          "required": ["cameraId"]
        }
        """);

    public Task<object?> HandleAsync(
        CalibrationConfigureCommand command,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>(_session.Configure(
            command.CameraId, command.Cols, command.Rows, command.SquareMm));
}

/// <summary>calibration.addPoint 实现。</summary>
public sealed class CalibrationAddPointCapability : ICapabilityHandler<CalibrationAddPointCommand>
{
    private readonly ICalibrationSession _session;

    public CalibrationAddPointCapability(ICalibrationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CalibrationAddPointCommand.Id,
        Title: "追加标定视角",
        Description:
            "追加一个视角的角点（按棋盘行优先顺序，像素坐标）。点数必须等于 cols×rows。" +
            "建议 10 个以上不同姿态，且棋盘深度要拉开。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CalibrationAddPointCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "cameraId": { "type": "string" },
            "imagePoints": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": { "x": { "type": "number" }, "y": { "type": "number" } },
                "required": ["x", "y"]
              }
            }
          },
          "required": ["cameraId", "imagePoints"]
        }
        """);

    public Task<object?> HandleAsync(
        CalibrationAddPointCommand command,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>(_session.AddView(command.CameraId, command.ImagePoints));
}

/// <summary>calibration.removePoint 实现。</summary>
public sealed class CalibrationRemovePointCapability : ICapabilityHandler<CalibrationRemovePointCommand>
{
    private readonly ICalibrationSession _session;

    public CalibrationRemovePointCapability(ICalibrationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CalibrationRemovePointCommand.Id,
        Title: "移除标定视角",
        Description: "按索引移除一个已采集视角（用于剔除残差过大的图）。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CalibrationRemovePointCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": { "cameraId": { "type": "string" }, "index": { "type": "integer", "minimum": 0 } },
          "required": ["cameraId", "index"]
        }
        """);

    public Task<object?> HandleAsync(
        CalibrationRemovePointCommand command,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>(_session.RemoveView(command.CameraId, command.Index));
}

/// <summary>calibration.reset 实现。</summary>
public sealed class CalibrationResetCapability : ICapabilityHandler<CalibrationResetCommand>
{
    private readonly ICalibrationSession _session;

    public CalibrationResetCapability(ICalibrationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CalibrationResetCommand.Id,
        Title: "清空标定视角",
        Description: "清空会话内的全部视角与求解结果（保留棋盘规格）。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CalibrationResetCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "cameraId": { "type": "string" } }, "required": ["cameraId"] }
        """);

    public Task<object?> HandleAsync(
        CalibrationResetCommand command,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>(_session.Reset(command.CameraId));
}

/// <summary>calibration.apply 实现。</summary>
public sealed class CalibrationApplyCapability : ICapabilityHandler<CalibrationApplyCommand>
{
    private readonly ICalibrationSession _session;
    private readonly ICalibrationStore _store;

    public CalibrationApplyCapability(ICalibrationSession session, ICalibrationStore store)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);

        _session = session;
        _store = store;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CalibrationApplyCommand.Id,
        Title: "求解并保存标定",
        Description:
            "用会话内视角求解内参+畸变并落盘。需至少 3 个视角（建议 10+，且深度要拉开）。" +
            "返回重投影 RMSE 与畸变系数；RMSE 偏大不要直接使用。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(CalibrationApplyCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "cameraId": { "type": "string" },
            "imageWidth": { "type": "integer", "minimum": 1 },
            "imageHeight": { "type": "integer", "minimum": 1 },
            "note": { "type": "string" }
          },
          "required": ["cameraId", "imageWidth", "imageHeight"]
        }
        """);

    public async Task<object?> HandleAsync(
        CalibrationApplyCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        CalibrationSessionState before = _session.GetState(command.CameraId);

        IntrinsicsSolveResult result = _session.Solve(command.CameraId, command.ImageWidth, command.ImageHeight);

        if (!result.Ok || result.Intrinsics is null)
        {
            return new CalibrationSolveSummary(
                false, result.Error, null, null, null, null, null, null, null, null, null, before.ViewCount, false);
        }

        var record = new CalibrationRecord(
            command.CameraId,
            result.Intrinsics,
            before.ViewCount,
            result.HomographyRmsePx,
            DateTimeOffset.UtcNow,
            $"{before.GridCols}x{before.GridRows}@{before.SquareMm}mm",
            command.Note,
            DistortionNote: result.Distortion is null
                ? "未估计畸变（闭合解）。"
                : $"k1={result.Distortion.K1:F4} k2={result.Distortion.K2:F4} k3={result.Distortion.K3:F4}",
            Distortion: result.Distortion,
            ReprojectionRmsePx: result.ReprojectionRmsePx);

        await _store.SaveAsync(record, cancellationToken).ConfigureAwait(false);

        return new CalibrationSolveSummary(
            true,
            null,
            result.Intrinsics.Fx,
            result.Intrinsics.Fy,
            result.Intrinsics.Cx,
            result.Intrinsics.Cy,
            result.Distortion?.K1,
            result.Distortion?.K2,
            result.Distortion?.K3,
            result.ReprojectionRmsePx,
            result.HomographyRmsePx,
            before.ViewCount,
            true);
    }
}

/// <summary>calibration.export 实现。</summary>
public sealed class CalibrationExportCapability : ICapabilityHandler<CalibrationExportCommand>
{
    private readonly ICalibrationStore _store;

    public CalibrationExportCapability(ICalibrationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CalibrationExportCommand.Id,
        Title: "导出标定",
        Description: "返回已保存的标定记录（JSON）。未标定时返回 found=false。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CalibrationExportCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "cameraId": { "type": "string" } }, "required": ["cameraId"] }
        """);

    public async Task<object?> HandleAsync(
        CalibrationExportCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        CalibrationRecord? record = await _store.GetAsync(command.CameraId, cancellationToken).ConfigureAwait(false);

        return new
        {
            found = record is not null,
            record
        };
    }
}

/// <summary>calibration.solveHandEye 实现。</summary>
public sealed class CalibrationSolveHandEyeCapability
    : ICapabilityHandler<CalibrationSolveHandEyeCommand>
{
    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CalibrationSolveHandEyeCommand.Id,
        Title: "手眼标定 AX=XB",
        Description:
            "由若干『机器人位姿 + 对应相机位姿』求解相机在法兰上的位姿。" +
            "机器人姿态必须有明显旋转差异（纯平移无效）；至少 3 组，建议 10 组以上。" +
            "返回 X 与残差；残差偏大不要直接用于抓取。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CalibrationSolveHandEyeCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "samples": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "robotPose": { "type": "object", "description": "gripperInBase：rotation[3][3] + translation[3]" },
                  "cameraPose": { "type": "object", "description": "targetToCamera：rotation[3][3] + translation[3]" }
                },
                "required": ["robotPose", "cameraPose"]
              }
            }
          },
          "required": ["samples"]
        }
        """);

    public Task<object?> HandleAsync(
        CalibrationSolveHandEyeCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command.Samples);

        var samples = command.Samples
            .Select(s => new HandEyeSample(s.RobotPose, s.CameraPose))
            .ToList();

        HandEyeResult result = HandEyeCalibrator.Calibrate(samples);

        return Task.FromResult<object?>(new
        {
            ok = result.Ok,
            x = result.CameraInGripper,
            rotationErrorDeg = result.RotationErrorDeg,
            translationErrorMm = result.TranslationErrorMm,
            pairCount = result.PairCount,
            error = result.Error,
            diagnostics = result.Diagnostics
        });
    }
}
