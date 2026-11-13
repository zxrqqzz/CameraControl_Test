using System.Text.Json;
using CameraControl.Application.Capabilities;
using CameraControl.Application.Commands;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Capture;

namespace CameraControl.Application.Cua;

/// <summary>
/// 把模型给的 JSON 参数绑定成强类型命令。
///
/// 为什么集中在一个类里而不是让每个能力自己解析：
/// - 绑定逻辑是"AI 输入边界"，集中便于审计与模糊测试；
/// - 参数错误能返回结构化错误让 AI 自我纠正，而不是抛异常。
/// </summary>
public interface ICommandBinder
{
    /// <summary>绑定成功返回命令；失败返回 null 并给出原因。</summary>
    ICommand? Bind(string capabilityId, JsonElement arguments, out string? error);
}

/// <summary>默认绑定器（覆盖当前已实现能力）。</summary>
public sealed class DefaultCommandBinder : ICommandBinder
{
    public ICommand? Bind(string capabilityId, JsonElement arguments, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        error = null;

        try
        {
            return capabilityId switch
            {
                CaptureGroupCommand.Id => BindCaptureGroup(arguments, out error),
                DeviceListCommand.Id => new DeviceListCommand(),
                SystemSelfCheckCommand.Id => new SystemSelfCheckCommand(),
                SystemGetMetricsCommand.Id => new SystemGetMetricsCommand(),
                SystemGetAuditLogCommand.Id => new SystemGetAuditLogCommand(
                    JsonArgs.GetInt(arguments, "count") ?? 50),
                CuaGetCapabilitiesCommand.Id => new CuaGetCapabilitiesCommand(
                    ParseFormat(JsonArgs.GetString(arguments, "format"))),

                RobotGetStateCommand.Id => new RobotGetStateCommand(),
                RobotStopCommand.Id => new RobotStopCommand(),
                RobotMoveToCommand.Id => BindRobotMoveTo(arguments),

                CalibrationGetCommand.Id => BindCalibrationGet(arguments),
                CalibrationListCommand.Id => new CalibrationListCommand(),
                CalibrationRemoveCommand.Id => BindCalibrationRemove(arguments),
                CalibrationSolveCommand.Id => BindCalibrationSolve(arguments),

                // ── 相机与采集 ──────────────────────────────
            CameraOpenCommand.Id => new CameraOpenCommand(
                JsonArgs.RequireString(arguments, "cameraId")),
            CameraCloseCommand.Id => new CameraCloseCommand(
                JsonArgs.RequireString(arguments, "cameraId")),
            CameraListOpenCommand.Id => new CameraListOpenCommand(),
            CameraGetStatusCommand.Id => new CameraGetStatusCommand(
                JsonArgs.RequireString(arguments, "cameraId")),
            CaptureSingleCommand.Id => new CaptureSingleCommand(
                JsonArgs.RequireString(arguments, "cameraId"),
                JsonArgs.GetInt(arguments, "exposureUs"),
                JsonArgs.GetInt(arguments, "gain"),
                JsonArgs.GetBool(arguments, "includeImage") ?? false),
            CaptureArmCommand.Id => new CaptureArmCommand(
                JsonArgs.RequireString(arguments, "cameraId"),
                JsonArgs.GetString(arguments, "triggerKind") ?? "software",
                JsonArgs.GetInt(arguments, "softwareDelayUs") ?? 0),
            CaptureDisarmCommand.Id => new CaptureDisarmCommand(
                JsonArgs.RequireString(arguments, "cameraId")),
            CaptureTriggerCommand.Id => new CaptureTriggerCommand(
                JsonArgs.RequireString(arguments, "cameraId")),
            CaptureWaitSettledCommand.Id => new CaptureWaitSettledCommand(
                JsonArgs.GetInt(arguments, "timeoutMs") ?? 2000,
                JsonArgs.GetInt(arguments, "fixedDelayMs") ?? 0),
            CaptureGetStatsCommand.Id => new CaptureGetStatsCommand(
                JsonArgs.RequireString(arguments, "cameraId")),
            CaptureStartContinuousCommand.Id => new CaptureStartContinuousCommand(
                JsonArgs.RequireString(arguments, "cameraId"),
                JsonArgs.GetInt(arguments, "fps") ?? 10),
            CaptureStopContinuousCommand.Id => new CaptureStopContinuousCommand(
                JsonArgs.RequireString(arguments, "cameraId")),
            CaptureGetFrameCommand.Id => new CaptureGetFrameCommand(
                JsonArgs.RequireString(arguments, "cameraId"),
                JsonArgs.GetBool(arguments, "includeImage") ?? true,
                JsonArgs.GetInt(arguments, "timeoutMs") ?? 1000),

            // ── 光源 ────────────────────────────────────
            LightOnCommand.Id => new LightOnCommand(JsonArgs.GetInt(arguments, "channel") ?? 1),
            LightOffCommand.Id => new LightOffCommand(JsonArgs.GetInt(arguments, "channel") ?? 1),
            LightSetBrightnessCommand.Id => new LightSetBrightnessCommand(
                JsonArgs.GetInt(arguments, "channel") ?? 1,
                JsonArgs.GetDouble(arguments, "percent") ?? 50),
            LightPulseCommand.Id => new LightPulseCommand(
                JsonArgs.GetInt(arguments, "channel") ?? 1,
                JsonArgs.GetInt(arguments, "durationUs") ?? 1000),
            LightStrobeCommand.Id => new LightStrobeCommand(
                JsonArgs.GetInt(arguments, "channel") ?? 1,
                JsonArgs.GetInt(arguments, "durationUs") ?? 1000,
                JsonArgs.GetInt(arguments, "count") ?? 1),
            LightGetStateCommand.Id => new LightGetStateCommand(JsonArgs.GetInt(arguments, "channel") ?? 1),
            LightListCommand.Id => new LightListCommand(),

            // ── 通讯 ────────────────────────────────────
            CommAddCommand.Id => new CommAddCommand(
                JsonArgs.GetString(arguments, "id") ?? throw new JsonException("id 必填。"),
                JsonArgs.GetString(arguments, "kind") ?? throw new JsonException("kind 必填。"),
                JsonArgs.GetString(arguments, "endpoint") ?? throw new JsonException("endpoint 必填。")),
            CommRemoveCommand.Id => new CommRemoveCommand(
                JsonArgs.GetString(arguments, "id") ?? throw new JsonException("id 必填。")),
            CommListCommand.Id => new CommListCommand(),
            CommOpenCommand.Id => new CommOpenCommand(
                JsonArgs.GetString(arguments, "id") ?? throw new JsonException("id 必填。")),
            CommCloseCommand.Id => new CommCloseCommand(
                JsonArgs.GetString(arguments, "id") ?? throw new JsonException("id 必填。")),
            CommSendCommand.Id => new CommSendCommand(
                JsonArgs.GetString(arguments, "id") ?? throw new JsonException("id 必填。"),
                JsonArgs.GetString(arguments, "hex") ?? throw new JsonException("hex 必填。")),
            CommGetLogCommand.Id => new CommGetLogCommand(
                JsonArgs.GetString(arguments, "id") ?? throw new JsonException("id 必填。"),
                JsonArgs.GetInt(arguments, "maxEntries") ?? 50),

            // ── 机器人补充 ──────────────────────────────
            RobotGetPoseCommand.Id => new RobotGetPoseCommand(),
            RobotMoveRelativeCommand.Id => new RobotMoveRelativeCommand(
                JsonArgs.RequireDouble(arguments, "dx"),
                JsonArgs.RequireDouble(arguments, "dy"),
                JsonArgs.RequireDouble(arguments, "dz"),
                JsonArgs.GetDouble(arguments, "drx") ?? 0,
                JsonArgs.GetDouble(arguments, "dry") ?? 0,
                JsonArgs.GetDouble(arguments, "drz") ?? 0,
                JsonArgs.GetInt(arguments, "timeoutMs") ?? 30000),
            RobotCheckSoftLimitCommand.Id => new RobotCheckSoftLimitCommand(
                JsonArgs.GetDouble(arguments, "x"),
                JsonArgs.GetDouble(arguments, "y"),
                JsonArgs.GetDouble(arguments, "z"),
                JsonArgs.GetDouble(arguments, "rx"),
                JsonArgs.GetDouble(arguments, "ry"),
                JsonArgs.GetDouble(arguments, "rz")),
            RobotGetUserFrameCommand.Id => new RobotGetUserFrameCommand(JsonArgs.GetInt(arguments, "index") ?? 1),
            RobotSetUserFrameCommand.Id => new RobotSetUserFrameCommand(
                JsonArgs.GetInt(arguments, "index") ?? throw new JsonException("index 必填。"),
                JsonArgs.RequireDouble(arguments, "x"),
                JsonArgs.RequireDouble(arguments, "y"),
                JsonArgs.RequireDouble(arguments, "z"),
                JsonArgs.RequireDouble(arguments, "rx"),
                JsonArgs.RequireDouble(arguments, "ry"),
                JsonArgs.RequireDouble(arguments, "rz")),

            // ── 标定会话 ────────────────────────────────
            CalibrationDetectCornersCommand.Id => new CalibrationDetectCornersCommand(
                JsonArgs.RequireString(arguments, "cameraId"),
                JsonArgs.GetBool(arguments, "addToSession") ?? true),
            CalibrationGetStateCommand.Id => new CalibrationGetStateCommand(
                JsonArgs.RequireString(arguments, "cameraId")),
            CalibrationConfigureCommand.Id => new CalibrationConfigureCommand(
                JsonArgs.RequireString(arguments, "cameraId"),
                JsonArgs.GetInt(arguments, "cols") ?? 12,
                JsonArgs.GetInt(arguments, "rows") ?? 13,
                JsonArgs.GetDouble(arguments, "squareMm") ?? 30),
            CalibrationAddPointCommand.Id => BindCalibrationAddPoint(arguments),
            CalibrationRemovePointCommand.Id => new CalibrationRemovePointCommand(
                JsonArgs.RequireString(arguments, "cameraId"),
                JsonArgs.GetInt(arguments, "index") ?? throw new JsonException("index 必填。")),
            CalibrationResetCommand.Id => new CalibrationResetCommand(
                JsonArgs.RequireString(arguments, "cameraId")),
            CalibrationApplyCommand.Id => new CalibrationApplyCommand(
                JsonArgs.RequireString(arguments, "cameraId"),
                JsonArgs.GetInt(arguments, "imageWidth") ?? throw new JsonException("imageWidth 必填。"),
                JsonArgs.GetInt(arguments, "imageHeight") ?? throw new JsonException("imageHeight 必填。"),
                JsonArgs.GetString(arguments, "note")),
            CalibrationExportCommand.Id => new CalibrationExportCommand(
                JsonArgs.RequireString(arguments, "cameraId")),
            CalibrationSolveHandEyeCommand.Id => BindCalibrationSolveHandEye(arguments),

            // ── 设备 / 系统 / CUA 补充 ──────────────────
            DeviceGetCommand.Id => new DeviceGetCommand(
                JsonArgs.RequireString(arguments, "cameraId")),
            DeviceRenameCommand.Id => new DeviceRenameCommand(
                JsonArgs.RequireString(arguments, "cameraId"),
                JsonArgs.GetString(arguments, "alias")),
            SystemGetEnvironmentCommand.Id => new SystemGetEnvironmentCommand(),
            SystemGetSettingsCommand.Id => new SystemGetSettingsCommand(),
            SystemExportDiagnosticsCommand.Id => new SystemExportDiagnosticsCommand(
                JsonArgs.GetString(arguments, "outputPath")),
            CuaGetAuthorizationModeCommand.Id => new CuaGetAuthorizationModeCommand(),
            CuaStopCommand.Id => new CuaStopCommand(),
            CuaRequestElevationCommand.Id => new CuaRequestElevationCommand(
                JsonArgs.GetString(arguments, "reason") ?? string.Empty),

            _ => Unknown(capabilityId, out error)
            };
        }
        catch (JsonException ex)
        {
            error = $"参数不是合法 JSON：{ex.Message}";
            return null;
        }
    }

