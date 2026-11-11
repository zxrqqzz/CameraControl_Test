using CameraControl.Domain.Ports;
using CameraControl.Infrastructure.Ai;
using Xunit;

namespace CameraControl.Infrastructure.Tests;

/// <summary>
/// 多配置 + 密钥不可修改 + baseUrl 归一化 + 模型分档。
///
/// 这些是"填个 key 就能用"的底座，出错会直接让用户配不对，所以全部用测试固定住。
/// </summary>
public sealed class LlmSettingsTests
{
    /// <summary>内存态设置存储（便于断言"落盘内容里没有明文 key"）。</summary>
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

        public LlmSettings? Peek() => _values.TryGetValue("llm", out object? raw) ? raw as LlmSettings : null;
    }

    /// <summary>可逆的假保护器：能断言"存的不是明文"。</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plainText) => "ENC:" + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(plainText));

        public string? Unprotect(string protectedText)
        {
            if (!protectedText.StartsWith("ENC:", StringComparison.Ordinal)) return null;

            return System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(protectedText[4..]));
        }
    }

    private static (LlmSettingsStore Store, MemorySettings Raw) Build()
    {
        var raw = new MemorySettings();
        return (new LlmSettingsStore(raw, new FakeProtector()), raw);
    }

    // ── baseUrl 归一化 ──────────────────────────────────

    [Theory]
    [InlineData("api.deepseek.com", "https://api.deepseek.com/v1")]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/v1")]
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1")]
    [InlineData("https://example.com/openai/v1", "https://example.com/openai/v1")]
    [InlineData("  api.deepseek.com  ", "https://api.deepseek.com/v1")]
    public void baseUrl应自动补scheme与v1(string input, string expected)
    {
        Assert.Equal(expected, LlmSettingsStore.NormalizeBaseUrl(input));
    }

    [Theory]
    [InlineData("localhost:11434", "http://localhost:11434/v1")]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080/v1")]
    [InlineData("192.168.1.50:8000", "http://192.168.1.50:8000/v1")]
    public void 本机与内网地址应默认http(string input, string expected)
    {
        Assert.Equal(expected, LlmSettingsStore.NormalizeBaseUrl(input));
    }

    [Fact]
    public void 空baseUrl应返回空串()
    {
        Assert.Equal(string.Empty, LlmSettingsStore.NormalizeBaseUrl(null));
        Assert.Equal(string.Empty, LlmSettingsStore.NormalizeBaseUrl("   "));
    }

    // ── 密钥 ────────────────────────────────────────────

    [Fact]
    public void 密钥应加密保存且落盘不含明文()
    {
        (LlmSettingsStore store, MemorySettings raw) = Build();

        LlmProfile profile = store.AddProfile(
            "DeepSeek", "api.deepseek.com", "deepseek-chat", "sk-super-secret-1234");

        LlmSettings? persisted = raw.Peek();

        Assert.NotNull(persisted);
        Assert.DoesNotContain("sk-super-secret-1234", persisted!.Profiles[0].ApiKeyProtected!);
        Assert.StartsWith("ENC:", persisted.Profiles[0].ApiKeyProtected!, StringComparison.Ordinal);

        // 但能正常解密回明文供调用使用
        Assert.Equal("sk-super-secret-1234", store.GetApiKey(profile.Id));
    }

    [Fact]
    public void 密钥提示只保留末4位()
    {
        (LlmSettingsStore store, _) = Build();

        LlmProfile profile = store.AddProfile("p", "api.deepseek.com", "m", "sk-abcdefgh1234");

        Assert.Equal("…1234", profile.KeyHint);
    }

    [Fact]
    public void 短key不应泄露任何字符()
    {
        Assert.Equal("****", LlmSettingsStore.MaskKey("ab12"));
        Assert.Null(LlmSettingsStore.MaskKey(null));
    }

    [Fact]
    public void 删除配置后密钥不可再取()
    {
        (LlmSettingsStore store, _) = Build();

        LlmProfile profile = store.AddProfile("p", "api.deepseek.com", "m", "sk-x");
        Assert.True(store.RemoveProfile(profile.Id));

        Assert.Null(store.GetApiKey(profile.Id));
        Assert.Empty(store.ListProfiles());
    }

    [Fact]
    public void 更新配置不应触碰密钥()
    {
        (LlmSettingsStore store, _) = Build();

        LlmProfile profile = store.AddProfile("p", "api.deepseek.com", "m", "sk-keep-me");

        bool ok = store.UpdateProfile(profile.Id, name: "新名字", model: "deepseek-reasoner");

        Assert.True(ok);
        Assert.Equal("新名字", store.FindProfile(profile.Id)!.Name);
        Assert.Equal("deepseek-reasoner", store.FindProfile(profile.Id)!.Model);

        // 关键：密钥仍是原来那把（没有入口能改它）
        Assert.Equal("sk-keep-me", store.GetApiKey(profile.Id));
    }

    // ── 多配置 ──────────────────────────────────────────

    [Fact]
    public void 第一条配置应自动成为当前配置()
    {
        (LlmSettingsStore store, _) = Build();

        LlmProfile first = store.AddProfile("A", "api.deepseek.com", "deepseek-chat", "k1");
        LlmProfile second = store.AddProfile("B", "api.openai.com", "gpt-4o", "k2");

        Assert.Equal(first.Id, store.ActiveProfile()!.Id);

        Assert.True(store.SetActive(second.Id));
        Assert.Equal(second.Id, store.ActiveProfile()!.Id);
    }

    [Fact]
    public void 删除当前配置应回退到剩余第一条()
    {
        (LlmSettingsStore store, _) = Build();

        LlmProfile first = store.AddProfile("A", "api.deepseek.com", "deepseek-chat", "k1");
        LlmProfile second = store.AddProfile("B", "api.openai.com", "gpt-4o", "k2");

        store.SetActive(second.Id);
        store.RemoveProfile(second.Id);

        Assert.Equal(first.Id, store.ActiveProfile()!.Id);
    }

    [Fact]
    public void 未配置时IsConfigured应为false()
    {
        (LlmSettingsStore store, _) = Build();

        Assert.False(store.IsConfigured());

        store.AddProfile("A", "api.deepseek.com", "deepseek-chat", "k1");
        Assert.True(store.IsConfigured());
    }

    [Fact]
    public void 没有密钥时IsConfigured应为false()
    {
        (LlmSettingsStore store, _) = Build();

        store.AddProfile("A", "api.deepseek.com", "deepseek-chat", plainApiKey: null);

        Assert.False(store.IsConfigured());
    }

    // ── 旧配置迁移 ──────────────────────────────────────

    [Fact]
    public void 旧版单条配置应迁移为一条profile()
    {
        var raw = new MemorySettings();

        raw.Save("llm", new LlmSettings
        {
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            ApiKeyProtected = "ENC:" + Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes("sk-legacy")),
            TimeoutSeconds = 30,
            Temperature = 0.2
        });

        var store = new LlmSettingsStore(raw, new FakeProtector());

        IReadOnlyList<LlmProfile> profiles = store.ListProfiles();

        Assert.Single(profiles);
        Assert.Equal("gpt-4o", profiles[0].Model);
        Assert.Equal("sk-legacy", store.GetApiKey(profiles[0].Id));

        // 迁移后旧字段要清空，避免同一把 key 在两处存在
        LlmSettings? persisted = raw.Peek();
        Assert.Null(persisted!.ApiKeyProtected);
    }

    // ── 模型分档与推理力度 ──────────────────────────────

    [Theory]
    [InlineData("deepseek-reasoner", ModelTier.Strong)]
    [InlineData("o3-mini", ModelTier.Strong)]
    [InlineData("gpt-4o-mini", ModelTier.Fast)]
    [InlineData("qwen-max", ModelTier.Strong)]
    [InlineData("deepseek-chat", ModelTier.Balanced)]
    [InlineData("some-unknown-model", ModelTier.Balanced)]
    public void 模型应能自动分档(string model, ModelTier expected)
    {
        Assert.Equal(expected, ModelCatalogService.Classify(model));
    }

    [Theory]
    [InlineData("o1", true)]
    [InlineData("o3-mini", true)]
    [InlineData("gpt-5", true)]
    [InlineData("deepseek-reasoner", false)]   // 自身推理，但不接受 reasoning_effort
    [InlineData("deepseek-chat", false)]
    public void 推理力度支持应精确判断(string model, bool expected)
    {
        Assert.Equal(expected, ModelCatalogService.SupportsReasoningEffort(model));
    }

    [Theory]
    [InlineData(LlmStrength.Low, "low")]
    [InlineData(LlmStrength.Medium, "medium")]
    [InlineData(LlmStrength.High, "high")]
    public void 强度应映射为服务端参数(LlmStrength strength, string expected)
    {
        Assert.Equal(expected, ModelCatalogService.ToReasoningEffort(strength));
    }

    [Fact]
    public async Task 拉取失败应回退内置清单而不是报错()
    {
        using var catalog = new ModelCatalogService();

        // 指向一个必然连不上的地址，验证兜底行为
        ModelCatalogResult result = await catalog.FetchAsync(
            "http://127.0.0.1:1/v1",
            apiKey: null,
            CancellationToken.None);

        Assert.Equal(ModelCatalogSource.BuiltIn, result.Source);
        Assert.NotEmpty(result.Models);
        Assert.NotNull(result.Error);
        Assert.True(result.IsFallback);
    }

    /// <summary>
    /// 端到端落盘：真实 JsonSettingsStore + 真实文件系统。
    ///
    /// 前面用内存存储验证业务规则，这里补一条"真的写进磁盘再读回来"——
    /// 用户遇到的正是**落盘**失败，模拟存储测不出来。
    /// </summary>
    [Fact]
    public void 应能真实落盘并读回()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "llm-e2e", Guid.NewGuid().ToString("N"));

        try
        {
            var store = new LlmSettingsStore(new JsonSettingsStore(root), new FakeProtector());

            LlmProfile profile = store.AddProfile(
                "DeepSeek", "api.deepseek.com", "deepseek-chat", "sk-e2e-5678");

            string path = Path.Combine(root, "llm.json");

            Assert.True(File.Exists(path));

            // 落盘内容不含明文 key
            string json = File.ReadAllText(path);
            Assert.DoesNotContain("sk-e2e-5678", json);

            // 重新构造 store（模拟重启）后仍能读回并解密
            var reopened = new LlmSettingsStore(new JsonSettingsStore(root), new FakeProtector());

            Assert.Single(reopened.ListProfiles());
            Assert.Equal("deepseek-chat", reopened.ActiveProfile()!.Model);
            Assert.Equal("sk-e2e-5678", reopened.GetApiKey(profile.Id));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 清理失败不影响结论
            }
        }
    }
}