using CameraControl.Application.Capture;
using CameraControl.Application.Tests.Fakes;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Capture;
using CameraControl.Domain.Ports;
using Xunit;

namespace CameraControl.Application.Tests;

/// <summary>
/// 同步采集编排：P0 单相机垂直切片的契约测试。
/// </summary>
public sealed class CaptureGroupServiceTests
{
    private sealed record Harness(
        CaptureGroupService Service,
        CameraSessionRegistry Registry,
        FakeCameraProvider Provider,
        FakeReadinessGate Gate,
        FakeTriggerSourceFactory Triggers,
        FakeClock Clock);

    private static Harness Build()
    {
        var clock = new FakeClock();
        var provider = new FakeCameraProvider(clock);
        var registry = new CameraSessionRegistry(provider);
        var gate = new FakeReadinessGate();
        var triggers = new FakeTriggerSourceFactory();

        var service = new CaptureGroupService(registry, gate, triggers, clock);
        return new Harness(service, registry, provider, gate, triggers, clock);
    }

    [Fact]
    public async Task 相机未打开应报CAMERA_NOT_OPEN()
    {
        Harness h = Build();

        CameraOperationException ex = await Assert.ThrowsAsync<CameraOperationException>(() =>
            h.Service.CaptureAsync(
                CaptureGroupRequest.ForSingleCamera(new CameraId("camA")),
                CancellationToken.None));

        Assert.Equal(CaptureErrorCodes.CameraNotOpen, ex.ErrorCode);
    }

    [Fact]
    public async Task 单相机采集应返回一帧且为软件时间戳()
    {
        Harness h = Build();
        h.Provider.Register("camA");
        await h.Registry.OpenAsync(new CameraId("camA"), CancellationToken.None);

        CaptureGroupResult result = await h.Service.CaptureAsync(
            CaptureGroupRequest.ForSingleCamera(
                new CameraId("camA"),
                readiness: ReadinessCriteria.FixedDelayPlaceholder(TimeSpan.Zero)),
            CancellationToken.None);

        Assert.Single(result.Frames);
        Assert.Equal("camA", result.Frames[0].CameraId.Value);
        Assert.Equal(TimestampQuality.SoftwareApprox, result.TimestampQuality);
        Assert.Equal(TimeSpan.Zero, result.Skew);
        Assert.True(result.IsComplete);
        Assert.NotNull(result.Readiness);
    }

    [Fact]
    public async Task 就绪门未通过应报NOT_SETTLED且不采集()
    {
        Harness h = Build();
        FakeCameraSession session = h.Provider.Register("camA");
        await h.Registry.OpenAsync(new CameraId("camA"), CancellationToken.None);

        h.Gate.Ready = false;

        CameraOperationException ex = await Assert.ThrowsAsync<CameraOperationException>(() =>
            h.Service.CaptureAsync(
                CaptureGroupRequest.ForSingleCamera(
                    new CameraId("camA"),
                    readiness: ReadinessCriteria.FixedDelayPlaceholder(TimeSpan.Zero)),
                CancellationToken.None));

        Assert.Equal(CaptureErrorCodes.NotSettled, ex.ErrorCode);

        // 关键：未通过就绪门时不得触发采集
        Assert.DoesNotContain("capture", session.Calls);
        Assert.Equal(0, h.Triggers.Software.FireCount);
    }

    [Fact]
    public async Task 硬件触发未实现应报NOT_IMPLEMENTED()
    {
        Harness h = Build();
        h.Provider.Register("camA");
        await h.Registry.OpenAsync(new CameraId("camA"), CancellationToken.None);

        var request = new CaptureGroupRequest(
            CameraIds: [new CameraId("camA")],
            Trigger: TriggerSpec.Hardware("plc.ch1"));

        CameraOperationException ex = await Assert.ThrowsAsync<CameraOperationException>(() =>
            h.Service.CaptureAsync(request, CancellationToken.None));

        Assert.Equal(CaptureErrorCodes.ModeNotImplemented, ex.ErrorCode);
    }

    [Fact]
    public async Task 运动冻结模式未实现应报NOT_IMPLEMENTED()
    {
        Harness h = Build();
        h.Provider.Register("camA");
        await h.Registry.OpenAsync(new CameraId("camA"), CancellationToken.None);

        var request = new CaptureGroupRequest(
            CameraIds: [new CameraId("camA")],
            Trigger: TriggerSpec.Software(),
            Mode: CaptureMode.MotionFrozen);

        CameraOperationException ex = await Assert.ThrowsAsync<CameraOperationException>(() =>
            h.Service.CaptureAsync(request, CancellationToken.None));

        Assert.Equal(CaptureErrorCodes.ModeNotImplemented, ex.ErrorCode);
    }

    [Fact]
    public async Task 部分相机失败时其余帧仍然返回且记录缺失()
    {
        Harness h = Build();
        FakeCameraSession ok = h.Provider.Register("camA");
        FakeCameraSession bad = h.Provider.Register("camB");
        bad.FailCapture = true;

        await h.Registry.OpenAsync(new CameraId("camA"), CancellationToken.None);
        await h.Registry.OpenAsync(new CameraId("camB"), CancellationToken.None);

        var request = new CaptureGroupRequest(
            CameraIds: [new CameraId("camA"), new CameraId("camB")],
            Trigger: TriggerSpec.Software());

        CaptureGroupResult result = await h.Service.CaptureAsync(request, CancellationToken.None);

        Assert.Single(result.Frames);
        Assert.Equal("camA", result.Frames[0].CameraId.Value);
        Assert.False(result.IsComplete);
        Assert.Single(result.MissingCameras);
        Assert.Equal("camB", result.MissingCameras[0].Value);
        Assert.Single(ok.Calls.FindAll(c => c == "capture"));
    }

    [Fact]
    public async Task ArmFirst时应在触发前武装并在结束后解除武装()
    {
        Harness h = Build();
        FakeCameraSession session = h.Provider.Register("camA");
        await h.Registry.OpenAsync(new CameraId("camA"), CancellationToken.None);

        var request = new CaptureGroupRequest(
            CameraIds: [new CameraId("camA")],
            Trigger: TriggerSpec.Software(),
            ArmFirst: true);

        await h.Service.CaptureAsync(request, CancellationToken.None);

        // 顺序：arm → capture → disarm
        Assert.Contains("arm:Software", session.Calls);
        Assert.Contains("capture", session.Calls);
        Assert.Contains("disarm", session.Calls);
        Assert.True(session.Calls.IndexOf("arm:Software") < session.Calls.IndexOf("disarm"));
    }

    [Fact]
    public async Task 未指定相机应报INVALID_PARAMETER()
    {
        Harness h = Build();

        var request = new CaptureGroupRequest(
            CameraIds: [],
            Trigger: TriggerSpec.Software());

        CameraOperationException ex = await Assert.ThrowsAsync<CameraOperationException>(() =>
            h.Service.CaptureAsync(request, CancellationToken.None));

        Assert.Equal(CaptureErrorCodes.InvalidParameter, ex.ErrorCode);
    }
}