    private static ICommand? Unknown(string capabilityId, out string? error)
    {
        error = $"能力 {capabilityId} 没有参数绑定器（可能尚未接入 AI 调用）。";
        return null;    }

    private static CapabilityCatalogFormat ParseFormat(string? value) =>
        string.Equals(value, "Markdown", StringComparison.OrdinalIgnoreCase)
            ? CapabilityCatalogFormat.Markdown
            : CapabilityCatalogFormat.Json;

    /// <summary>绑定 calibration.addPoint：把 JSON 的点数组转成 Point2 列表。</summary>
    private static ICommand BindCalibrationAddPoint(JsonElement arguments)
    {
        string cameraId = JsonArgs.RequireString(arguments, "cameraId");

        if (!arguments.TryGetProperty("imagePoints", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            throw new JsonException("imagePoints 必须是点数组。");

        var points = new List<CameraControl.Domain.Calibration.Point2>(array.GetArrayLength());

        foreach (JsonElement item in array.EnumerateArray())
        {
            points.Add(new CameraControl.Domain.Calibration.Point2(
                JsonArgs.RequireDouble(item, "x"),
                JsonArgs.RequireDouble(item, "y")));
        }

        return new CalibrationAddPointCommand(cameraId, points);
    }

    /// <summary>绑定 calibration.solveHandEye：把手眼采样从 JSON 解出来。</summary>
    private static ICommand BindCalibrationSolveHandEye(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("samples", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            throw new JsonException("samples 必须是数组。");

        var samples = new List<HandEyeSampleDto>(array.GetArrayLength());

        foreach (JsonElement item in array.EnumerateArray())
        {
            samples.Add(new HandEyeSampleDto(
                ParseTransform(item, "robotPose"),
                ParseTransform(item, "cameraPose")));
        }

        return new CalibrationSolveHandEyeCommand(samples);
    }

    /// <summary>解析 rotation[3][3] + translation[3] 形式的刚体变换。</summary>
    private static CameraControl.Domain.Calibration.Transform3D ParseTransform(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element))
            throw new JsonException($"{name} 必填。");

        if (!element.TryGetProperty("rotation", out JsonElement rotation) || rotation.GetArrayLength() != 3)
            throw new JsonException($"{name}.rotation 必须是 3×3。");

        var matrix = new double[3, 3];

        for (int r = 0; r < 3; r++)
        {
            JsonElement row = rotation[r];

            if (row.GetArrayLength() != 3) throw new JsonException($"{name}.rotation 必须是 3×3。");

            for (int c = 0; c < 3; c++) matrix[r, c] = row[c].GetDouble();
        }

        if (!element.TryGetProperty("translation", out JsonElement translation) || translation.GetArrayLength() != 3)
            throw new JsonException($"{name}.translation 必须是 3 个数。");

        var t = new double[3];
        for (int i = 0; i < 3; i++) t[i] = translation[i].GetDouble();

        return new CameraControl.Domain.Calibration.Transform3D(matrix, t);
    }
    private static ICommand? BindCaptureGroup(JsonElement arguments, out string? error)
    {
        error = null;

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = "参数必须是 JSON 对象。";
            return null;
        }

        IReadOnlyList<string> cameraIds = JsonArgs.GetStringArray(arguments, "cameraIds");
        if (cameraIds.Count == 0)
        {
            error = "cameraIds 不能为空。可先调用 device_list 查询可用相机 id。";
            return null;
        }

        if (!arguments.TryGetProperty("trigger", out JsonElement triggerElement)
            || triggerElement.ValueKind != JsonValueKind.Object)
        {
            error = "缺少 trigger 对象，例如 {\"kind\":\"software\"}。";
            return null;
        }

        string? kind = JsonArgs.GetString(triggerElement, "kind");
        TriggerSpec trigger = kind?.ToLowerInvariant() switch
        {
            "software" => TriggerSpec.Software(JsonArgs.GetInt(triggerElement, "softwareDelayUs") ?? 0),
            "hardware" => TriggerSpec.Hardware(
                JsonArgs.GetString(triggerElement, "hardwareSourceId")
                ?? throw new JsonException("hardware 触发必须提供 hardwareSourceId。")),
            null => throw new JsonException("trigger.kind 必填（software 或 hardware）。"),
            _ => throw new JsonException($"未知 trigger.kind：{kind}。")
        };

        CaptureMode mode = string.Equals(
            JsonArgs.GetString(arguments, "mode"),
            "MotionFrozen",
            StringComparison.OrdinalIgnoreCase)
            ? CaptureMode.MotionFrozen
            : CaptureMode.StaticSettled;

        int timeoutMs = JsonArgs.GetInt(arguments, "timeoutMs") ?? CaptureGroupRequest.DefaultTimeoutMs;

        return new CaptureGroupCommand(
            CameraIds: [.. cameraIds.Select(id => new CameraId(id))],
            Trigger: trigger,
            Mode: mode,
            TimeoutMs: timeoutMs,
            Readiness: null,
            ArmFirst: JsonArgs.GetBool(arguments, "armFirst") ?? false);
    }

