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
}
