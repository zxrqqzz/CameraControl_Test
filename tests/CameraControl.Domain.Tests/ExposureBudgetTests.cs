using CameraControl.Domain.Capture;
using Xunit;

namespace CameraControl.Domain.Tests;

/// <summary>
/// 曝光/运动模糊预算。这是 P2 运动冻结采集的质量闸门，
/// P0 就把公式与判定锁死，避免 P2 时各说各话。
/// </summary>
public sealed class ExposureBudgetTests
{
    [Fact]
    public void 曝光超过频闪时长应判为欠曝()
    {
        var profile = new ExposureProfile(ExposureUs: 500, StrobeDurationUs: 200);
        ExposureBudgetCheck check = ExposureBudget.Evaluate(profile, velocityMmPerSec: 0, pixelSizeMm: 0.005, maxBlurPx: 1);

        Assert.False(check.Ok);
        Assert.Equal(BudgetVerdict.ExposureExceedsStrobe, check.Verdict);
    }

    [Fact]
    public void 曝光在频闪加余量内应通过欠曝检查()
    {
        // 曝光 200，频闪 200 + 余量 50 → 允许
        var profile = new ExposureProfile(ExposureUs: 200, StrobeDurationUs: 200);
        ExposureBudgetCheck check = ExposureBudget.Evaluate(profile, 0, 0.005, maxBlurPx: 1);

        Assert.True(check.Ok);
        Assert.Equal(BudgetVerdict.Ok, check.Verdict);
    }

    [Fact]
    public void 运动模糊应等于速度乘曝光除以像素尺寸()
    {
        // 速度 1000mm/s，曝光 100µs → 位移 0.1mm；像素 0.005mm/px → 20px
        var profile = new ExposureProfile(ExposureUs: 100, StrobeDurationUs: 100);
        ExposureBudgetCheck check = ExposureBudget.Evaluate(
            profile, velocityMmPerSec: 1000, pixelSizeMm: 0.005, maxBlurPx: 25);

        Assert.True(check.Ok);
        Assert.Equal(20.0, check.EstimatedBlurPx, 6);
    }

    [Fact]
    public void 运动模糊超阈值应被拒绝()
    {
        var profile = new ExposureProfile(ExposureUs: 100, StrobeDurationUs: 100);
        ExposureBudgetCheck check = ExposureBudget.Evaluate(
            profile, velocityMmPerSec: 1000, pixelSizeMm: 0.005, maxBlurPx: 10);

        Assert.False(check.Ok);
        Assert.Equal(BudgetVerdict.MotionBlurExceeded, check.Verdict);
        Assert.True(check.EstimatedBlurPx > 10);
    }

    [Theory]
    [InlineData(0, 100, 0.005, 1.0)]
    [InlineData(100, 0, 0.005, 1.0)]
    [InlineData(100, 100, 0, 1.0)]
    [InlineData(100, 100, 0.005, 0)]
    public void 非法参数应被拒绝(int exposureUs, int strobeUs, double pixelSizeMm, double maxBlurPx)
    {
        var profile = new ExposureProfile(exposureUs, strobeUs);
        ExposureBudgetCheck check = ExposureBudget.Evaluate(profile, 100, pixelSizeMm, maxBlurPx);

        Assert.False(check.Ok);
        Assert.Equal(BudgetVerdict.InvalidParameter, check.Verdict);
    }

    [Fact]
    public void 静止时模糊为零()
    {
        var profile = new ExposureProfile(ExposureUs: 100, StrobeDurationUs: 100);
        ExposureBudgetCheck check = ExposureBudget.Evaluate(profile, velocityMmPerSec: 0, pixelSizeMm: 0.005, maxBlurPx: 0.1);

        Assert.True(check.Ok);
        Assert.Equal(0.0, check.EstimatedBlurPx, 9);
    }
}
