using CameraControl.Application.Auditing;
using CameraControl.Application.Capabilities;
using CameraControl.Application.Commands;
using CameraControl.Application.Diagnostics;
using CameraControl.Application.Tests.Fakes;
using CameraControl.Domain.Calibration;
using Xunit;

namespace CameraControl.Application.Tests;

/// <summary>
/// 标定能力：求解 → 存储 → 查询/删除，全部走命令总线。
///
/// 用内存假存储，不需要落盘；求解器用真实的 <see cref="ZhangCalibrator"/>。
/// </summary>
public sealed class CalibrationCapabilityTests
{
    private sealed class MemoryCalibrationStore : ICalibrationStore
    {
        private readonly Dictionary<string, CalibrationRecord> _records = new(StringComparer.OrdinalIgnoreCase);

        public Task<CalibrationRecord?> GetAsync(string cameraId, CancellationToken cancellationToken) =>
            Task.FromResult(_records.GetValueOrDefault(cameraId));

        public Task SaveAsync(CalibrationRecord record, CancellationToken cancellationToken)
        {
            _records[record.CameraId] = record;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CalibrationRecord>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CalibrationRecord>>([.. _records.Values]);

        public Task<bool> RemoveAsync(string cameraId, CancellationToken cancellationToken) =>
            Task.FromResult(_records.Remove(cameraId));
    }

    private static (CommandBus Bus, MemoryCalibrationStore Store) Build()
    {
        var calibrator = new ZhangCalibrator();
        var store = new MemoryCalibrationStore();
        var clock = new FakeClock();

        var registry = new CapabilityRegistry()
            .Register(new CalibrationSolveCapability(calibrator, store, clock))
            .Register(new CalibrationGetCapability(store))
            .Register(new CalibrationListCapability(store))
            .Register(new CalibrationRemoveCapability(store));

        var bus = new CommandBus(
            registry,
            new DefaultRiskPolicy(),
            new InMemoryAuditLog(),
            clock,
            new InMemoryMetrics(),
            new FakeAppLogger());

        return (bus, store);
    }

    // ── 造合成角点（已知内参投影）────────────────────────────

    private const double TrueFx = 900;
    private const double TrueFy = 902;
    private const double TrueCx = 320;
    private const double TrueCy = 240;
    private const int Width = 640;
    private const int Height = 480;
    private const int Cols = 9;
    private const int Rows = 6;
    private const double SquareMm = 20;

    private static List<IReadOnlyList<Point2>> SyntheticViews()
    {
        (double rx, double ry, double rz, double tx, double ty, double tz)[] poses =
        [
            (-25, 8, 3, -30, -20, 300),
            (18, -22, 12, 30, -18, 320),
            (-12, 26, -8, -25, -25, 310),
            (28, 15, 18, 35, -20, 290),
            (-30, -18, -15, -40, -22, 330),
            (8, 32, 6, 20, -28, 305)
        ];

        var spec = new ChessboardSpec(Cols, Rows, SquareMm);
        var objectPoints = spec.ObjectPoints();
        var views = new List<IReadOnlyList<Point2>>(poses.Length);

        foreach ((double rx, double ry, double rz, double tx, double ty, double tz) in poses)
        {
            double a = rx * Math.PI / 180, b = ry * Math.PI / 180, c = rz * Math.PI / 180;

            double[,] rot =
            {
                { Math.Cos(c) * Math.Cos(b), (Math.Cos(c) * Math.Sin(b) * Math.Sin(a)) - (Math.Sin(c) * Math.Cos(a)), (Math.Cos(c) * Math.Sin(b) * Math.Cos(a)) + (Math.Sin(c) * Math.Sin(a)) },
                { Math.Sin(c) * Math.Cos(b), (Math.Sin(c) * Math.Sin(b) * Math.Sin(a)) + (Math.Cos(c) * Math.Cos(a)), (Math.Sin(c) * Math.Sin(b) * Math.Cos(a)) - (Math.Cos(c) * Math.Sin(a)) },
                { -Math.Sin(b), Math.Cos(b) * Math.Sin(a), Math.Cos(b) * Math.Cos(a) }
            };

            var points = new List<Point2>(objectPoints.Count);

            foreach (Point3 p in objectPoints)
            {
                double xc = (rot[0, 0] * p.X) + (rot[0, 1] * p.Y) + tx;
                double yc = (rot[1, 0] * p.X) + (rot[1, 1] * p.Y) + ty;
                double zc = (rot[2, 0] * p.X) + (rot[2, 1] * p.Y) + tz;

                points.Add(new Point2(((TrueFx * xc) / zc) + TrueCx, ((TrueFy * yc) / zc) + TrueCy));
            }

            views.Add(points);
        }

        return views;
    }

