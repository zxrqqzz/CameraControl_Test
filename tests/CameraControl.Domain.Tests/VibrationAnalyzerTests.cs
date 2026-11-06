using CameraControl.Domain.Analysis;
using Xunit;

namespace CameraControl.Domain.Tests;

/// <summary>
/// 帧间位移测量。这是"用相机当振动传感器"的数学内核，
/// 必须先用合成数据证明它算得准，才能拿去测真实工位。
///
/// 基准构造方式：用**连续解析函数**在两个亚像素相位上分别采样，
/// 而不是"先采样再插值平移"——后者会引入低通模糊，污染真实精度。
/// </summary>
public sealed class VibrationAnalyzerTests
{
    private const int Size = 128;

    /// <summary>
    /// 连续平滑场：三个不同周期的正弦叠加，梯度在各方向都足够强。
    /// 周期 17/23/29 远大于搜索半径，避免相关性出现多峰。
    /// </summary>
    private static double Field(double x, double y) =>
        128
        + (60 * Math.Sin(2 * Math.PI * x / 17.0))
        + (40 * Math.Sin(2 * Math.PI * y / 23.0))
        + (30 * Math.Sin(2 * Math.PI * (x + y) / 29.0));

    /// <summary>按 (dx, dy) 相位采样连续场（即"内容向右下移动了 dx,dy"）。</summary>
    private static GrayImage CreateField(double dx, double dy)
    {
        var pixels = new byte[Size * Size];

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double v = Field(x - dx, y - dy);
                pixels[(y * Size) + x] = (byte)Math.Clamp(v, 0, 255);
            }
        }

        return new GrayImage(Size, Size, pixels);
    }

    [Fact]
    public void 完全相同的两帧位移应接近零()
    {
        GrayImage reference = CreateField(0, 0);
        Roi roi = Roi.CenterSquare(Size, Size, 0.375);

        OffsetMeasurement m = VibrationAnalyzer.Measure(reference, reference, roi);

        // 亚像素噪声底：实测 ~0.006px，远低于任何真实精度要求
        Assert.True(Math.Abs(m.Dx) < 0.02, $"Dx={m.Dx}");
        Assert.True(Math.Abs(m.Dy) < 0.02, $"Dy={m.Dy}");
        Assert.True(m.Score > 0.99, $"Score={m.Score}");
    }

    [Theory]
    [InlineData(3, -2)]
    [InlineData(-4, 5)]
    [InlineData(7, 0)]
    [InlineData(0, 6)]
    public void 整数位移应被准确测出(int dx, int dy)
    {
        GrayImage reference = CreateField(0, 0);
        GrayImage target = CreateField(dx, dy);
        Roi roi = Roi.CenterSquare(Size, Size, 0.375);

        OffsetMeasurement m = VibrationAnalyzer.Measure(reference, target, roi, searchRadius: 10);

        Assert.True(Math.Abs(m.Dx - dx) < 0.1, $"Dx={m.Dx} 期望 {dx}");
        Assert.True(Math.Abs(m.Dy - dy) < 0.1, $"Dy={m.Dy} 期望 {dy}");
        Assert.True(m.Score > 0.9, $"Score={m.Score}");
    }

    [Theory]
    [InlineData(2.4, -1.6)]
    [InlineData(-0.35, 0.85)]
    [InlineData(1.75, 3.25)]
    public void 亚像素位移应被测量到零点二像素以内(double dx, double dy)
    {
        GrayImage reference = CreateField(0, 0);
        GrayImage target = CreateField(dx, dy);
        Roi roi = Roi.CenterSquare(Size, Size, 0.375);

        OffsetMeasurement m = VibrationAnalyzer.Measure(reference, target, roi, searchRadius: 8);

        Assert.True(Math.Abs(m.Dx - dx) < 0.2, $"Dx={m.Dx} 期望 {dx}");
        Assert.True(Math.Abs(m.Dy - dy) < 0.2, $"Dy={m.Dy} 期望 {dy}");
        Assert.True(m.Amplitude > 0);
    }

    [Fact]
    public void 纯色图应被判为不可信()
    {
        var flat = new GrayImage(Size, Size, new byte[Size * Size]);
        Roi roi = Roi.CenterSquare(Size, Size, 0.375);

        OffsetMeasurement m = VibrationAnalyzer.Measure(flat, flat, roi);

        Assert.Equal(double.NaN, m.Dx);
    }

    [Fact]
    public void 尺寸不一致应抛异常()
    {
        GrayImage a = CreateField(0, 0);
        var b = new GrayImage(64, 64, new byte[64 * 64]);
        Roi roi = Roi.CenterSquare(Size, Size, 0.375);

        Assert.Throws<ArgumentException>(() => VibrationAnalyzer.Measure(a, b, roi));
    }
}
