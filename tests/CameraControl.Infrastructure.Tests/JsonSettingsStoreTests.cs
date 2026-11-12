using System.IO;
using CameraControl.Infrastructure;
using CameraControl.Infrastructure.Storage;
using Xunit;

namespace CameraControl.Infrastructure.Tests;

/// <summary>
/// 设置落盘的健壮性。
///
/// 背景：用户新增配置时遇到
/// <c>Access to the path '...\llm.json.tmp' is denied</c>。
/// 目录权限正常、也没有残留文件——属于**瞬时占用**（杀软/索引器扫描新建的 .tmp）。
/// 原实现一次失败就直接抛，用户会以为"配置坏了"。
/// </summary>
public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _root;

    public JsonSettingsStoreTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "settings-tests", Guid.NewGuid().ToString("N"));
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

    private sealed record Payload(string Name, int Value);

    [Fact]
    public void 目录不存在时应自动创建并写入()
    {
        var store = new JsonSettingsStore(_root);

        store.Save("demo", new Payload("a", 1));

        Assert.True(File.Exists(Path.Combine(_root, "demo.json")));
        Assert.True(store.TryLoad<Payload>("demo", out Payload? loaded));
        Assert.Equal("a", loaded!.Name);
    }

    [Fact]
    public void 应覆盖已有文件()
    {
        var store = new JsonSettingsStore(_root);

        store.Save("demo", new Payload("first", 1));
        store.Save("demo", new Payload("second", 2));

        Assert.True(store.TryLoad<Payload>("demo", out Payload? loaded));
        Assert.Equal("second", loaded!.Name);
        Assert.Equal(2, loaded.Value);
    }

    [Fact]
    public void 残留的临时文件不应阻塞写入()
    {
        Directory.CreateDirectory(_root);

        string temp = Path.Combine(_root, "demo.json.tmp");
        File.WriteAllText(temp, "半截内容");

        var store = new JsonSettingsStore(_root);

        store.Save("demo", new Payload("ok", 3));

        Assert.True(store.TryLoad<Payload>("demo", out Payload? loaded));
        Assert.Equal("ok", loaded!.Name);

        // 连续写两次也必须稳定（不依赖"清掉旧 .tmp"这一实现细节）
        store.Save("demo", new Payload("again", 4));

        Assert.True(store.TryLoad<Payload>("demo", out Payload? second));
        Assert.Equal("again", second!.Name);
    }

    [Fact]
    public void 损坏的文件应返回false而不是抛异常()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "demo.json"), "{ 这不是 JSON");

        var store = new JsonSettingsStore(_root);

        Assert.False(store.TryLoad<Payload>("demo", out _));
    }

    [Fact]
    public void 反复失败应抛出可诊断的异常()
    {
        // 用一个"文件"当目录：CreateDirectory/写入必然失败
        string blocker = Path.Combine(AppContext.BaseDirectory, $"blocker-{Guid.NewGuid():N}.txt");
        File.WriteAllText(blocker, "x");

        try
        {
            var store = new JsonSettingsStore(Path.Combine(blocker, "sub"));

            SettingsPersistenceException error = Assert.Throws<SettingsPersistenceException>(() =>
                store.Save("demo", new Payload("a", 1)));

            Assert.Contains("demo.json", error.Path, StringComparison.Ordinal);
            Assert.True(error.Attempts >= 2);
            Assert.Contains("配置写入失败", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
