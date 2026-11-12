using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CameraControl.Infrastructure.Ai;

/// <summary>
/// API Key 的保护接口。实现负责加解密，调用方永远不接触明文持久化。
/// </summary>
public interface ISecretProtector
{
    /// <summary>加密并返回可持久化的字符串（通常是 Base64）。</summary>
    string Protect(string plainText);

    /// <summary>解密；失败返回 null（密钥被其它用户/机器加密时会失败）。</summary>
    string? Unprotect(string protectedText);
}

/// <summary>
/// 基于 Windows DPAPI 的密钥保护（当前用户作用域）。
///
/// 用 P/Invoke 直接调 crypt32，而不是引 `System.Security.Cryptography.ProtectedData` 包：
/// 少一个依赖，行为与 Windows 自带的"仅本用户可解密"完全一致。
///
/// **注意**：DPAPI 绑定用户与机器。换用户或换机器后无法解密，需要重新输入 API Key。
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    public string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);

        byte[] data = Encoding.UTF8.GetBytes(plainText);
        byte[] encrypted = ProtectBytes(data);
        return Convert.ToBase64String(encrypted);
    }

    public string? Unprotect(string protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText)) return null;

        try
        {
            byte[] encrypted = Convert.FromBase64String(protectedText);
            byte[] data = UnprotectBytes(encrypted);
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            // 换了用户/机器、或数据被篡改：返回 null，由调用方提示重新输入
            return null;
        }
    }

    private static byte[] ProtectBytes(byte[] data) => Transform(data, protect: true);

    private static byte[] UnprotectBytes(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new DataBlob();
        var output = new DataBlob();
        IntPtr inputPtr = IntPtr.Zero;

        try
        {
            inputPtr = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, inputPtr, data.Length);
            input.Data = inputPtr;
            input.Length = data.Length;

            bool ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);

            if (!ok) throw new InvalidOperationException(
                $"DPAPI 调用失败，Win32 错误码 {Marshal.GetLastWin32Error()}。");

            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            if (inputPtr != IntPtr.Zero) Marshal.FreeHGlobal(inputPtr);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);
}

/// <summary>
/// 思考强度（"推理等级"）。
///
/// 与 DeepSeek / OpenAI 的实际取值对齐：
/// - <see cref="Off"/>：关闭思考（DeepSeek 用 <c>thinking.type = disabled</c>）
/// - <see cref="Low"/> / <see cref="High"/> / <see cref="Max"/>：<c>reasoning_effort = low/high/max</c>
///
/// 刻意**不提供"中"档**：DeepSeek 的 <c>reasoning_effort</c> 没有 medium
/// （medium 会被映射成 high），给了也只会让人以为设了中档。
/// </summary>
public enum LlmStrength
{
    /// <summary>关闭思考。</summary>
    Off = 0,

    /// <summary>低。</summary>
    Low = 1,

    /// <summary>高（DeepSeek 默认）。</summary>
    High = 2,

    /// <summary>最大。</summary>
    Max = 3
}

/// <summary>
/// 一套模型配置。
///
/// **API Key 只能写入一次**：新增条目时加密保存，之后只读（界面上不可编辑、不可回显明文）。
/// 要换 key 只能删除整条再新增——这样"密钥泄露"只可能来自新增动作，而不可能来自"编辑时看到旧值"。
/// </summary>
public sealed class LlmProfile
{
    /// <summary>稳定 id。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名，如 "DeepSeek 官方"。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>base URL（保存前已归一化，含 /v1）。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>模型名。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>DPAPI 密文。**写入后不再修改**。</summary>
    public string? ApiKeyProtected { get; set; }

    /// <summary>
    /// 密钥提示：只保留末 4 位，用于人工辨认是哪一把钥匙。
    /// 不含完整密钥，可安全落盘。
    /// </summary>
    public string? KeyHint { get; set; }

    /// <summary>
    /// 该 key 实际可用的模型名（识别时抓取并缓存）。
    /// 存在这里是为了"使用时切换模型"不必每次重新拉取。
    /// </summary>
    public List<string> Models { get; set; } = [];

    /// <summary>推理力度。仅当所选模型支持 reasoning_effort 时才真正生效。</summary>
    public LlmStrength Strength { get; set; } = LlmStrength.High;

    /// <summary>单次请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>采样温度。</summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>是否已保存 key。</summary>
    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKeyProtected);
}

/// <summary>
/// LLM 配置：**多条配置并存**，可切换当前使用的一条。
/// </summary>
public sealed class LlmSettings
{
    /// <summary>全部配置。</summary>
    public List<LlmProfile> Profiles { get; set; } = [];

    /// <summary>当前使用的配置 id。</summary>
    public string? ActiveProfileId { get; set; }