    private static ICommand BindRobotMoveTo(JsonElement arguments)
    {
        var target = new Domain.Robotics.RobotPose(
            X: JsonArgs.RequireDouble(arguments, "x"),
            Y: JsonArgs.RequireDouble(arguments, "y"),
            Z: JsonArgs.RequireDouble(arguments, "z"),
            Rx: JsonArgs.RequireDouble(arguments, "rx"),
            Ry: JsonArgs.RequireDouble(arguments, "ry"),
            Rz: JsonArgs.RequireDouble(arguments, "rz"),
            UserFrame: JsonArgs.GetInt(arguments, "userFrame") ?? 1);

        int timeoutMs = Math.Clamp(JsonArgs.GetInt(arguments, "timeoutMs") ?? 30000, 100, 600_000);

        return new RobotMoveToCommand(target, timeoutMs);
    }

    // ── 标定 ────────────────────────────────────────────────

    private static ICommand BindCalibrationGet(JsonElement arguments)
    {
        string cameraId = JsonArgs.GetString(arguments, "cameraId")
            ?? throw new JsonException("缺少 cameraId。");

        return new CalibrationGetCommand(cameraId);
    }

    private static ICommand BindCalibrationRemove(JsonElement arguments)
    {
        string cameraId = JsonArgs.GetString(arguments, "cameraId")
            ?? throw new JsonException("缺少 cameraId。");

        return new CalibrationRemoveCommand(cameraId);
    }

