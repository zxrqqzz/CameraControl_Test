using CameraControl.Application.Calibration;
using CameraControl.Application.Capabilities;
using CameraControl.Application.Comm;
using CameraControl.Domain.Calibration;
using CameraControl.Domain.Ports;
using Xunit;

namespace CameraControl.Application.Tests;

/// <summary>
/// 标定会话 / 通讯管理 / 设备别名 / 授权模式。
/// 全部不依赖真机。
/// </summary>
public sealed class PlatformServiceTests
{
    /// <summary>内存态设置存储（避免碰用户配置文件）。</summary>
    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public bool TryLoad<T>(string key, out T? value)
        {
            if (_values.TryGetValue(key, out object? raw) && raw is T typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }

        public void Save<T>(string key, T value) => _values[key] = value;
    }

    // ── 标定会话 ────────────────────────────────────────

    private sealed class StubCalibrator : ICameraCalibrator
    {
        public int Calls { get; private set; }

        public IntrinsicsSolveResult Solve(
            IReadOnlyList<CalibrationView> views,
            ChessboardSpec spec,
            int imageWidth,
            int imageHeight)
        {
            Calls++;

            return new IntrinsicsSolveResult(
                Ok: true,
                Intrinsics: new CameraIntrinsics(1000, 1000, 320, 240, imageWidth, imageHeight),
                HomographyRmsePx: 0.5,
                Error: null,
                Diagnostics: [],
                Distortion: new DistortionModel(K1: -0.1),
                ReprojectionRmsePx: 0.3);
        }
    }

    private static IReadOnlyList<Point2> Points(ChessboardSpec spec) =>
        [.. spec.ObjectPoints().Select(p => new Point2(p.X, p.Y))];

    [Fact]
    public void 标定会话应能配置并累计视角()
    {
        var session = new CalibrationSessionService(new StubCalibrator());

        session.Configure("cam1", 4, 3, 25);

        var spec = new ChessboardSpec(4, 3, 25);

        session.AddView("cam1", Points(spec));
        session.AddView("cam1", Points(spec));

        CalibrationSessionState state = session.GetState("cam1");

        Assert.Equal(2, state.ViewCount);
        Assert.Equal(4, state.GridCols);
        Assert.Equal(3, state.GridRows);
        Assert.Equal(25, state.SquareMm);
    }

