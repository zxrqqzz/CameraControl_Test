namespace CameraControl.Domain.Cameras;

/// <summary>相机接入类型（决定走哪条适配器通道）。</summary>
public enum CameraKind
{
    /// <summary>未知/未识别</summary>
    Unknown = 0,

    /// <summary>海康 GigE Vision</summary>
    HikGigE = 1,

    /// <summary>海康 USB3 Vision</summary>
    HikUsb3 = 2,

    /// <summary>UVC（含笔记本内置摄像头）</summary>
    Uvc = 3
}

/// <summary>
/// 相机安装形态。
/// 当前为 <see cref="EyeInHand"/>（装在机械臂上）；未来多相机方案为 <see cref="EyeToHand"/>（固定在工作区）。
/// </summary>
public enum CameraMountKind
{
    /// <summary>眼在手上：外参来自采集时刻的机器人位姿，位姿随动。</summary>
    EyeInHand = 0,

    /// <summary>眼在手外：一次性标定各相机外参后固定。</summary>
    EyeToHand = 1
}

/// <summary>
/// 相机的静态描述信息。
/// 只描述"这台相机是什么"，不包含运行时状态。
/// </summary>
public sealed record CameraDescriptor(
    CameraId Id,
    string DisplayName,
    string Vendor,
    string Model,
    string? SerialNumber,
    CameraKind Kind,
    CameraMountKind Mount = CameraMountKind.EyeInHand,
    bool IsInfraredOnly = false,
    bool SupportsHardwareTrigger = false,
    double? PixelSizeMm = null)
{
    /// <summary>红外摄像头仅用于 Windows Hello 人脸，不参与取流。</summary>
    public bool CanCapture => !IsInfraredOnly;
}
