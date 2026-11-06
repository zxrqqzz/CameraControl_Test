namespace CameraControl.Domain.Capture;

/// <summary>触发源类型。调用方只换参数，不改代码。</summary>
public enum TriggerSourceKind
{
    /// <summary>软件触发：由本程序依次/并发向相机下发触发命令。P0 实现。</summary>
    Software = 0,

    /// <summary>硬件触发：由外部信号（PLC / 光源控制器）同时驱动所有相机曝光。P2 实现。</summary>
    Hardware = 1
}

/// <summary>采集模式（对应工艺阶段）。</summary>
public enum CaptureMode
{
    /// <summary>
    /// 静止采集：机械臂到位后，等振动衰减到阈值以下再拍。
    /// P0 ~ P2 初期使用。
    /// </summary>
    StaticSettled = 0,

    /// <summary>
    /// 运动冻结：机械臂仍在运动时同步曝光。
    /// P2 后期（节拍不足时）启用，需配齐四件套：位置触发 + 频闪 + 短曝光 + 同一脉冲。
    /// </summary>
    MotionFrozen = 1
}

/// <summary>
/// 触发方式。
/// </summary>
/// <param name="Kind">触发源类型。</param>
/// <param name="HardwareSourceId">硬触发源 id（P2：PLC 通道 / 光源控制器），<see cref="TriggerSourceKind.Hardware"/> 时必填。</param>
/// <param name="SoftwareDelayUs">软件触发时的补偿延时（µs），用于对齐多相机曝光。</param>
public sealed record TriggerSpec(
    TriggerSourceKind Kind,
    string? HardwareSourceId = null,
    int SoftwareDelayUs = 0)
{
    /// <summary>软件触发。</summary>
    public static TriggerSpec Software(int delayUs = 0) => new(TriggerSourceKind.Software, null, delayUs);

    /// <summary>硬件触发。</summary>
    public static TriggerSpec Hardware(string hardwareSourceId) =>
        new(TriggerSourceKind.Hardware, hardwareSourceId);

    public bool IsHardware => Kind == TriggerSourceKind.Hardware;
}
