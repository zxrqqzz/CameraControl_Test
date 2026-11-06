namespace CameraControl.Domain.Analysis;

/// <summary>灰度图（8 位）。分析用，不参与业务。</summary>
public sealed class GrayImage
{
    public GrayImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(pixels);

        if (pixels.Length < width * height)
            throw new ArgumentException("像素数据长度不足。", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public byte At(int x, int y) => Pixels[(y * Width) + x];

    /// <summary>从原始帧像素转灰度（支持 Gray8 / Bgr24 / Rgb24 / Bgra32）。</summary>
    public static GrayImage FromFrame(
        int width,
        int height,
        int stride,
        string pixelFormat,
        ReadOnlySpan<byte> pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var result = new byte[width * height];

        int bytesPerPixel = pixelFormat switch
        {
            "Gray8" => 1,
            "Bgr24" or "Rgb24" => 3,
            "Bgra32" => 4,
            _ => 0
        };

        if (bytesPerPixel == 0)
            throw new NotSupportedException($"不支持的像素格式：{pixelFormat}。");

        for (int y = 0; y < height; y++)
        {
            int srcRow = y * stride;
            int dstRow = y * width;

            for (int x = 0; x < width; x++)
            {
                int i = srcRow + (x * bytesPerPixel);
                if (i + bytesPerPixel > pixels.Length) break;

                byte gray = bytesPerPixel switch
                {
                    1 => pixels[i],
                    4 => pixels[i],
                    _ => pixelFormat == "Bgr24"
                        // ITU-R BT.601 近似
                        ? (byte)((pixels[i] * 29 + pixels[i + 1] * 150 + pixels[i + 2] * 77) >> 8)
                        : (byte)((pixels[i] * 77 + pixels[i + 1] * 150 + pixels[i + 2] * 29) >> 8)
                };

                result[dstRow + x] = gray;
            }
        }

        return new GrayImage(width, height, result);
    }
}

/// <summary>感兴趣区域。</summary>
/// <param name="X">左上角 X。</param>
/// <param name="Y">左上角 Y。</param>
/// <param name="Width">宽度。</param>
/// <param name="Height">高度。</param>
public readonly record struct Roi(int X, int Y, int Width, int Height)
{
    /// <summary>取图像中央的方形区域。</summary>
    public static Roi CenterSquare(int imageWidth, int imageHeight, double fraction = 0.25)
    {
        int size = (int)Math.Max(16, Math.Min(imageWidth, imageHeight) * fraction);
        size = Math.Min(size, Math.Min(imageWidth, imageHeight));

        return new Roi((imageWidth - size) / 2, (imageHeight - size) / 2, size, size);
    }
}

/// <summary>
/// 帧间位移测量结果。
/// </summary>
/// <param name="Dx">水平位移（像素，亚像素精度）。</param>
/// <param name="Dy">垂直位移（像素，亚像素精度）。</param>
/// <param name="Amplitude">幅值 √(dx²+dy²)。</param>
/// <param name="Score">最佳匹配的归一化互相关值（越接近 1 越可信）。</param>
public readonly record struct OffsetMeasurement(double Dx, double Dy, double Amplitude, double Score)
{
    public static OffsetMeasurement Invalid => new(double.NaN, double.NaN, double.NaN, 0);
}
