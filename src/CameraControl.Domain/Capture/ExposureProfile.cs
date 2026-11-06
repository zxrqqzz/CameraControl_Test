namespace CameraControl.Domain.Capture;

/// <summary>
/// 曝光/频闪配置。仅 <see cref="CaptureMode.MotionFrozen"/> 模式需要。
/// 常亮光下极短曝光会欠曝，所以频闪是刚需。
/// </summary>
/// <param name="ExposureUs">相机曝光时间（µs）。</param>
/// <param name="StrobeDurationUs">频闪时长（µs），通常略大于曝光。</param>
/// <param name="StrobeLeadUs">频闪相对曝光脉冲的提前量（µs），用于补偿光源响应延迟。</param>
public sealed record ExposureProfile(
    int ExposureUs,
    int StrobeDurationUs,
    int StrobeLeadUs = 0);

/// <summary>预算校验结果。</summary>
public enum BudgetVerdict
{
    /// <summary>通过。</summary>
    Ok = 0,

    /// <summary>曝光时长超过频闪时长，会欠曝。</summary>
    ExposureExceedsStrobe = 1,

    /// <summary>运动模糊超出阈值。</summary>
    MotionBlurExceeded = 2,

    /// <summary>参数本身非法（非正数等）。</summary>
    InvalidParameter = 3
}

/// <summary>预算校验详细结果。</summary>
/// <param name="Verdict">结论。</param>
/// <param name="EstimatedBlurPx">估算的运动模糊量（像素）。</param>
/// <param name="Message">可读说明。</param>
public sealed record ExposureBudgetCheck(
    BudgetVerdict Verdict,
    double EstimatedBlurPx,
    string Message)
{
    public bool Ok => Verdict == BudgetVerdict.Ok;
}

/// <summary>
/// 曝光/运动模糊预算校验（纯领域逻辑，无外部依赖，可直接单元测试）。
///
/// 两条硬约束：
/// 1. 曝光 ≤ 频闪时长 + 余量，否则欠曝；
/// 2. 运动模糊量 = 速度 × 曝光 ÷ 像素尺寸 ≤ 阈值。
/// </summary>
public static class ExposureBudget
{
    /// <summary>频闪相对曝光的允许余量（µs）。</summary>
    public const int StrobeMarginUs = 50;

    public static ExposureBudgetCheck Evaluate(
        ExposureProfile profile,
        double velocityMmPerSec,
        double pixelSizeMm,
        double maxBlurPx)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.ExposureUs <= 0 || profile.StrobeDurationUs <= 0)
            return new(BudgetVerdict.InvalidParameter, double.NaN, "曝光与频闪时长必须为正数。");

        if (pixelSizeMm <= 0)
            return new(BudgetVerdict.InvalidParameter, double.NaN, "像素尺寸必须为正数。");

        if (maxBlurPx <= 0)
            return new(BudgetVerdict.InvalidParameter, double.NaN, "模糊阈值必须为正数。");

        if (velocityMmPerSec < 0)
            return new(BudgetVerdict.InvalidParameter, double.NaN, "速度不能为负。");

        if (profile.ExposureUs > profile.StrobeDurationUs + StrobeMarginUs)
        {
            return new(
                BudgetVerdict.ExposureExceedsStrobe,
                double.NaN,
                $"曝光 {profile.ExposureUs}µs 超过频闪 {profile.StrobeDurationUs}µs + 余量 {StrobeMarginUs}µs，会欠曝。");
        }

        // 位移(mm) = 速度(mm/s) × 曝光(s)；模糊(px) = 位移 / 像素尺寸(mm/px)
        double blurPx = velocityMmPerSec * (profile.ExposureUs / 1_000_000.0) / pixelSizeMm;

        return blurPx > maxBlurPx
            ? new(
                BudgetVerdict.MotionBlurExceeded,
                blurPx,
                $"预计运动模糊 {blurPx:F2}px 超过阈值 {maxBlurPx:F2}px，请缩短曝光或降低速度。")
            : new(BudgetVerdict.Ok, blurPx, $"预计运动模糊 {blurPx:F2}px，在阈值 {maxBlurPx:F2}px 内。");
    }
}
