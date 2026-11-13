using CameraControl.Application.Cua.Context;
using CameraControl.Infrastructure.Context;
using Xunit;

namespace CameraControl.Infrastructure.Tests;

/// <summary>
/// 上下文开关（Mods/context/mod.json）。
///
/// 语义：**文件不存在就禁用**——删掉文件夹即彻底卸载，
/// 而不是"悄悄用默认值继续跑"。损坏也只禁用并给出原因，不猜。
/// </summary>
public sealed class ContextModSettingsTests : IDisposable
{
    private readonly string _mods;

    public ContextModSettingsTests()
    {
        // 用测试输出目录（系统 TEMP 在本机存在外部写入拦截）
        _mods = Path.Combine(AppContext.BaseDirectory, "ctx-mods", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_mods);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_mods)) Directory.Delete(_mods, recursive: true);
        }
        catch
        {
            // 忽略
        }
    }

    private void WriteMod(string json)
    {
        string directory = Path.Combine(_mods, "context");
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "mod.json"), json);
    }

    [Fact]
    public void 文件缺失应禁用并说明原因()
    {
        ContextModSettings settings = ContextModSettings.Load(_mods);

        Assert.False(settings.Enabled);
        Assert.NotNull(settings.DisabledReason);
        Assert.Contains("未找到", settings.DisabledReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void 开启时应生效并带上阈值()
    {
        WriteMod("""{ "enabled": true, "maxEstimatedTokens": 5000, "maxEntryChars": 100 }""");

        ContextModSettings settings = ContextModSettings.Load(_mods);

        Assert.True(settings.Enabled);
        Assert.Equal(5000, settings.MaxEstimatedTokens);
        Assert.Equal(100, settings.MaxEntryChars);

        ContextStoreOptions options = settings.ToStoreOptions();

        Assert.True(options.Enabled);
        Assert.Equal(5000, options.MaxEstimatedTokens);
        Assert.Equal(100, options.MaxEntryChars);
    }

    [Fact]
    public void 显式关闭时应禁用()
    {
        WriteMod("""{ "enabled": false }""");

        Assert.False(ContextModSettings.Load(_mods).Enabled);
    }

    [Fact]
    public void 文件损坏应禁用而不是猜默认值()
    {
        WriteMod("{ 这不是 json");

        ContextModSettings settings = ContextModSettings.Load(_mods);

        Assert.False(settings.Enabled);
        Assert.Contains("解析失败", settings.DisabledReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void 阈值非法时应回落到默认值()
    {
        WriteMod("""{ "enabled": true, "maxEstimatedTokens": 0, "maxEntryChars": -5 }""");

        ContextModSettings settings = ContextModSettings.Load(_mods);

        Assert.True(settings.Enabled);
        Assert.Equal(1_000_000, settings.MaxEstimatedTokens);
        Assert.Equal(8_000, settings.MaxEntryChars);
    }

    [Fact]
    public void 未知档位名应回落到完整注入()
    {
        WriteMod("""{ "enabled": true, "defaultTier": "不存在的档位" }""");

        Assert.Equal(ContextTier.InjectFull, ContextModSettings.Load(_mods).DefaultTier);
    }
}