    // ── 旧版扁平字段：仅用于一次性迁移，迁移后置空 ──────────
    public string? ProviderName { get; set; }
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
    public string? ApiKeyProtected { get; set; }
    public int TimeoutSeconds { get; set; }
    public double Temperature { get; set; }
}

/// <summary>
/// LLM 配置存储：读写 settings 文件，密钥经 DPAPI 保护。
///
/// 约定：
/// - <see cref="AddProfile"/> 之外**没有**修改 key 的入口（只能删条目重建）；
/// - baseUrl 保存前统一归一化（自动补 scheme 与 /v1）。
/// </summary>
public sealed class LlmSettingsStore
{
    private const string Key = "llm";

    private readonly Domain.Ports.ISettingsStore _store;
    private readonly ISecretProtector _protector;

    public LlmSettingsStore(Domain.Ports.ISettingsStore store, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protector);

        _store = store;
        _protector = protector;
    }

    // ── 读取 ────────────────────────────────────────────────

    public LlmSettings Load()
    {
        LlmSettings settings = _store.TryLoad<LlmSettings>(Key, out LlmSettings? loaded) && loaded is not null
            ? loaded
            : new LlmSettings();

        return MigrateLegacy(settings);
    }

    public IReadOnlyList<LlmProfile> ListProfiles() => Load().Profiles;

    public LlmProfile? FindProfile(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Load().Profiles.FirstOrDefault(p => p.Id == id);

    /// <summary>当前生效的配置；没有则返回 null。</summary>
    public LlmProfile? ActiveProfile()
    {
        LlmSettings settings = Load();

        LlmProfile? active = settings.Profiles.FirstOrDefault(p => p.Id == settings.ActiveProfileId);

        return active ?? settings.Profiles.FirstOrDefault();
    }

    /// <summary>取某条配置的明文 key；解密失败（换用户/机器）返回 null。</summary>
    public string? GetApiKey(string profileId)
    {
        LlmProfile? profile = FindProfile(profileId);
        return profile?.ApiKeyProtected is null ? null : _protector.Unprotect(profile.ApiKeyProtected);
    }

    /// <summary>当前配置的明文 key。</summary>
    public string? GetActiveApiKey()
    {
        LlmProfile? profile = ActiveProfile();
        return profile is null ? null : GetApiKey(profile.Id);
    }

    /// <summary>是否已具备可用配置（有模型 + key 可解）。</summary>
    public bool IsConfigured()
    {
        LlmProfile? profile = ActiveProfile();

        return profile is not null
               && !string.IsNullOrWhiteSpace(profile.Model)
               && GetApiKey(profile.Id) is not null;
    }

    // ── 写入 ────────────────────────────────────────────────

    /// <summary>新增配置（唯一写入 key 的入口）。</summary>
    public LlmProfile AddProfile(
        string name,
        string baseUrl,
        string model,
        string? plainApiKey,
        LlmStrength strength = LlmStrength.High,
        IReadOnlyList<string>? models = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        string normalized = NormalizeBaseUrl(baseUrl);

        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("baseUrl 不能为空。", nameof(baseUrl));

        LlmSettings settings = Load();

        var profile = new LlmProfile
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = string.IsNullOrWhiteSpace(name) ? DescribeHost(normalized) : name.Trim(),
            BaseUrl = normalized,
            Model = model.Trim(),
            Strength = strength,
            ApiKeyProtected = string.IsNullOrWhiteSpace(plainApiKey) ? null : _protector.Protect(plainApiKey),
            KeyHint = MaskKey(plainApiKey),
            Models = models is null ? [] : [.. models],
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        settings.Profiles.Add(profile);

        // 第一条自动设为当前
        settings.ActiveProfileId ??= profile.Id;

        Save(settings);

        return profile;
    }

    /// <summary>删除整条配置（含密钥）。不存在返回 false。</summary>
    public bool RemoveProfile(string profileId)
    {
        LlmSettings settings = Load();

        int removed = settings.Profiles.RemoveAll(p => p.Id == profileId);

        if (removed == 0) return false;

        if (settings.ActiveProfileId == profileId)
            settings.ActiveProfileId = settings.Profiles.FirstOrDefault()?.Id;

        Save(settings);

        return true;
    }

    /// <summary>切换当前配置。</summary>
    public bool SetActive(string profileId)
    {
        LlmSettings settings = Load();

        if (settings.Profiles.All(p => p.Id != profileId)) return false;

        settings.ActiveProfileId = profileId;
        Save(settings);

        return true;
    }

    /// <summary>更新缓存的模型清单。</summary>
    public bool SetModels(string profileId, IReadOnlyList<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        LlmSettings settings = Load();
        LlmProfile? profile = settings.Profiles.FirstOrDefault(p => p.Id == profileId);

        if (profile is null) return false;

        profile.Models = [.. models];
        Save(settings);

        return true;
    }

    /// <summary>
    /// 修改配置的**非密钥**字段。密钥不在此列——要换 key 请删除后重新新增。
    /// </summary>
    public bool UpdateProfile(
        string profileId,
        string? name = null,
        string? baseUrl = null,
        string? model = null,
        LlmStrength? strength = null,
        int? timeoutSeconds = null,
        double? temperature = null)
    {
        LlmSettings settings = Load();

        LlmProfile? profile = settings.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null) return false;

        if (!string.IsNullOrWhiteSpace(name)) profile.Name = name.Trim();

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            string normalized = NormalizeBaseUrl(baseUrl);
            if (!string.IsNullOrWhiteSpace(normalized)) profile.BaseUrl = normalized;
        }

        if (!string.IsNullOrWhiteSpace(model)) profile.Model = model.Trim();
        if (strength is not null) profile.Strength = strength.Value;
        if (timeoutSeconds is not null) profile.TimeoutSeconds = Math.Clamp(timeoutSeconds.Value, 5, 600);
        if (temperature is not null) profile.Temperature = Math.Clamp(temperature.Value, 0, 2);

        Save(settings);

        return true;
    }

    // ── 归一化 / 掩码 ───────────────────────────────────────

    /// <summary>
    /// baseUrl 归一化：
    /// - 缺 scheme 时补上（本机/内网默认 http，其它默认 https）；
    /// - 路径为空时补 <c>/v1</c>；
    /// - 已带路径（/v1、/openai/v1、/v1beta…）则原样保留，不猜。
    /// </summary>
    public static string NormalizeBaseUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string text = raw.Trim().TrimEnd('/');

        if (text.Length == 0) return string.Empty;

        if (!text.Contains("://", StringComparison.Ordinal))
            text = (IsLikelyLocal(text) ? "http://" : "https://") + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)) return string.Empty;

        string path = uri.AbsolutePath.TrimEnd('/');

        if (path.Length == 0)
            path = "/v1";

        var builder = new UriBuilder(uri) { Path = path };

        return builder.Uri.ToString().TrimEnd('/');
    }

    /// <summary>给 baseUrl 生成一个可读名字（未填名称时用）。</summary>
    public static string DescribeHost(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return "未命名";

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ? uri.Host : baseUrl;
    }

    /// <summary>
    /// 生成密钥提示：只保留末 4 位。
    /// 不含完整密钥，可安全落盘，用于人工辨认"是哪一把钥匙"。
    /// </summary>
    public static string? MaskKey(string? plainApiKey)
    {
        if (string.IsNullOrWhiteSpace(plainApiKey)) return null;

        string trimmed = plainApiKey.Trim();

        return trimmed.Length <= 4 ? "****" : $"…{trimmed[^4..]}";
    }

    private static bool IsLikelyLocal(string text)
    {
        string host = text.Split('/', ':')[0];

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || host.StartsWith("127.", StringComparison.Ordinal)
               || host.StartsWith("10.", StringComparison.Ordinal)
               || host.StartsWith("192.168.", StringComparison.Ordinal)
               || host.StartsWith("172.16.", StringComparison.Ordinal);
    }

    // ── 私有 ────────────────────────────────────────────────

    private void Save(LlmSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _store.Save(Key, settings);
    }

    /// <summary>
    /// 旧版（单条扁平字段）→ 多条配置的一次性迁移。
    /// 迁移后清掉旧字段，避免同一把 key 在两处存在。
    /// </summary>
    private LlmSettings MigrateLegacy(LlmSettings settings)
    {
        if (settings.Profiles.Count > 0) return settings;
        if (string.IsNullOrWhiteSpace(settings.ApiKeyProtected)) return settings;

        string? baseUrl = NormalizeBaseUrl(settings.BaseUrl);
        string model = settings.Model ?? string.Empty;

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
            return settings;

        var profile = new LlmProfile
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = DescribeHost(baseUrl),
            BaseUrl = baseUrl,
            Model = model,
            ApiKeyProtected = settings.ApiKeyProtected,
            KeyHint = null,
            TimeoutSeconds = settings.TimeoutSeconds <= 0 ? 60 : settings.TimeoutSeconds,
            Temperature = settings.Temperature <= 0 ? 0.1 : settings.Temperature
        };

        settings.Profiles.Add(profile);
        settings.ActiveProfileId = profile.Id;
        settings.BaseUrl = null;
        settings.Model = null;
        settings.ApiKeyProtected = null;
        settings.ProviderName = null;

        try
        {
            Save(settings);
        }
        catch (Exception)
        {
            // 迁移时的落盘失败不能拖垮启动：内存里已经是迁移后的结构，
            // 下次启动会再试一次。真正写不进去时，用户新增配置会给出明确错误。
        }

        return settings;
    }
}
