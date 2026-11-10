using CameraControl.Application.Capture;
using CameraControl.Domain.Capture;

namespace CameraControl.Infrastructure.Imaging;

/// <summary>
/// <see cref="IFrameEncoder"/> 的 PNG 实现。
/// 纯内存转换，不落盘（隐私约束）。
/// </summary>
public sealed class PngFrameEncoder : IFrameEncoder
{
    public byte[] EncodePng(FrameImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        return PngEncoder.Encode(image);
    }
}