    // ── 测试 ────────────────────────────────────────────────

    [Fact]
    public async Task 求解应成功并保存记录()
    {
        (CommandBus bus, MemoryCalibrationStore store) = Build();

        var command = new CalibrationSolveCommand(
            "cam-1", Cols, Rows, SquareMm, Width, Height, SyntheticViews(), "单元测试");

        CommandResult result = await bus.ExecuteAsync(command, Actor.Human("tester"));

        Assert.True(result.Ok, result.Error?.Message);

        CalibrationRecord? saved = await store.GetAsync("cam-1", CancellationToken.None);
        Assert.NotNull(saved);
        Assert.True(Math.Abs(saved!.Intrinsics.Fx - TrueFx) / TrueFx < 0.02, $"fx={saved.Intrinsics.Fx}");
        Assert.True(Math.Abs(saved.Intrinsics.Fy - TrueFy) / TrueFy < 0.02, $"fy={saved.Intrinsics.Fy}");
        Assert.Equal(6, saved.ViewCount);
        Assert.Contains("未估计畸变", saved.DistortionNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未标定时查询应返回空()
    {
        (CommandBus bus, _) = Build();

        CommandResult result = await bus.ExecuteAsync(
            new CalibrationGetCommand("never-calibrated"), Actor.Human("tester"));

        Assert.True(result.Ok);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task 求解后查询应返回保存的参数()
    {
        (CommandBus bus, _) = Build();

        await bus.ExecuteAsync(
            new CalibrationSolveCommand("cam-1", Cols, Rows, SquareMm, Width, Height, SyntheticViews()),
            Actor.Human("tester"));

        CommandResult result = await bus.ExecuteAsync(
            new CalibrationGetCommand("cam-1"), Actor.Human("tester"));

        Assert.True(result.Ok);
        var dto = Assert.IsType<CalibrationDto>(result.Data);
        Assert.Equal("cam-1", dto.CameraId);
        Assert.True(dto.Fx > 0);
    }

    [Fact]
    public async Task 列表应包含已保存的记录()
    {
        (CommandBus bus, _) = Build();

        await bus.ExecuteAsync(
            new CalibrationSolveCommand("cam-a", Cols, Rows, SquareMm, Width, Height, SyntheticViews()),
            Actor.Human("tester"));

        CommandResult result = await bus.ExecuteAsync(new CalibrationListCommand(), Actor.Human("tester"));

        Assert.True(result.Ok);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public async Task 删除后查询应为空()
    {
        (CommandBus bus, _) = Build();

        await bus.ExecuteAsync(
            new CalibrationSolveCommand("cam-x", Cols, Rows, SquareMm, Width, Height, SyntheticViews()),
            Actor.Human("tester"));

        CommandResult removed = await bus.ExecuteAsync(
            new CalibrationRemoveCommand("cam-x"), Actor.Human("tester"));

        Assert.True(removed.Ok);

        CommandResult query = await bus.ExecuteAsync(
            new CalibrationGetCommand("cam-x"), Actor.Human("tester"));

        Assert.True(query.Ok);
        Assert.Null(query.Data);
    }

    [Fact]
    public async Task 点数不匹配应给出明确参数错误()
    {
        (CommandBus bus, _) = Build();

        IReadOnlyList<Point2> tooFew = new List<Point2> { new(1, 2), new(3, 4) };
        var badViews = new List<IReadOnlyList<Point2>> { tooFew };

        CommandResult result = await bus.ExecuteAsync(
            new CalibrationSolveCommand("cam", Cols, Rows, SquareMm, Width, Height, badViews),
            Actor.Human("tester"));

        Assert.False(result.Ok);
        Assert.Equal(CalibrationStoreErrorCodes.InvalidArguments, result.Error!.Code);
    }

    [Fact]
    public void 求解能力风险等级应为可逆而非物理()
    {
        (_, MemoryCalibrationStore store) = Build();
        _ = store;

        var capability = new CalibrationSolveCapability(
            new ZhangCalibrator(),
            new MemoryCalibrationStore(),
            new FakeClock());

        Assert.Equal(RiskLevel.Reversible, capability.Descriptor.Risk);
        Assert.False(capability.Descriptor.RequiresConfirmation);
    }
}
