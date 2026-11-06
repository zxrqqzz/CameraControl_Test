using CameraControl.Domain.Cameras;
using CameraControl.Domain.Capture;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Tests.Fakes;

/// <summary>可控的假相机会话。所有调用都会被记录，便于断言编排顺序。</summary>
public sealed class FakeCameraSession : ICameraSession
{
    private readonly IClock _clock;

    public FakeCameraSession(CameraDescriptor descriptor, IClock clock)
    {
        Descriptor = descriptor;
        Id = descriptor.Id;
        _clock = clock;
    }

    public CameraId Id { get; }

    public CameraDescriptor Descriptor { get; }

    public bool IsOpen { get; set; } = true;

    public List<string> Calls { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>为 true 时 CaptureSingleAsync 直接抛错（模拟硬件故障）。</summary>
    public bool FailCapture { get; set; }

    public Task ArmAsync(TriggerSpec trigger, CancellationToken cancellationToken)
    {
        Calls.Add($"arm:{trigger.Kind}");
        return Task.CompletedTask;
    }

    public Task DisarmAsync(CancellationToken cancellationToken)
    {
        Calls.Add("disarm");
        return Task.CompletedTask;
    }

    public Task TriggerSoftwareAsync(CancellationToken cancellationToken)
    {
        Calls.Add("trigger");
        return Task.CompletedTask;
    }

    public Task<Frame> CaptureSingleAsync(CaptureOptions? options, CancellationToken cancellationToken)
    {
        Calls.Add("capture");

        if (FailCapture)
        {
            throw new CameraOperationException("FAKE_CAPTURE_FAILED", "模拟采集失败。");
        }

        var image = new FrameImage(4, 4, 12, "Rgb24", new byte[4 * 4 * 3]);
        return Task.FromResult(new Frame(
            FrameId: Guid.NewGuid().ToString("N"),
            CameraId: Id,
            TimestampUtc: _clock.UtcNow,
            OffsetFromGroup: TimeSpan.Zero,
            Image: image));
    }

    public Task SetParameterAsync(string name, string value, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<string?> GetParameterAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        IsOpen = false;
        return ValueTask.CompletedTask;
    }
}

/// <summary>假相机提供者。</summary>
public sealed class FakeCameraProvider : ICameraProvider
{
    private readonly IClock _clock;
    private readonly List<CameraDescriptor> _descriptors = [];

    public FakeCameraProvider(IClock clock)
    {
        _clock = clock;
    }

    public FakeCameraSession Add(string id, CameraKind kind = CameraKind.HikGigE)
    {
        var descriptor = new CameraDescriptor(
            Id: new CameraId(id),
            DisplayName: id,
            Vendor: "FakeVendor",
            Model: "FakeModel",
            SerialNumber: id,
            Kind: kind);

        _descriptors.Add(descriptor);
        return new FakeCameraSession(descriptor, _clock);
    }

    /// <summary>已创建的会话，按 id 索引。</summary>
    public Dictionary<string, FakeCameraSession> Sessions { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<CameraDescriptor>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CameraDescriptor>>([.. _descriptors]);

    public Task<ICameraSession> OpenAsync(CameraId id, CancellationToken cancellationToken)
    {
        if (!Sessions.TryGetValue(id.Value, out FakeCameraSession? session))
            throw new CameraNotFoundException(id.Value);

        session.IsOpen = true;
        return Task.FromResult<ICameraSession>(session);
    }

    /// <summary>注册一台相机并返回其会话。</summary>
    public FakeCameraSession Register(string id, CameraKind kind = CameraKind.HikGigE)
    {
        FakeCameraSession session = Add(id, kind);
        Sessions[id] = session;
        return session;
    }
}

/// <summary>假触发源：只记录是否被触发。</summary>
public sealed class FakeTriggerSource : ITriggerSource
{
    public FakeTriggerSource(TriggerSourceKind kind) => Kind = kind;

    public TriggerSourceKind Kind { get; }

    public int FireCount { get; private set; }

    public Task FireAsync(
        TriggerSpec spec,
        IReadOnlyList<ICameraSession> targets,
        CancellationToken cancellationToken)
    {
        FireCount++;
        return Task.CompletedTask;
    }
}

/// <summary>触发源工厂：只提供软件触发（对齐 P0 现状）。</summary>
public sealed class FakeTriggerSourceFactory : Application.Capture.ITriggerSourceFactory
{
    public FakeTriggerSource Software { get; } = new(TriggerSourceKind.Software);

    public ITriggerSource? TryGet(TriggerSourceKind kind) =>
        kind == TriggerSourceKind.Software ? Software : null;
}

/// <summary>假就绪门：可控返回。</summary>
public sealed class FakeReadinessGate : IReadinessGate
{
    public bool Ready { get; set; } = true;

    public int Calls { get; private set; }

    public Task<ReadinessResult> WaitAsync(ReadinessCriteria criteria, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Ready
            ? new ReadinessResult(true, 0.05, TimeSpan.Zero, criteria.Source)
            : ReadinessResult.NotReady(criteria.Timeout, criteria.Source));
    }
}

/// <summary>可控时钟。</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
