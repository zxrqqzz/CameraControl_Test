using CameraControl.Application.Cua.Context;
using CameraControl.Infrastructure.Context;
using Xunit;

namespace CameraControl.Infrastructure.Tests;

/// <summary>
/// 上下文存储。
///
/// 最关键的一条：**已写入的字节永不改写**。
/// 缓存命中的前提就是前缀稳定——只要有一轮把前缀重写（哪怕内容一样、换行不同），
/// 服务端缓存就全废，成本成倍上升。所以这里用字节比对来验证，而不是只比对文本。
/// </summary>
public sealed class ContextStoreTests : IDisposable
{
    private const string Profile = "profile-1";

    private readonly string _root;

    public ContextStoreTests()
    {
        // 刻意用测试输出目录，而不是系统 TEMP：
        // 本机 %TEMP% 位于 C:\Users\... 下，存在外部写入拦截（UnauthorizedAccessException），
        // 与业务无关，放在项目盘上才能稳定跑。
        _root = Path.Combine(AppContext.BaseDirectory, "ctx-tests", Guid.NewGuid().ToString("N"));
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

    private JsonlContextStore Create(long maxTokens = 1_000_000, ContextStoreOptions? options = null) =>
        new(_root, options ?? new ContextStoreOptions { MaxEstimatedTokens = maxTokens });

    private string ActiveFile(string profileId = Profile)
    {
        string? id = Create(maxTokens: 1_000_000).GetActiveContextId(profileId);
        Assert.NotNull(id);

        return Path.Combine(_root, profileId, id + ".jsonl");
    }

    // ── 估算器 ──────────────────────────────────────────────

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    public void 估算器应按四字符一token处理西文(string? text, long expected)
    {
        Assert.Equal(expected, new CharacterTokenEstimator().Estimate(text));
    }

    [Fact]
    public void 估算器中文应一字一token()
    {
        Assert.Equal(6, new CharacterTokenEstimator().Estimate("相机打开了吗"));
    }

    [Fact]
    public void 估算器中英混合应分段计算()
    {
        // 4 个汉字(4) + "abcd"(1) = 5
        Assert.Equal(5, new CharacterTokenEstimator().Estimate("相机打开abcd"));
    }

    // ── 追加与前缀稳定 ──────────────────────────────────────

    [Fact]
    public void 写入一轮后应能作为前缀读回()
    {
        JsonlContextStore store = Create();

        store.AppendTurn(Profile, "有哪些相机", "共 2 台");

        string prefix = store.BuildPrefix(Profile);

        Assert.Contains("用户：有哪些相机", prefix, StringComparison.Ordinal);
        Assert.Contains("助手：共 2 台", prefix, StringComparison.Ordinal);
    }

    [Fact]
    public void 第二轮前缀必须严格包含第一轮前缀()
    {
        JsonlContextStore store = Create();

        store.AppendTurn(Profile, "第一问", "第一答");
        string first = store.BuildPrefix(Profile);

        store.AppendTurn(Profile, "第二问", "第二答");
        string second = store.BuildPrefix(Profile);

        Assert.StartsWith(first, second, StringComparison.Ordinal);
        Assert.True(second.Length > first.Length);
    }

    [Fact]
    public void 追加时不得改写已有字节()
    {
        JsonlContextStore store = Create();

        store.AppendTurn(Profile, "第一问", "第一答");

        string path = ActiveFile();
        byte[] before = File.ReadAllBytes(path);

        store.AppendTurn(Profile, "第二问", "第二答");

        byte[] after = File.ReadAllBytes(path);

        Assert.True(after.Length > before.Length);
        Assert.Equal(before, after.Take(before.Length).ToArray());
    }

    // ── 轮换 ────────────────────────────────────────────────

    [Fact]
    public void 达到阈值应自动归档并开新上下文()
    {
        // 每轮约几十个估算 token；阈值设小一点，几轮就会触发
        JsonlContextStore store = Create(maxTokens: 20);

        for (int i = 0; i < 6; i++)
            store.AppendTurn(Profile, $"第 {i} 个问题", "回答内容");

        IReadOnlyList<ContextSnapshot> all = store.List(Profile);

        Assert.True(all.Count >= 2, $"应发生轮换，实际只有 {all.Count} 份上下文");
        Assert.Contains(all, c => c.IsArchived);
    }

    [Fact]
    public void 轮换后旧文件应移动到archive()
    {
        JsonlContextStore store = Create(maxTokens: 20);

        for (int i = 0; i < 10; i++) store.AppendTurn(Profile, "问题内容", "回答内容");

        string archive = Path.Combine(_root, Profile, "archive");

        Assert.True(Directory.Exists(archive));
        Assert.NotEmpty(Directory.GetFiles(archive, "ctx-*.jsonl"));
    }

    // ── 两个"清空"的语义差别 ────────────────────────────────

    [Fact]
    public void 开启新对话应保留旧内容()
    {
        JsonlContextStore store = Create();

        store.AppendTurn(Profile, "重要的问题", "重要的回答");
        string oldId = store.GetActiveContextId(Profile)!;

        string newId = store.StartNewContext(Profile);

        Assert.NotEqual(oldId, newId);
        Assert.Equal(string.Empty, store.BuildPrefix(Profile));

        // 旧内容仍在磁盘上（归档），没有被删
        string archived = Path.Combine(_root, Profile, "archive", oldId + ".jsonl");

        Assert.True(File.Exists(archived));
        Assert.Contains("重要的问题", File.ReadAllText(archived), StringComparison.Ordinal);
    }

    [Fact]
    public void 清空应删除当前文件并新建()
    {
        JsonlContextStore store = Create();

        store.AppendTurn(Profile, "要删掉的内容", "回答");
        string oldId = store.GetActiveContextId(Profile)!;

        string newId = store.ClearCurrent(Profile);

        Assert.NotEqual(oldId, newId);
        Assert.Equal(string.Empty, store.BuildPrefix(Profile));

        // 清空是把内容真的丢掉（与"开启新对话"不同）
        Assert.DoesNotContain(store.List(Profile), c => c.Id == oldId);
    }

    // ── 档位 ────────────────────────────────────────────────

    [Fact]
    public void 只存档档位应写文件但不注入()
    {
        JsonlContextStore store = Create();

        store.SetTier(Profile, ContextTier.ArchiveOnly);
        store.AppendTurn(Profile, "问题", "回答");

        Assert.Equal(string.Empty, store.BuildPrefix(Profile));
        Assert.Contains("问题", File.ReadAllText(ActiveFile()), StringComparison.Ordinal);
    }

    [Fact]
    public void 关闭档位应完全不写文件()
    {
        JsonlContextStore store = Create();

        store.SetTier(Profile, ContextTier.Off);
        store.AppendTurn(Profile, "问题", "回答");

        // 连文件都不该创建
        string directory = Path.Combine(_root, Profile);

        Assert.False(
            Directory.Exists(directory) && Directory.GetFiles(directory, "ctx-*.jsonl").Length > 0);
    }

    [Fact]
    public void 总开关关闭时不应产生任何文件()
    {
        var store = new JsonlContextStore(
            _root,
            new ContextStoreOptions { Enabled = false },
            new CharacterTokenEstimator());

        store.AppendTurn(Profile, "问题", "回答");

        Assert.False(Directory.Exists(Path.Combine(_root, Profile)));
        Assert.Equal(string.Empty, store.BuildPrefix(Profile));
    }

    // ── 管理 ────────────────────────────────────────────────

    [Fact]
    public void 删除指定上下文应生效()
    {
        JsonlContextStore store = Create();

        store.AppendTurn(Profile, "问题", "回答");
        string id = store.GetActiveContextId(Profile)!;

        Assert.True(store.Delete(Profile, id));
        Assert.DoesNotContain(store.List(Profile), c => c.Id == id);
    }

    [Fact]
    public void 索引丢失后应能从磁盘重建()
    {
        JsonlContextStore store = Create();

        store.AppendTurn(Profile, "重建测试", "回答内容");

        File.Delete(Path.Combine(_root, Profile, "index.json"));

        JsonlContextStore reopened = Create();

        Assert.NotNull(reopened.GetActiveContextId(Profile));
        Assert.Contains("重建测试", reopened.BuildPrefix(Profile), StringComparison.Ordinal);
    }

    [Fact]
    public void 超长条目应被截断()
    {
        var store = new JsonlContextStore(
            _root,
            new ContextStoreOptions { MaxEntryChars = 50 },
            new CharacterTokenEstimator());

        store.AppendTurn(Profile, new string('问', 500), "回答");

        Assert.Contains("已截断", store.BuildPrefix(Profile), StringComparison.Ordinal);
    }

    [Fact]
    public void 默认档位应来自选项()
    {
        var store = new JsonlContextStore(
            _root,
            new ContextStoreOptions { DefaultTier = ContextTier.ArchiveOnly },
            new CharacterTokenEstimator());

        Assert.Equal(ContextTier.ArchiveOnly, store.GetTier(Profile));
    }
}