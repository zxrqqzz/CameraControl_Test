namespace CameraControl.Application.Commands;

/// <summary>
/// 能力风险等级。
/// **失败关闭原则**：未声明风险等级的能力一律按 <see cref="Destructive"/> 处理。
/// </summary>
public enum RiskLevel
{
    /// <summary>只读：查状态、读参数、读位姿。AI 可直接执行。</summary>
    ReadOnly = 0,

    /// <summary>可逆：改曝光、改亮度、切坐标系。AI 可执行但必须留痕。</summary>
    Reversible = 1,

    /// <summary>物理：机器人运动、设当前位姿。**必须人工确认**。</summary>
    Physical = 2,

    /// <summary>破坏性：删设备、清标定、改 IP。**必须确认 + 二次校验**。</summary>
    Destructive = 3
}

/// <summary>
/// 能力描述符。既用于白名单校验，也用于导出给 LLM 的 JSON Schema。
/// </summary>
/// <param name="Id">能力 id，如 capture.group。</param>
/// <param name="Title">短标题。</param>
/// <param name="Description">给 LLM 阅读的自然语言说明。</param>
/// <param name="Risk">风险等级。</param>
/// <param name="CommandType">对应的命令类型（用于注册表分派）。</param>
/// <param name="ParametersJsonSchema">参数 JSON Schema（P0 可为占位）。</param>
public sealed record CapabilityDescriptor(
    string Id,
    string Title,
    string Description,
    RiskLevel Risk,
    Type CommandType,
    string? ParametersJsonSchema = null)
{
    /// <summary>是否需要人工确认：物理与破坏性操作需要。</summary>
    public bool RequiresConfirmation => Risk >= RiskLevel.Physical;
}
