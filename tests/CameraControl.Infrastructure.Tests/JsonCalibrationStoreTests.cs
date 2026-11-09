using System.IO;
using CameraControl.Domain.Calibration;
using CameraControl.Infrastructure.Storage;
using Xunit;

namespace CameraControl.Infrastructure.Tests;

/// <summary>
/// 标定结果的 JSON 落盘存储。
///
/// 覆盖真实文件读写（临时目录），重点是：
/// - 相机 id 含反斜杠与 &amp;，文件名必须做安全映射；
/// - 写入先临时文件再替换，不能留半截；
/// - 损坏文件不能被当成"已标定"。
/// </summary>
public sealed class JsonCalibrationStoreTests : IDisposable
{
    private readonly string _root;

    public JsonCalibrationStoreTests()
    {
        // 不用 %TEMP%：受限环境里它可能不可写。放到程序目录下的独立子目录。
        _root = Path.Combine(
            AppContext.BaseDirectory,
            "store-tests",
            Guid.NewGuid().ToString("N"));

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
            // 清理失败不影响测试结论
        }
    }

    private static CalibrationRecord MakeRecord(string cameraId) => new(
        CameraId: cameraId,
        Intrinsics: new CameraIntrinsics(679.5, 682.2, 315.0, 266.2, 640, 480),
        ViewCount: 11,
        HomographyRmsePx: 1.85,
        SolvedAtUtc: new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(8)),
        GridDescription: "12x13@30mm",
        Note: "单元测试");

    [Fact]
    public async Task 保存后应能读回()
    {
        var store = new JsonCalibrationStore(_root);
        CalibrationRecord record = MakeRecord("cam-1");

        await store.SaveAsync(record, CancellationToken.None);
        CalibrationRecord? loaded = await store.GetAsync("cam-1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(record.CameraId, loaded!.CameraId);
        Assert.Equal(record.Intrinsics.Fx, loaded.Intrinsics.Fx, 6);
        Assert.Equal(record.Intrinsics.Cy, loaded.Intrinsics.Cy, 6);
        Assert.Equal(record.ViewCount, loaded.ViewCount);
        Assert.Contains("未估计畸变", loaded.DistortionNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 含反斜杠和与号的相机id应能安全落盘()
    {
        var store = new JsonCalibrationStore(_root);

        // 真实 UVC 相机 id 长这样：含反斜杠与 &
        const string cameraId = @"USB\VID_30C9&PID_00EC&MI_00\6&3a5c8ec3&0&0000";

        await store.SaveAsync(MakeRecord(cameraId), CancellationToken.None);

        CalibrationRecord? loaded = await store.GetAsync(cameraId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(cameraId, loaded!.CameraId);

        // 文件名不能包含原始 id（否则 Windows 直接报错）
        string[] files = Directory.GetFiles(Path.Combine(_root, "calibration"), "*.json");
        Assert.Single(files);
        Assert.DoesNotContain('\\', Path.GetFileName(files[0]));
        Assert.DoesNotContain('&', Path.GetFileName(files[0]));
    }

    [Fact]
    public async Task 未标定时应返回空()
    {
        var store = new JsonCalibrationStore(_root);

        Assert.Null(await store.GetAsync("never", CancellationToken.None));
    }

    [Fact]
    public async Task 列表应包含全部记录()
    {
        var store = new JsonCalibrationStore(_root);

        await store.SaveAsync(MakeRecord("cam-b"), CancellationToken.None);
        await store.SaveAsync(MakeRecord("cam-a"), CancellationToken.None);

        IReadOnlyList<CalibrationRecord> all = await store.ListAsync(CancellationToken.None);

        Assert.Equal(2, all.Count);
        Assert.Equal("cam-a", all[0].CameraId);   // 按 id 排序
        Assert.Equal("cam-b", all[1].CameraId);
    }

    [Fact]
    public async Task 重复保存应覆盖而不是追加()
    {
        var store = new JsonCalibrationStore(_root);

        await store.SaveAsync(MakeRecord("cam-1"), CancellationToken.None);

        CalibrationRecord updated = MakeRecord("cam-1") with
        {
            Intrinsics = new CameraIntrinsics(700, 701, 320, 240, 640, 480)
        };

        await store.SaveAsync(updated, CancellationToken.None);

        Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(700, (await store.GetAsync("cam-1", CancellationToken.None))!.Intrinsics.Fx, 6);
    }

    [Fact]
    public async Task 删除应生效且重复删除返回false()
    {
        var store = new JsonCalibrationStore(_root);
        await store.SaveAsync(MakeRecord("cam-1"), CancellationToken.None);

        Assert.True(await store.RemoveAsync("cam-1", CancellationToken.None));
        Assert.False(await store.RemoveAsync("cam-1", CancellationToken.None));
        Assert.Null(await store.GetAsync("cam-1", CancellationToken.None));
    }

    [Fact]
    public async Task 损坏文件应视为未标定而不是抛异常()
    {
        var store = new JsonCalibrationStore(_root);

        await store.SaveAsync(MakeRecord("cam-1"), CancellationToken.None);

        string file = Directory.GetFiles(Path.Combine(_root, "calibration"), "*.json")[0];
        await File.WriteAllTextAsync(file, "{ 这不是合法 JSON", CancellationToken.None);

        Assert.Null(await store.GetAsync("cam-1", CancellationToken.None));
    }
}
