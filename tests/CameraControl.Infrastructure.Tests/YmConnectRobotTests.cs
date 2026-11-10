using System.IO;
using CameraControl.Domain.Robotics;
using CameraControl.Infrastructure.Storage;
using CameraControl.Infrastructure.YmConnect;
using Xunit;

namespace CameraControl.Infrastructure.Tests;

/// <summary>
/// 安川机器人适配器（YMConnect）。
///
/// **这里只测不依赖真机的部分**：配置读写、未配置时的行为。
/// 真机联调（连接、读位姿、运动）需现场实测，见 docs/10。
/// </summary>
public sealed class YmConnectRobotTests : IDisposable
{
    private readonly string _root;

    public YmConnectRobotTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "robot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论
        }
    }

    private sealed class FixedClock : Domain.Ports.IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 9, 12, 0, 0, TimeSpan.FromHours(8));
    }

    private sealed class NullLogger : Application.Diagnostics.IAppLogger
    {
        public bool IsEnabled(Application.Diagnostics.LogLevel level) => false;

        public void Log(
            Application.Diagnostics.LogLevel level,
            string message,
            IReadOnlyDictionary<string, object?>? fields = null)
        {
        }
    }

    private RobotSettingsStore CreateStore() => new(new JsonSettingsStore(_root));

    [Fact]
    public void 未配置IP时IsConfigured应为false()
    {
        RobotSettingsStore store = CreateStore();

        Assert.False(store.Load().IsConfigured);
    }

    [Fact]
    public void 配置应能保存并读回()
    {
        RobotSettingsStore store = CreateStore();

        store.Save(new RobotSettings
        {
            Ip = "192.168.1.31",
            SpeedMmPerSec = 50,
            ReceiveTimeoutMs = 4000,
            SendRetries = 5
        });

        RobotSettings loaded = store.Load();

        Assert.Equal("192.168.1.31", loaded.Ip);
        Assert.Equal(50, loaded.SpeedMmPerSec);
        Assert.Equal(4000, loaded.ReceiveTimeoutMs);
        Assert.Equal(5, loaded.SendRetries);
        Assert.True(loaded.IsConfigured);
    }

    [Fact]
    public void 空白IP应视为未配置()
    {
        RobotSettingsStore store = CreateStore();
        store.Save(new RobotSettings { Ip = "   " });

        Assert.False(store.Load().IsConfigured);
    }

    [Fact]
    public async Task 未配置IP时读取状态应返回未连接而不是抛异常()
    {
        var robot = new YmConnectRobot(CreateStore(), new FixedClock(), new NullLogger());

        RobotState state = await robot.GetStateAsync(CancellationToken.None);

        Assert.Equal(RobotConnectionState.Disconnected, state.State);
        Assert.Contains("未配置", state.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未配置IP时运动应被拒绝()
    {
        var robot = new YmConnectRobot(CreateStore(), new FixedClock(), new NullLogger());

        RobotMotionResult result = await robot.MoveToAsync(
            new RobotPose(100, 200, 300, 0, 90, 0),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(RobotErrorCodes.NotConnected, result.ErrorCode);
    }

    [Fact]
    public async Task 未连接时急停应安全返回()
    {
        var robot = new YmConnectRobot(CreateStore(), new FixedClock(), new NullLogger());

        // 不应抛异常：急停路径不该级联失败
        await robot.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// 原生库可加载性检查。
    ///
    /// 用 127.0.0.1 触发一次真实连接尝试：预期**连接失败**（没有控制器），
    /// 但绝不能是 DllNotFoundException/BadImageFormatException ——
    /// 那说明原生 YMConnect.dll 没被正确部署或架构不匹配。
    /// </summary>
    [Fact]
    public async Task 原生库应能被加载而不是DllNotFound()
    {
        RobotSettingsStore store = CreateStore();
        store.Save(new RobotSettings { Ip = "127.0.0.1", ReceiveTimeoutMs = 500, SendRetries = 1 });

        var robot = new YmConnectRobot(store, new FixedClock(), new NullLogger());

        RobotState state = await robot.GetStateAsync(CancellationToken.None);

        string message = state.Message ?? string.Empty;

        Assert.DoesNotContain("DllNotFound", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BadImageFormat", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unable to load", message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void 适配器名称应为YMConnect()
    {
        var robot = new YmConnectRobot(CreateStore(), new FixedClock(), new NullLogger());

        Assert.Equal("YMConnect", robot.AdapterName);
    }
}
