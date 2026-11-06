namespace CameraControl.Domain.Capture;

/// <summary>
/// 时间戳可信度。
/// 这是**类型级区分**：融合层不得把 <see cref="SoftwareApprox"/> 当硬同步使用。
/// </summary>
public enum TimestampQuality
{
    /// <summary>软件近似：由帧到达时间推定，不承诺同步精度。</summary>
    SoftwareApprox = 0,

    /// <summary>硬件打标：由触发信号/相机硬件时间戳产生。</summary>
    HardwareStamped = 1
}

/// <summary>
/// 一帧图像及其像素数据。
/// 像素数据只存在内存中（隐私约束：UVC/内置摄像头绝不落盘）。
/// </summary>
/// <param name="Width">宽度（像素）。</param>
/// <param name="Height">高度（像素）。</param>
/// <param name="Stride">行字节数。</param>
/// <param name="PixelFormat">像素格式标识，如 Bgra32 / Mono8。</param>
/// <param name="Pixels">像素数据。</param>
public sealed record FrameImage(
    int Width,
    int Height,
    int Stride,
    string PixelFormat,
    ReadOnlyMemory<byte> Pixels)
{
    public long ByteLength => Pixels.Length;
}

/// <summary>
/// 一帧采集结果。
/// </summary>
/// <param name="FrameId">帧标识。</param>
/// <param name="CameraId">来源相机。</param>
/// <param name="TimestampUtc">该帧的采集时间（UTC）。</param>
/// <param name="OffsetFromGroup">相对组级时间戳的偏移。</param>
/// <param name="Image">像素数据。</param>
public sealed record Frame(
    string FrameId,
    Cameras.CameraId CameraId,
    DateTimeOffset TimestampUtc,
    TimeSpan OffsetFromGroup,
    FrameImage Image);
