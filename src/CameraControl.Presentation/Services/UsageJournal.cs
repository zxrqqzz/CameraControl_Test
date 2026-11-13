using System.IO;
using System.Text;
using System.Text.Json;
using CameraControl.Application.Cua;

namespace CameraControl.Presentation.Services;

/// <summary>一条"某轮对话用了多少 token"的记录。</summary>
public sealed record TurnUsageEntry(
    string? ContextId,
    DateTimeOffset AtUtc,
    int UncachedInputTokens,
    int CachedTokens,
    int CompletionTokens)
{
    public int TotalTokens => UncachedInputTokens + CachedTokens + CompletionTokens;
}

/// <summary>
/// 用量流水（跨进程持久化）。
///
/// 为什么要单独落盘：上下文文件只存"用户/助手的文字"（而且要保证注入前缀逐字节稳定，
/// 不能随便加字段）。用量属于界面信息，重启后仍要能看到，
/// 所以按行追加到 <c>data/usage/turns.jsonl</c>，读取时按顺序挂回历史对话。
///
/// 任何 IO 失败都只返回空/静默——用量看不到是小事，不能连累对话。
/// </summary>
public static class UsageJournal
{
    private static readonly JsonSerializerOptions JsonCompact = new() { WriteIndented = false };

    private static readonly object Sync = new();

    private static string? _path;

    /// <summary>流水文件路径：{dataDirectory}/usage/turns.jsonl。</summary>
    public static string Path
    {
        get
        {
            if (_path is not null) return _path;

            _path = System.IO.Path.Combine(
                CameraControl.Composition.ServiceRegistration.ResolveDataDirectory(),
                "usage",
                "turns.jsonl");

            return _path;
        }
    }

    /// <summary>记一轮用量。</summary>
    public static void Append(string? contextId, LlmUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        try
        {
            var entry = new TurnUsageEntry(
                contextId,
                DateTimeOffset.UtcNow,
                usage.UncachedInputTokens,
                usage.CachedTokens,
                usage.CompletionTokens);

            string line = JsonSerializer.Serialize(entry, JsonCompact);

            lock (Sync)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception)
        {
            // 记不下就算了：不影响对话本身
        }
    }

    /// <summary>读某套上下文下的全部记录（按写入顺序）。</summary>
    public static IReadOnlyList<TurnUsageEntry> Read(string? contextId)
    {
        try
        {
            lock (Sync)
            {
                if (!File.Exists(Path)) return [];

                var entries = new List<TurnUsageEntry>();

                foreach (string line in File.ReadLines(Path, new UTF8Encoding(false)))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        TurnUsageEntry? parsed = JsonSerializer.Deserialize<TurnUsageEntry>(line);

                        if (parsed is null) continue;
                        if (parsed.ContextId != contextId) continue;

                        entries.Add(parsed);
                    }
                    catch (JsonException)
                    {
                        // 单行损坏跳过
                    }
                }

                return entries;
            }
        }
        catch (Exception)
        {
            return [];
        }
    }
}
