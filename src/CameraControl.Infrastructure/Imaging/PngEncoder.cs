using System.Buffers.Binary;
using System.IO.Compression;
using CameraControl.Domain.Capture;

namespace CameraControl.Infrastructure.Imaging;

/// <summary>
/// 极简 PNG 编码器（无第三方依赖）。
///
/// 为什么需要：AI/界面要"看图"就必须有可解码的图片格式；
/// 而领域层的 <see cref="FrameImage"/> 是原始像素。
/// 这里只做**无损、无 ICC、无交错**的最小合法 PNG。
///
/// 支持：<c>Bgra32</c>（转 RGBA，丢掉 alpha 预乘问题）与 <c>Mono8</c>（灰度）。
/// 隐私：纯内存转换，不落盘。
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>把帧图像编码为 PNG 字节。</summary>
    public static byte[] Encode(FrameImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        (int width, int height, int channels, byte[] raw) = ToRawPixels(image);

        using var output = new MemoryStream(raw.Length / 2 + 256);

        output.Write(Signature);

        WriteChunk(output, "IHDR", BuildHeader(width, height, channels));

        // 每行前置 filter 字节 0（None）
        int rowBytes = width * channels;
        var filtered = new byte[(rowBytes + 1) * height];

        for (int y = 0; y < height; y++)
        {
            filtered[y * (rowBytes + 1)] = 0;
            raw.AsSpan(y * rowBytes, rowBytes).CopyTo(filtered.AsSpan((y * (rowBytes + 1)) + 1, rowBytes));
        }

        WriteChunk(output, "IDAT", ZlibCompress(filtered));
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    /// <summary>按像素格式把 <see cref="FrameImage"/> 转成紧凑的 RGB/RGBA/灰度原始数据。</summary>
    private static (int Width, int Height, int Channels, byte[] Raw) ToRawPixels(FrameImage image)
    {
        ReadOnlySpan<byte> source = image.Pixels.Span;

        if (string.Equals(image.PixelFormat, "Mono8", StringComparison.OrdinalIgnoreCase))
        {
            var gray = new byte[image.Width * image.Height];

            for (int y = 0; y < image.Height; y++)
                source.Slice(y * image.Stride, image.Width).CopyTo(gray.AsSpan(y * image.Width, image.Width));

            return (image.Width, image.Height, 1, gray);
        }

        if (string.Equals(image.PixelFormat, "Bgra32", StringComparison.OrdinalIgnoreCase))
        {
            var rgb = new byte[image.Width * image.Height * 3];

            for (int y = 0; y < image.Height; y++)
            {
                ReadOnlySpan<byte> row = source.Slice(y * image.Stride, image.Width * 4);

                for (int x = 0; x < image.Width; x++)
                {
                    int s = x * 4;
                    int d = ((y * image.Width) + x) * 3;

                    // BGRA → RGB
                    rgb[d] = row[s + 2];
                    rgb[d + 1] = row[s + 1];
                    rgb[d + 2] = row[s];
                }
            }

            return (image.Width, image.Height, 3, rgb);
        }

        throw new NotSupportedException($"PNG 编码暂不支持像素格式 {image.PixelFormat}。");
    }

    private static byte[] BuildHeader(int width, int height, int channels)
    {
        var header = new byte[13];

        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);

        header[8] = 8;                                    // bit depth
        header[9] = channels switch { 1 => 0, 3 => 2, _ => 6 }; // color type: gray / rgb / rgba
        header[10] = 0;                                   // compression: deflate
        header[11] = 0;                                   // filter: adaptive
        header[12] = 0;                                   // interlace: none

        return header;
    }

    /// <summary>zlib 封装：2 字节头 + raw deflate + adler32。</summary>
    private static byte[] ZlibCompress(ReadOnlySpan<byte> data)
    {
        using var compressed = new MemoryStream();
        compressed.WriteByte(0x78);
        compressed.WriteByte(0x01);

        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data);

        uint adler = Adler32(data);
        compressed.WriteByte((byte)(adler >> 24));
        compressed.WriteByte((byte)(adler >> 16));
        compressed.WriteByte((byte)(adler >> 8));
        compressed.WriteByte((byte)adler);

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        byte[] typeBytes = [.. type.Select(c => (byte)c)];
        output.Write(typeBytes);
        output.Write(data);

        uint crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;

        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte value in type) crc = UpdateCrc(crc, value);
        foreach (byte value in data) crc = UpdateCrc(crc, value);

        return crc ^ 0xFFFFFFFF;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;

        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;

        return crc;
    }
}
