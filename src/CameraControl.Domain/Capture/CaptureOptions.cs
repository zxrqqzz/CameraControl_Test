namespace CameraControl.Domain.Capture;

/// <summary>单帧采集的可选覆盖参数。</summary>
/// <param name="ExposureUs">曝光时间（µs），null 表示沿用相机当前设置。</param>
/// <param name="Gain">增益，null 表示沿用相机当前设置。</param>
public sealed record CaptureOptions(
    int? ExposureUs = null,
    int? Gain = null);
