using CameraControl.Application.Capture;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Capture;
using CameraControl.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CameraControl.Application.Tests;

/// <summary>
/// 相机会话注册表的释放语义。
///
/// 回归背景：注册表只实现 <see cref="IAsyncDisposable"/> 时，
/// DI 容器的**同步** <c>Dispose()</c> 会抛
/// <c>InvalidOperationException: ... type only implements IAsyncDisposable</c>，
/// 表现为"每次关闭程序都报崩溃"。
/// WPF 的 <c>OnExit</c> 只能同步收尾，所以注册表必须两种方式都能释放。
/// </summary>
public sealed class CameraSessionRegistryDisposeTests
{
    private static readonly CameraId FakeId = new("USB\\FAKE");

    private static CameraDescriptor Descriptor => new(
        FakeId,
        "假相机",
        "Test",
        "Fake",
        null,
        CameraKind.Uvc);

    private sealed class FakeSession : ICameraSession
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public Task Disposed => _disposed.Task;

        public CameraId Id => FakeId;

        public CameraDescriptor Descriptor => CameraSessionRegistryDisposeTests.Descriptor;

        public bool IsOpen { get; private set; } = true;

        public Task SetParameterAsync(string name, string value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string?> GetParameterAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task ArmAsync(TriggerSpec trigger, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisarmAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task TriggerSoftwareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<Frame> CaptureSingleAsync(CaptureOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsOpen = false;
            _disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProvider(FakeSession session) : ICameraProvider
    {
        public Task<IReadOnlyList<CameraDescriptor>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CameraDescriptor>>([]);

        public Task<ICameraSession> OpenAsync(CameraId id, CancellationToken cancellationToken) =>
            Task.FromResult<ICameraSession>(session);
    }

    [Fact]
    public void 同步释放不应抛异常()
    {
        var registry = new CameraSessionRegistry(new FakeProvider(new FakeSession()));

        registry.Dispose();
    }

    [Fact]
    public async Task 同步释放应真的把会话释放掉()
    {
        var session = new FakeSession();
        var registry = new CameraSessionRegistry(new FakeProvider(session));

        await registry.OpenAsync(FakeId, CancellationToken.None);
        registry.Dispose();

        // 不阻塞等待，但释放"已发起"，应很快完成
        Task completed = await Task.WhenAny(session.Disposed, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(session.Disposed, completed);
        Assert.Equal(1, session.DisposeCount);
        Assert.Empty(registry.OpenSessions);
    }

    [Fact]
    public async Task 重复释放不应重复关闭()
    {
        var session = new FakeSession();
        var registry = new CameraSessionRegistry(new FakeProvider(session));

        await registry.OpenAsync(FakeId, CancellationToken.None);

        registry.Dispose();
        registry.Dispose();

        await session.Disposed;

        Assert.Equal(1, session.DisposeCount);
    }

    /// <summary>精确复现线上问题：DI 容器同步释放时不得抛异常。</summary>
    [Fact]
    public void DI容器同步释放不应抛异常()
    {
        var session = new FakeSession();
        var services = new ServiceCollection();

        services.AddSingleton<ICameraProvider>(new FakeProvider(session));
        services.AddSingleton<CameraSessionRegistry>();
        services.AddSingleton<ICameraSessionRegistry>(sp => sp.GetRequiredService<CameraSessionRegistry>());

        using ServiceProvider provider = services.BuildServiceProvider();

        // 触发实例化，让容器真的持有该服务
        _ = provider.GetRequiredService<ICameraSessionRegistry>();

        // 这一步就是原先抛 InvalidOperationException 的地方
        provider.Dispose();
    }

    [Fact]
    public async Task 异步释放仍然完整()
    {
        var session = new FakeSession();
        var registry = new CameraSessionRegistry(new FakeProvider(session));

        await registry.OpenAsync(FakeId, CancellationToken.None);
        await registry.DisposeAsync();

        Assert.Equal(1, session.DisposeCount);
        Assert.Empty(registry.OpenSessions);
    }
}
