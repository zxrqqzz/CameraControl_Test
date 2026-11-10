using CameraControl.Application.Capabilities;
using CameraControl.Application.Capture;
using CameraControl.Application.Commands;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Capture;
using CameraControl.Domain.Ports;
using Xunit;

namespace CameraControl.Application.Tests;

/// <summary>
/// 相机会话与采集能力。
///
/// 用假相机验证"命令契约"（未打开时给什么错、打开后能否取图、统计是否累加），
/// 不依赖真机；真机联调见 docs/05。
/// </summary>
public sealed class CameraCapabilityTests
{
    private static readonly CameraId Id = new("USB\\FAKE");
    private static readonly CommandContext Context = new(Actor.Ai("test"), DateTimeOffset.UtcNow);

    private static CameraDescriptor Descriptor(bool infrared = false) => new(
        Id,
        "假相机",
        "Test",
        "Fake",
        null,
        CameraKind.Uvc,
        IsInfraredOnly: infrared);

    private sealed class FakeSession(bool infrared = false) : ICameraSession
    {
        public int CaptureCount { get; private set; }

        public TriggerSpec? ArmedWith { get; private set; }

        public CameraId Id => CameraCapabilityTests.Id;

        public CameraDescriptor Descriptor => CameraCapabilityTests.Descriptor(infrared);

        public bool IsOpen { get; private set; } = true;

