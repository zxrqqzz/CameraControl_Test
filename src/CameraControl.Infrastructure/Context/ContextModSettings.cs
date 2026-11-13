using System.Text.Json;
using CameraControl.Application.Cua.Context;

namespace CameraControl.Infrastructure.Context;

/// <summary>
/// 上下文开关（<c>Mods/context/mod.json</c>）。
///
/// 设计取舍：**不做 DLL 反射加载**，只做"文件开关 + 内置实现"。
/// 理由：插件一旦变多，反射扫描会拖慢启动，而这里只需要"能关掉、能删干净"。
/// 文件夹或文件缺失时功能整体关闭——删掉 <c>Mods/context</c> 就等于彻底卸载。
/// </summary>
public sealed class ContextModSettings
{
    /// <summary>开关配置相对 Mods 目录的位置。</summary>
    public const string RelativePath = "context/mod.json";

    /// <summary>是否启用上下文功能。</summary>
    public bool Enabled { get; init; }

    /// <summary>单份上下文的估算 token 上限。</summary>
    public long MaxEstimatedTokens { get; init; } = 1_000_000;

    /// <summary>单条记录最大字符数。</summary>
    public int MaxEntryChars { get; init; } = 8_000;

    /// <summary>新配置的默认档位。</summary>
    public ContextTier DefaultTier { get; init; } = ContextTier.InjectFull;

    /// <summary>为什么被禁用（写日志用）。</summary>
    public string? DisabledReason { get; init; }

    /// <summary>
    /// 读取开关。文件不存在 → 禁用（这是有意的：删掉即卸载）。
    /// 文件损坏 → 禁用并给出原因，绝不"猜一个默认值"继续跑。
    /// </summary>
    public static ContextModSettings Load(string modsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        string path = Path.Combine(modsDirectory, RelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            return new ContextModSettings
            {
                Enabled = false,
                DisabledReason = $"未找到 {path}（删除该文件即关闭上下文功能）"
            };
        }

        try
        {
            ModDto? dto = JsonSerializer.Deserialize<ModDto>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (dto is null)
            {
                return new ContextModSettings { Enabled = false, DisabledReason = "mod.json 内容为空" };
            }

            return new ContextModSettings
            {
                Enabled = dto.Enabled,
                MaxEstimatedTokens = dto.MaxEstimatedTokens <= 0 ? 1_000_000 : dto.MaxEstimatedTokens,
                MaxEntryChars = dto.MaxEntryChars <= 0 ? 8_000 : dto.MaxEntryChars,
                DefaultTier = Enum.TryParse(dto.DefaultTier, ignoreCase: true, out ContextTier tier)
                    ? tier
                    : ContextTier.InjectFull
            };
        }
        catch (Exception ex)
        {
            return new ContextModSettings
            {
                Enabled = false,
                DisabledReason = $"mod.json 解析失败：{ex.GetType().Name} / {ex.Message}"
            };
        }
    }

    /// <summary>转成存储参数。</summary>
    public ContextStoreOptions ToStoreOptions() => new()
    {
        Enabled = Enabled,
        MaxEstimatedTokens = MaxEstimatedTokens,
        MaxEntryChars = MaxEntryChars,
        DefaultTier = DefaultTier
    };

    private sealed class ModDto
    {
        public bool Enabled { get; set; } = true;

        public long MaxEstimatedTokens { get; set; } = 1_000_000;

        public int MaxEntryChars { get; set; } = 8_000;

        public string? DefaultTier { get; set; }
    }
}