    private static ICommand BindCalibrationSolve(JsonElement arguments)
    {
        string cameraId = JsonArgs.GetString(arguments, "cameraId")
            ?? throw new JsonException("缺少 cameraId。");

        int gridCols = JsonArgs.GetInt(arguments, "gridCols")
            ?? throw new JsonException("缺少 gridCols。");

        int gridRows = JsonArgs.GetInt(arguments, "gridRows")
            ?? throw new JsonException("缺少 gridRows。");

        double squareMm = JsonArgs.RequireDouble(arguments, "squareMm");

        int imageWidth = JsonArgs.GetInt(arguments, "imageWidth")
            ?? throw new JsonException("缺少 imageWidth。");

        int imageHeight = JsonArgs.GetInt(arguments, "imageHeight")
            ?? throw new JsonException("缺少 imageHeight。");

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("views", out JsonElement viewsElement)
            || viewsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("缺少 views 数组。");
        }

        var views = new List<IReadOnlyList<Domain.Calibration.Point2>>();

        foreach (JsonElement view in viewsElement.EnumerateArray())
        {
            if (view.ValueKind != JsonValueKind.Array)
                throw new JsonException("views 的每一项必须是点数组。");

            var points = new List<Domain.Calibration.Point2>();

            foreach (JsonElement point in view.EnumerateArray())
            {
                points.Add(new Domain.Calibration.Point2(
                    JsonArgs.RequireDouble(point, "x"),
                    JsonArgs.RequireDouble(point, "y")));
            }

            views.Add(points);
        }

        return new CalibrationSolveCommand(
            cameraId,
            gridCols,
            gridRows,
            squareMm,
            imageWidth,
            imageHeight,
            views,
            JsonArgs.GetString(arguments, "note"));
    }
}
