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
/// LLM 配置。
/// <paramref name="ApiKeyProtected"/> 存的是 DPAPI 密文，**永不落明文**。
/// </summary>
public sealed class LlmSettings
{
    /// <summary>提供方显示名（用于日志/审计）。</summary>
    public string ProviderName { get; set; } = "OpenAI Compatible";

    /// <summary>OpenAI 兼容的 base URL，如 https://api.openai.com/v1 。</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>模型名。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>DPAPI 加密后的 API Key（Base64）。</summary>
    public string? ApiKeyProtected { get; set; }

    /// <summary>单次请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>采样温度。</summary>
    public double Temperature { get; set; } = 0.1;
}

/// <summary>LLM 配置存储：读写 settings 文件，密钥经 DPAPI 保护。</summary>
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

    public LlmSettings Load() =>
        _store.TryLoad<LlmSettings>(Key, out LlmSettings? settings) && settings is not null
            ? settings
            : new LlmSettings();

    public void Save(LlmSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _store.Save(Key, settings);
    }

    /// <summary>
    /// 设置明文 API Key（内部立即加密，调用方无需关心）。
    /// 传空字符串表示清除。
    /// </summary>
    public void SetApiKey(string? plainApiKey)
    {
        LlmSettings settings = Load();

        settings.ApiKeyProtected = string.IsNullOrWhiteSpace(plainApiKey)
            ? null
            : _protector.Protect(plainApiKey);

        Save(settings);
    }

    /// <summary>取明文 API Key；未配置或无法解密返回 null。</summary>
    public string? GetApiKey()
    {
        LlmSettings settings = Load();
        return settings.ApiKeyProtected is null ? null : _protector.Unprotect(settings.ApiKeyProtected);
    }

    /// <summary>是否已具备可用配置（有 baseUrl + model + key）。</summary>
    public bool IsConfigured()
    {
        LlmSettings settings = Load();
        return !string.IsNullOrWhiteSpace(settings.BaseUrl)
               && !string.IsNullOrWhiteSpace(settings.Model)
               && GetApiKey() is not null;
    }
}
