using CameraControl.Domain.Cameras;
using CameraControl.Domain.Capture;
using Xunit;

namespace CameraControl.Domain.Tests;

/// <summary>
/// 采集组组装逻辑。这组测试锁住"P0 单相机也走组语义"的契约，
/// 未来加相机若破坏契约会立刻失败。
/// </summary>
public sealed class CaptureGroupAssemblerTests
{
    private static Frame MakeFrame(string cameraId, DateTimeOffset ts, int width = 8, int height = 8)
    {
        var image = new FrameImage(width, height, width * 3, "Rgb24", new byte[width * height * 3]);
        return new Frame(
            FrameId: $"{cameraId}-{ts.ToUnixTimeMilliseconds()}",
            CameraId: new CameraId(cameraId),
            TimestampUtc: ts,
            OffsetFromGroup: TimeSpan.Zero,
            Image: image);
    }

    [Fact]
    public void 单相机应返回一帧且偏斜为零()
    {
        var t0 = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        Frame frame = MakeFrame("hik:SN001", t0);

        CaptureGroupResult result = CaptureGroupAssembler.Assemble(
            groupId: "g1",
            frames: [frame],
            requestedCameras: [new CameraId("hik:SN001")],
            quality: TimestampQuality.SoftwareApprox);

        Assert.Equal("g1", result.GroupId);
        Assert.Single(result.Frames);
        Assert.Equal(TimeSpan.Zero, result.Skew);
        Assert.Equal(TimeSpan.Zero, result.Frames[0].OffsetFromGroup);
        Assert.Equal(t0, result.TimestampUtc);
        Assert.True(result.IsComplete);
        Assert.Empty(result.MissingCameras);
        Assert.Equal(TimestampQuality.SoftwareApprox, result.TimestampQuality);
        Assert.True(result.IsSoftwareTimestamp);
    }

    [Fact]
    public void 多相机时组时间戳取最早帧且偏斜等于最大时间差()
    {
        var t0 = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddMilliseconds(3);

        CaptureGroupResult result = CaptureGroupAssembler.Assemble(
            groupId: "g2",
            frames: [MakeFrame("camB", t1), MakeFrame("camA", t0)],
            requestedCameras: [new CameraId("camA"), new CameraId("camB")],
            quality: TimestampQuality.HardwareStamped);

        Assert.Equal(2, result.Frames.Count);
        Assert.Equal(t0, result.TimestampUtc);
        Assert.Equal(TimeSpan.FromMilliseconds(3), result.Skew);

        // 结果按 CameraId 稳定排序，保证可复现
        Assert.Equal("camA", result.Frames[0].CameraId.Value);
        Assert.Equal(TimeSpan.Zero, result.Frames[0].OffsetFromGroup);
        Assert.Equal(TimeSpan.FromMilliseconds(3), result.Frames[1].OffsetFromGroup);
        Assert.False(result.IsSoftwareTimestamp);
    }

    [Fact]
    public void 同一相机重复帧只保留第一帧()
    {
        var t0 = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        Frame first = MakeFrame("camA", t0);
        Frame second = MakeFrame("camA", t0.AddMilliseconds(50));

        CaptureGroupResult result = CaptureGroupAssembler.Assemble(
            groupId: "g3",
            frames: [first, second],
            requestedCameras: [new CameraId("camA")],
            quality: TimestampQuality.SoftwareApprox);

        Assert.Single(result.Frames);
        Assert.Equal(first.FrameId, result.Frames[0].FrameId);
    }

    [Fact]
    public void 缺少相机时必须出现在缺失列表()
    {
        var t0 = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

        CaptureGroupResult result = CaptureGroupAssembler.Assemble(
            groupId: "g4",
            frames: [MakeFrame("camA", t0)],
            requestedCameras: [new CameraId("camA"), new CameraId("camB")],
            quality: TimestampQuality.HardwareStamped);

        Assert.False(result.IsComplete);
        Assert.Single(result.MissingCameras);
        Assert.Equal("camB", result.MissingCameras[0].Value);
    }

    [Fact]
    public void 没有任何帧时全部相机都算缺失()
    {
        CaptureGroupResult result = CaptureGroupAssembler.Assemble(
            groupId: "g5",
            frames: [],
            requestedCameras: [new CameraId("camA"), new CameraId("camB")],
            quality: TimestampQuality.SoftwareApprox);

        Assert.Empty(result.Frames);
        Assert.Equal(2, result.MissingCameras.Count);
        Assert.False(result.IsComplete);
        Assert.Equal(DateTimeOffset.MinValue, result.TimestampUtc);
    }

    [Fact]
    public void 应透传就绪门结果()
    {
        var t0 = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        var readiness = new ReadinessResult(true, 0.08, TimeSpan.FromMilliseconds(66), ReadinessSource.FixedDelay);

        CaptureGroupResult result = CaptureGroupAssembler.Assemble(
            groupId: "g6",
            frames: [MakeFrame("camA", t0)],
            requestedCameras: [new CameraId("camA")],
            quality: TimestampQuality.SoftwareApprox,
            readiness: readiness);

        Assert.NotNull(result.Readiness);
        Assert.True(result.Readiness!.Ready);
        Assert.Equal(0.08, result.Readiness.MeasuredAmplitude, 6);
    }
}
