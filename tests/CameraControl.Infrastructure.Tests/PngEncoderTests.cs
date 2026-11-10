using CameraControl.Domain.Capture;
using CameraControl.Infrastructure.Imaging;
using Xunit;

namespace CameraControl.Infrastructure.Tests;

/// <summary>
/// PNG 编码器：不需要第三方库、不需要解码器，
/// 只校验 PNG 结构合法（签名 / IHDR 尺寸与格式 / IDAT 可还原 / IEND）。
/// </summary>
public sealed class PngEncoderTests
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void 应生成合法PNG结构()
    {
        var image = new FrameImage(4, 3, 16, "Bgra32", new byte[4 * 3 * 4]);

        byte[] png = PngEncoder.Encode(image);

        Assert.True(png.Length > 8);
        Assert.Equal(Signature, png[..8]);

        // IHDR 必须是第一个块
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));

        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));

        Assert.Equal(4, width);
        Assert.Equal(3, height);
        Assert.Equal(8, png[24]);       // bit depth
        Assert.Equal(2, png[25]);       // color type = RGB
        Assert.Equal(0, png[28]);       // interlace = none

        // 结尾必须是 IEND
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }

    [Fact]
    public void 灰度图应该是灰度色彩类型()
    {
        var image = new FrameImage(2, 2, 2, "Mono8", new byte[4]);

        byte[] png = PngEncoder.Encode(image);

        Assert.Equal(0, png[25]); // color type = grayscale
    }

    [Fact]
    public void 红色像素应正确转换成RGB顺序()
    {
        // 2x1 纯红：BGRA = (0,0,255,255)
        var pixels = new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 };
        var image = new FrameImage(2, 1, 8, "Bgra32", pixels);

        byte[] png = PngEncoder.Encode(image);
        byte[] raw = InflateFirstIdat(png);

        // 每行 1 个 filter 字节 + 2 像素 × 3 通道
        Assert.Equal(1 + 6, raw.Length);
        Assert.Equal(0, raw[0]);          // filter = None

        Assert.Equal(255, raw[1]);        // R
        Assert.Equal(0, raw[2]);          // G
        Assert.Equal(0, raw[3]);          // B
        Assert.Equal(255, raw[4]);
    }

    [Fact]
    public void 未知像素格式应明确拒绝()
    {
        var image = new FrameImage(1, 1, 4, "Weird16", new byte[4]);

        Assert.Throws<NotSupportedException>(() => PngEncoder.Encode(image));
    }

    /// <summary>取出第一个 IDAT 的 zlib 解压结果（跳过 2 字节 zlib 头）。</summary>
    private static byte[] InflateFirstIdat(byte[] png)
    {
        int offset = 8;

        while (offset < png.Length)
        {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);

            if (type == "IDAT")
            {
                using var compressed = new MemoryStream(png, offset + 10, length - 2);
                using var inflate = new System.IO.Compression.DeflateStream(
                    compressed,
                    System.IO.Compression.CompressionMode.Decompress);
                using var result = new MemoryStream();
                inflate.CopyTo(result);
                return result.ToArray();
            }

            offset += 12 + length;
        }

        throw new InvalidOperationException("没有找到 IDAT 块。");
    }
}