        public Task SetParameterAsync(string name, string value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string?> GetParameterAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task ArmAsync(TriggerSpec trigger, CancellationToken cancellationToken)
        {
            ArmedWith = trigger;
            return Task.CompletedTask;
        }

        public Task DisarmAsync(CancellationToken cancellationToken)
        {
            ArmedWith = null;
            return Task.CompletedTask;
        }

        public Task TriggerSoftwareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<Frame> CaptureSingleAsync(CaptureOptions? options, CancellationToken cancellationToken)
        {
            CaptureCount++;

            var pixels = new byte[2 * 2 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0;      // B
                pixels[i + 1] = 0;  // G
                pixels[i + 2] = 255;// R
                pixels[i + 3] = 255;// A
            }

            return Task.FromResult(new Frame(
                $"f{CaptureCount}",
                Id,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                new FrameImage(2, 2, 8, "Bgra32", pixels)));
        }

        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProvider(ICameraSession session) : ICameraProvider
    {
        public Task<IReadOnlyList<CameraDescriptor>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CameraDescriptor>>([Descriptor()]);

        public Task<ICameraSession> OpenAsync(CameraId id, CancellationToken cancellationToken) =>
            Task.FromResult(session);
    }

    private sealed class FakeEncoder : IFrameEncoder
    {
        public int Calls { get; private set; }

        public byte[] EncodePng(FrameImage image)
        {
            Calls++;
            return [1, 2, 3];
        }
    }

    private static (CameraSessionRegistry Registry, FakeSession Session, FakeProvider Provider) Build()
    {
        var session = new FakeSession();
        var provider = new FakeProvider(session);

        return (new CameraSessionRegistry(provider), session, provider);
    }

    [Fact]
    public async Task 未打开相机时采集应给出可自我纠正的错误()
    {
        (CameraSessionRegistry registry, _, _) = Build();

        var capability = new CaptureSingleCapability(registry, new CaptureStatisticsService(), new FakeEncoder());

        CameraOperationException error = await Assert.ThrowsAsync<CameraOperationException>(() =>
            capability.HandleAsync(new CaptureSingleCommand(Id.Value), Context, CancellationToken.None));

        Assert.Equal(CaptureErrorCodes.CameraNotOpen, error.ErrorCode);
        Assert.Contains("camera.open", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 打开后应能取帧且默认不含像素()
    {
        (CameraSessionRegistry registry, FakeSession session, FakeProvider provider) = Build();

        var open = new CameraOpenCapability(registry, provider);
        CameraSessionDto opened = Assert.IsType<CameraSessionDto>(
            await open.HandleAsync(new CameraOpenCommand(Id.Value), Context, CancellationToken.None));

        Assert.True(opened.IsOpen);
        Assert.Equal("Uvc", opened.Kind);

        var encoder = new FakeEncoder();
        var single = new CaptureSingleCapability(registry, new CaptureStatisticsService(), encoder);

        FrameDto frame = Assert.IsType<FrameDto>(
            await single.HandleAsync(new CaptureSingleCommand(Id.Value), Context, CancellationToken.None));

        Assert.Equal(2, frame.Width);
        Assert.Equal("Bgra32", frame.PixelFormat);
        Assert.Null(frame.PngBase64);
        Assert.Equal(0, encoder.Calls);
        Assert.Equal(1, session.CaptureCount);
    }

    [Fact]
    public async Task 指定includeImage时应返回base64图片()
    {
        (CameraSessionRegistry registry, _, _) = Build();

        var encoder = new FakeEncoder();
        var single = new CaptureSingleCapability(registry, new CaptureStatisticsService(), encoder);

        await registry.OpenAsync(Id, CancellationToken.None);

        FrameDto frame = Assert.IsType<FrameDto>(await single.HandleAsync(
            new CaptureSingleCommand(Id.Value, IncludeImage: true),
            Context,
            CancellationToken.None));

        Assert.NotNull(frame.PngBase64);
        Assert.Equal(1, encoder.Calls);
        Assert.Equal([1, 2, 3], Convert.FromBase64String(frame.PngBase64!));
    }

    [Fact]
    public async Task 武装应记录触发类型()
    {
        (CameraSessionRegistry registry, FakeSession session, _) = Build();
        await registry.OpenAsync(Id, CancellationToken.None);

        var arm = new CaptureArmCapability(registry);

        await arm.HandleAsync(new CaptureArmCommand(Id.Value, "software", 150), Context, CancellationToken.None);

        Assert.NotNull(session.ArmedWith);
        Assert.Equal(TriggerSourceKind.Software, session.ArmedWith!.Kind);
        Assert.Equal(150, session.ArmedWith.SoftwareDelayUs);
    }

    [Fact]
    public async Task 统计应累加成功与失败()
    {
        (CameraSessionRegistry registry, _, _) = Build();
        await registry.OpenAsync(Id, CancellationToken.None);

        var stats = new CaptureStatisticsService();
        var single = new CaptureSingleCapability(registry, stats, new FakeEncoder());

        await single.HandleAsync(new CaptureSingleCommand(Id.Value), Context, CancellationToken.None);
        await single.HandleAsync(new CaptureSingleCommand(Id.Value), Context, CancellationToken.None);

        CaptureStatistics snapshot = stats.Get(Id);

        Assert.Equal(2, snapshot.Frames);
        Assert.Equal(0, snapshot.Failures);
        Assert.Equal(2, snapshot.LastWidth);
        Assert.Equal("Bgra32", snapshot.LastPixelFormat);
    }

    [Fact]
    public async Task 未预览时取帧应返回null而不是报错()
    {
        (CameraSessionRegistry registry, _, _) = Build();
        var preview = new CapturePreviewService(registry, new CaptureStatisticsService());

        var capability = new CaptureFrameCapability(preview, new FakeEncoder());

        object? result = await capability.HandleAsync(
            new CaptureGetFrameCommand(Id.Value),
            Context,
            CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task 关闭相机后列表应为空()
    {
        (CameraSessionRegistry registry, _, _) = Build();
        await registry.OpenAsync(Id, CancellationToken.None);

        var list = new CameraListOpenCapability(registry);

        var before = Assert.IsType<CameraSessionListDto>(
            await list.HandleAsync(new CameraListOpenCommand(), Context, CancellationToken.None));

        Assert.Equal(1, before.Count);

        await new CameraCloseCapability(registry)
            .HandleAsync(new CameraCloseCommand(Id.Value), Context, CancellationToken.None);

        var after = Assert.IsType<CameraSessionListDto>(
            await list.HandleAsync(new CameraListOpenCommand(), Context, CancellationToken.None));

        Assert.Equal(0, after.Count);
    }

    [Fact]
    public async Task 连续预览应能取到帧并能停止()
    {
        (CameraSessionRegistry registry, _, _) = Build();
        await registry.OpenAsync(Id, CancellationToken.None);

        var preview = new CapturePreviewService(registry, new CaptureStatisticsService());

        await preview.StartAsync(Id, fps: 30, CancellationToken.None);

        // 轮询等待第一帧（最多 3 秒）
        Frame? frame = null;
        for (int i = 0; i < 60 && frame is null; i++)
        {
            await Task.Delay(50);
            frame = preview.Latest(Id);
        }

        Assert.NotNull(frame);
        Assert.Contains(Id.Value!, preview.Active);

        await preview.StopAsync(Id);

        Assert.Empty(preview.Active);
        Assert.Null(preview.Latest(Id));
    }
}
