using CameraControl.Domain.Calibration;
using Xunit;

namespace CameraControl.Domain.Tests;

/// <summary>
/// 单应变换。它是 Zhang 标定的第一步，也是点阵吸附的基础。
/// 用合成对应点验证：给定真实单应，能否精确还原。
/// </summary>
public sealed class HomographyTests
{
    /// <summary>真值单应（含透视项，模拟倾斜视角）。</summary>
    private static readonly double[,] Truth =
    {
        { 1.15, 0.08, 320 },
        { -0.05, 1.02, 240 },
        { 0.00035, 0.00021, 1 }
    };

    private static Point2 ApplyTruth(Point2 p)
    {
        double w = (Truth[2, 0] * p.X) + (Truth[2, 1] * p.Y) + Truth[2, 2];

        return new Point2(
            ((Truth[0, 0] * p.X) + (Truth[0, 1] * p.Y) + Truth[0, 2]) / w,
            ((Truth[1, 0] * p.X) + (Truth[1, 1] * p.Y) + Truth[1, 2]) / w);
    }

    private static (List<Point2> From, List<Point2> To) MakePairs(int count)
    {
        // 必须造出二维分布的点（至少两行），否则单应退化为共线问题
        int columns = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(count)));

        var from = new List<Point2>(count);
        var to = new List<Point2>(count);

        for (int i = 0; i < count; i++)
        {
            var p = new Point2(i % columns * 55.0, i / columns * 47.0);
            from.Add(p);
            to.Add(ApplyTruth(p));
        }

        return (from, to);
    }

    [Fact]
    public void 四点对应应精确还原单应()
    {
        (List<Point2> from, List<Point2> to) = MakePairs(4);

        Homography? h = Homography.Compute(from, to);
        Assert.NotNull(h);

        for (int i = 0; i < from.Count; i++)
        {
            Point2 predicted = h!.Apply(from[i]);
            Assert.True(Math.Abs(predicted.X - to[i].X) < 1e-6, $"点 {i} 的 X 误差过大");
            Assert.True(Math.Abs(predicted.Y - to[i].Y) < 1e-6, $"点 {i} 的 Y 误差过大");
        }
    }

    [Fact]
    public void 多于四点时应做最小二乘拟合()
    {
        (List<Point2> from, List<Point2> to) = MakePairs(30);

        Homography? h = Homography.Compute(from, to);
        Assert.NotNull(h);

        double maxError = 0;
        for (int i = 0; i < from.Count; i++)
        {
            Point2 predicted = h!.Apply(from[i]);
            maxError = Math.Max(maxError, Math.Abs(predicted.X - to[i].X));
            maxError = Math.Max(maxError, Math.Abs(predicted.Y - to[i].Y));
        }

        Assert.True(maxError < 1e-4, $"最大残差 {maxError:E3} px");
    }

    [Fact]
    public void 逆变换应把点还原()
    {
        (List<Point2> from, List<Point2> to) = MakePairs(8);

        Homography? h = Homography.Compute(from, to);
        Assert.NotNull(h);

        Homography? inverse = h!.Inverse();
        Assert.NotNull(inverse);

        for (int i = 0; i < to.Count; i++)
        {
            Point2 back = inverse!.Apply(to[i]);
            Assert.Equal(from[i].X, back.X, 4);
            Assert.Equal(from[i].Y, back.Y, 4);
        }
    }

    [Fact]
    public void 少于四点应返回空()
    {
        (List<Point2> from, List<Point2> to) = MakePairs(3);
        Assert.Null(Homography.Compute(from, to));
    }

    [Fact]
    public void 共线点应因退化而返回空()
    {
        var from = new List<Point2> { new(0, 0), new(10, 10), new(20, 20), new(30, 30) };
        var to = new List<Point2> { new(0, 0), new(10, 10), new(20, 20), new(30, 30) };

        Assert.Null(Homography.Compute(from, to));
    }
}