    [Fact]
    public void 点数不符应明确报错()
    {
        var session = new CalibrationSessionService(new StubCalibrator());
        session.Configure("cam1", 4, 3, 25);

        CameraOperationException error = Assert.Throws<CameraOperationException>(() =>
            session.AddView("cam1", [new Point2(1, 1)]));

        Assert.Contains("不一致", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 视角不足时求解应返回失败而不是抛异常()
    {
        var session = new CalibrationSessionService(new StubCalibrator());
        session.Configure("cam1", 4, 3, 25);

        IntrinsicsSolveResult result = session.Solve("cam1", 640, 480);

        Assert.False(result.Ok);
        Assert.Equal(CalibrationErrorCodes.TooFewViews, result.Diagnostics![0]);
    }

    [Fact]
    public void 移除与清空应生效()
    {
        var session = new CalibrationSessionService(new StubCalibrator());
        session.Configure("cam1", 4, 3, 25);

        var spec = new ChessboardSpec(4, 3, 25);
        session.AddView("cam1", Points(spec));
        session.AddView("cam1", Points(spec));

        Assert.Equal(1, session.RemoveView("cam1", 0).ViewCount);
        Assert.Equal(0, session.Reset("cam1").ViewCount);
    }

    [Fact]
    public void 求解成功后应记录结果()
    {
        var calibrator = new StubCalibrator();
        var session = new CalibrationSessionService(calibrator);
        session.Configure("cam1", 4, 3, 25);

        var spec = new ChessboardSpec(4, 3, 25);
        for (int i = 0; i < 3; i++) session.AddView("cam1", Points(spec));

        IntrinsicsSolveResult result = session.Solve("cam1", 640, 480);

        Assert.True(result.Ok);
        Assert.Equal(1, calibrator.Calls);
        Assert.NotNull(session.GetState("cam1").LastResult);
    }

    // ── 通讯 ────────────────────────────────────────────

    private sealed class UnsupportedFactory : ICommChannelFactory
    {
        public ICommChannel? TryCreate(CommEndpoint endpoint) => null;

        public IReadOnlyList<CommKind> SupportedKinds => [];
    }

    [Fact]
    public void 未接入驱动时添加连接应失败关闭()
    {
        var manager = new CommManager(new UnsupportedFactory());

        CameraOperationException error = Assert.Throws<CameraOperationException>(() =>
            manager.Add(new CommEndpoint("plc1", CommKind.ModbusTcp, "192.168.0.10:502")));

        Assert.Equal(CommErrorCodes.NotConfigured, error.ErrorCode);
        Assert.Contains("未接入", error.Message, StringComparison.Ordinal);
        Assert.Empty(manager.List());
    }

    [Fact]
    public void 重复添加应报重复()
    {
        var manager = new CommManager(new FakeFactory());

        manager.Add(new CommEndpoint("plc1", CommKind.Tcp, "127.0.0.1:9000"));

        CameraOperationException error = Assert.Throws<CameraOperationException>(() =>
            manager.Add(new CommEndpoint("plc1", CommKind.Tcp, "127.0.0.1:9000")));

        Assert.Equal(CommErrorCodes.Duplicate, error.ErrorCode);
    }

    [Fact]
    public async Task 未打开时发送应报未打开()
    {
        var manager = new CommManager(new FakeFactory());
        manager.Add(new CommEndpoint("plc1", CommKind.Tcp, "127.0.0.1:9000"));

        CameraOperationException error = await Assert.ThrowsAsync<CameraOperationException>(() =>
            manager.SendHexAsync("plc1", "01 03", CancellationToken.None));

        Assert.Equal(CommErrorCodes.NotOpen, error.ErrorCode);
    }

    [Fact]
    public async Task 打开后应能发送并读到日志()
    {
        var factory = new FakeFactory();
        var manager = new CommManager(factory);
        manager.Add(new CommEndpoint("plc1", CommKind.Tcp, "127.0.0.1:9000"));

        await manager.OpenAsync("plc1", CancellationToken.None);
        await manager.SendHexAsync("plc1", "01-03-00", CancellationToken.None);

        Assert.True(manager.Find("plc1")!.IsOpen);
        Assert.NotEmpty(manager.GetLog("plc1", 10));
    }

    private sealed class FakeFactory : ICommChannelFactory
    {
        public ICommChannel? TryCreate(CommEndpoint endpoint) => new FakeChannel(endpoint);

        public IReadOnlyList<CommKind> SupportedKinds => [CommKind.Tcp];

        private sealed class FakeChannel(CommEndpoint endpoint) : ICommChannel
        {
            private readonly List<CommLogEntry> _log = [];

            public CommEndpoint Endpoint => endpoint;

            public bool IsOpen { get; private set; }

            public Task OpenAsync(CancellationToken cancellationToken)
            {
                IsOpen = true;
                _log.Add(new CommLogEntry(DateTimeOffset.UtcNow, "info", "opened"));
                return Task.CompletedTask;
            }

            public Task CloseAsync(CancellationToken cancellationToken)
            {
                IsOpen = false;
                return Task.CompletedTask;
            }

            public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
            {
                _log.Add(new CommLogEntry(DateTimeOffset.UtcNow, "out", Convert.ToHexString(payload.Span)));
                return Task.CompletedTask;
            }

            public IReadOnlyList<CommLogEntry> GetLog(int maxEntries) =>
                [.. _log.TakeLast(maxEntries).Reverse()];
        }
    }

    // ── 设备别名 ────────────────────────────────────────

    [Fact]
    public void 别名应能设置与清除()
    {
        var store = new DeviceAliasStore(new MemorySettings());

        store.SetAlias("cam1", "工位一号");
        Assert.Equal("工位一号", store.GetAlias("cam1"));

        store.SetAlias("cam1", null);
        Assert.Null(store.GetAlias("cam1"));
    }

    // ── 授权模式 ────────────────────────────────────────

    [Fact]
    public void 默认授权模式应为确认()
    {
        var auth = new CuaAuthorization();

        Assert.Equal(CuaAuthorizationMode.Confirm, auth.Mode);
        Assert.False(auth.StopRequested);
    }

    [Fact]
    public void 请求停止后标记应生效并可清除()
    {
        var auth = new CuaAuthorization();

        auth.RequestStop();
        Assert.True(auth.StopRequested);

        auth.ClearStop();
        Assert.False(auth.StopRequested);
    }
}
