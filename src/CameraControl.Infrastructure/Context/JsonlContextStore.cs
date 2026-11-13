using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CameraControl.Application.Cua.Context;

namespace CameraControl.Infrastructure.Context;

/// <summary>上下文存储的参数。</summary>
public sealed class ContextStoreOptions
{
    /// <summary>功能总开关（来自 <c>Mods/context/mod.json</c>）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>单份上下文的估算 token 上限；达到即轮换。默认 1M。</summary>
    public long MaxEstimatedTokens { get; init; } = 1_000_000;

    /// <summary>单条记录的最大字符数（超出截断，避免一条把上下文撑爆）。</summary>
    public int MaxEntryChars { get; init; } = 8_000;

    /// <summary>首次创建时的默认档位（配置里没写过档位时使用）。</summary>
    public ContextTier DefaultTier { get; init; } = ContextTier.InjectFull;
}

/// <summary>
/// 基于 JSONL 的上下文存储。
///
/// **核心不变量：已有字节永不改写。**
/// 每轮只做 <c>File.AppendAllText</c>，绝不"读出来再整体重写"——
/// 前缀一旦变动，服务端的缓存命中就全部失效，成本会成倍上升。
///
/// 目录结构：
/// <code>
/// &lt;root&gt;/&lt;profileId&gt;/ctx-000001.jsonl     ← 当前上下文
///                     /archive/ctx-000001.jsonl ← 归档
///                     /index.json               ← 档位 + 活动编号 + 清单
/// </code>
/// </summary>
public sealed class JsonlContextStore : IContextStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // 关键：默认编码器会把中文写成 \uXXXX，
        // 一是文件不可读，二是体积膨胀约 6 倍、估算 token 跟随失真。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>JSONL 每行用的紧凑选项（不缩进，否则一行会变成多行）。</summary>
    private static readonly JsonSerializerOptions JsonCompact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _root;
    private readonly ContextStoreOptions _options;
    private readonly ITokenEstimator _estimator;
    private readonly IIOClock _clock;

    private readonly object _sync = new();

    public JsonlContextStore(
        string rootDirectory,
        ContextStoreOptions options,
        ITokenEstimator? estimator = null,
        IIOClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        _root = rootDirectory;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _estimator = estimator ?? new CharacterTokenEstimator();
        _clock = clock ?? WallClock.Instance;
    }

    public bool IsEnabled => _options.Enabled;

    // ── 档位 ────────────────────────────────────────────────

    public ContextTier GetTier(string profileId)
    {
        lock (_sync)
        {
            ContextIndex index = LoadIndex(profileId);
            return index.Tier;
        }
    }

    public void SetTier(string profileId, ContextTier tier)
    {
        lock (_sync)
        {
            ContextIndex index = LoadIndex(profileId);
            index.Tier = tier;
            SaveIndex(profileId, index);
        }
    }

    public string? GetActiveContextId(string profileId)
    {
        lock (_sync)
        {
            return LoadIndex(profileId).ActiveId;
        }
    }

    // ── 注入 ────────────────────────────────────────────────

    public string BuildPrefix(string profileId)
    {
        if (!IsEnabled) return string.Empty;

        lock (_sync)
        {
            ContextIndex index = LoadIndex(profileId);

            if (index.Tier != ContextTier.InjectFull) return string.Empty;
            if (index.ActiveId is null) return string.Empty;

            string path = ActivePath(profileId, index.ActiveId);
            if (!File.Exists(path)) return string.Empty;

            IReadOnlyList<ContextEntry> entries = ReadEntries(path);
            if (entries.Count == 0) return string.Empty;

            var text = new StringBuilder();

            foreach (ContextEntry entry in entries)
            {
                text.Append(entry.ToInjectionLine());
                text.Append('\n');
            }

            return text.ToString();
        }
    }

    // ── 记录 ────────────────────────────────────────────────

    public void AppendTurn(string profileId, string userText, string assistantText)
    {
        if (!IsEnabled) return;

        lock (_sync)
        {
            ContextIndex index = LoadIndex(profileId);

            // 档位 Off：什么都不写
            if (index.Tier == ContextTier.Off) return;

            // 达到阈值先轮换，保证新追加的内容落在新上下文里
            if (index.ActiveId is null || EstimatedOfActive(profileId, index) >= _options.MaxEstimatedTokens)
                index = Rotate(profileId, index);

            string contextId = index.ActiveId!;
            string path = ActivePath(profileId, contextId);

            ContextSnapshot snapshot = FindOrCreateSnapshot(profileId, index, contextId);

            string userPlain = Truncate(userText);
            string assistantPlain = Truncate(assistantText);

            string userLine = SerializeEntry(ContextEntryRole.User, userPlain);
            string assistantLine = SerializeEntry(ContextEntryRole.Assistant, assistantPlain);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // ★ 唯一的写入方式：追加。绝不重写已有内容。
            File.AppendAllText(path, userLine + "\n" + assistantLine + "\n", Utf8NoBom);

            snapshot = snapshot with
            {
                EntryCount = snapshot.EntryCount + 2,
                EstimatedTokens = snapshot.EstimatedTokens
                                  + _estimator.Estimate(userPlain)
                                  + _estimator.Estimate(assistantPlain)
            };

            ReplaceSnapshot(index, snapshot);
            SaveIndex(profileId, index);
        }
    }

    // ── 手动操作 ────────────────────────────────────────────

    public string StartNewContext(string profileId)
    {
        lock (_sync)
        {
            ContextIndex index = LoadIndex(profileId);
            index = Rotate(profileId, index);
            SaveIndex(profileId, index);

            return index.ActiveId!;
        }
    }

    public string ClearCurrent(string profileId)
    {
        lock (_sync)
        {
            ContextIndex index = LoadIndex(profileId);

            string? oldId = index.ActiveId;

            if (oldId is not null) TryDelete(ActivePath(profileId, oldId));

            // 先轮换（拿到新编号），再把这个编号从清单里摘掉
            index = Rotate(profileId, index);

            if (oldId is not null) index.Contexts.RemoveAll(c => c.Id == oldId);

            SaveIndex(profileId, index);

            return index.ActiveId!;
        }
    }

    public IReadOnlyList<ContextSnapshot> List(string profileId)
    {
        lock (_sync)
        {
            ContextIndex index = LoadIndex(profileId);

            return [.. index.Contexts
                .OrderByDescending(c => c.CreatedAtUtc)
                .Select(c => c)];
        }
    }

    public bool Delete(string profileId, string contextId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);

        lock (_sync)
        {
            ContextIndex index = LoadIndex(profileId);
            ContextSnapshot? snapshot = index.Contexts.FirstOrDefault(c => c.Id == contextId);

            if (snapshot is null) return false;

            TryDelete(ActivePath(profileId, contextId));
            TryDelete(ArchivePath(profileId, contextId));

            bool wasActive = index.ActiveId == contextId;

            index.Contexts.RemoveAll(c => c.Id == contextId);

            if (wasActive)
            {
                index.ActiveId = null;
                index = Rotate(profileId, index);
            }

            SaveIndex(profileId, index);

            return true;
        }
    }

    // ── 轮换 ────────────────────────────────────────────────

    /// <summary>把当前上下文归档，并新建一份空上下文。</summary>
    private ContextIndex Rotate(string profileId, ContextIndex index)
    {
        if (index.ActiveId is not null)
        {
            string activePath = ActivePath(profileId, index.ActiveId);
            string archivePath = ArchivePath(profileId, index.ActiveId);

            if (File.Exists(activePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
                File.Move(activePath, archivePath, overwrite: true);
            }

            ContextSnapshot? old = index.Contexts.FirstOrDefault(c => c.Id == index.ActiveId);

            if (old is not null) ReplaceSnapshot(index, old with { IsArchived = true });
        }

        string nextId = NextId(index);
        string nextPath = ActivePath(profileId, nextId);

        Directory.CreateDirectory(Path.GetDirectoryName(nextPath)!);
        File.WriteAllText(nextPath, string.Empty, Utf8NoBom);

        index.ActiveId = nextId;
        index.Contexts.Add(new ContextSnapshot(nextId, _clock.UtcNow, 0, 0, IsArchived: false));

        return index;
    }

    /// <summary>
    /// 递增编号。
    ///
    /// **必须用单调计数器，不能"取现有最大 +1"**：删掉最大编号那一条之后，
    /// 后者会把同一个编号再发给新上下文，导致旧文件被新内容覆盖、
    /// 删除操作看起来"没生效"。
    /// </summary>
    private static string NextId(ContextIndex index)
    {
        index.LastNumber++;
        return $"ctx-{index.LastNumber:D6}";
    }

    private static int MaxExistingNumber(ContextIndex index)
    {
        int max = 0;

        foreach (ContextSnapshot snapshot in index.Contexts)
        {
            if (snapshot.Id.StartsWith("ctx-", StringComparison.Ordinal)
                && int.TryParse(snapshot.Id.AsSpan(4), out int number))
            {
                max = Math.Max(max, number);
            }
        }

        return max;
    }

    // ── 索引 ────────────────────────────────────────────────

    private string ProfileDirectory(string profileId)
    {
        // 配置 Id 是 Guid 字符串，但为防意外仍做一层清洗
        string safe = string.Concat(profileId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(_root, safe);
    }

    private string ActivePath(string profileId, string contextId) =>
        Path.Combine(ProfileDirectory(profileId), contextId + ".jsonl");

    private string ArchivePath(string profileId, string contextId) =>
        Path.Combine(ProfileDirectory(profileId), "archive", contextId + ".jsonl");

    private string IndexPath(string profileId) => Path.Combine(ProfileDirectory(profileId), "index.json");

    private ContextIndex LoadIndex(string profileId)
    {
        string path = IndexPath(profileId);

        try
        {
            if (File.Exists(path))
            {
                ContextIndex? loaded = JsonSerializer.Deserialize<ContextIndex>(File.ReadAllText(path, Utf8NoBom), Json);

                if (loaded is not null)
                {
                    loaded.Contexts ??= [];
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // 索引损坏就当没有，下面会重建
        }

        return Rebuild(profileId);
    }

    /// <summary>索引丢了就从磁盘重建（宁可重建，也不要让上下文"凭空消失"）。</summary>
    private ContextIndex Rebuild(string profileId)
    {
        var index = new ContextIndex { Tier = _options.DefaultTier };

        try
        {
            string directory = ProfileDirectory(profileId);

            if (Directory.Exists(directory))
            {
                foreach (string file in Directory.EnumerateFiles(directory, "ctx-*.jsonl"))
                {
                    string id = Path.GetFileNameWithoutExtension(file);
                    IReadOnlyList<ContextEntry> entries = ReadEntries(file);

                    index.Contexts.Add(new ContextSnapshot(
                        id,
                        File.GetCreationTimeUtc(file),
                        entries.Count,
                        entries.Sum(e => _estimator.Estimate(e.Text)),
                        IsArchived: false));
                }

                string archive = Path.Combine(directory, "archive");

                if (Directory.Exists(archive))
                {
                    foreach (string file in Directory.EnumerateFiles(archive, "ctx-*.jsonl"))
                    {
                        string id = Path.GetFileNameWithoutExtension(file);
                        IReadOnlyList<ContextEntry> entries = ReadEntries(file);

                        index.Contexts.Add(new ContextSnapshot(
                            id,
                            File.GetCreationTimeUtc(file),
                            entries.Count,
                            entries.Sum(e => _estimator.Estimate(e.Text)),
                            IsArchived: true));
                    }
                }
            }
        }
        catch (Exception)
        {
            // 重建失败也要能继续跑
        }

        // 活动上下文：存在且未归档的最新一份
        index.LastNumber = MaxExistingNumber(index);

        index.ActiveId = index.Contexts
            .Where(c => !c.IsArchived)
            .OrderByDescending(c => c.CreatedAtUtc)
            .Select(c => c.Id)
            .FirstOrDefault();

        return index;
    }

    private void SaveIndex(string profileId, ContextIndex index)
    {
        string path = IndexPath(profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string json = JsonSerializer.Serialize(index, Json);

        // 索引本身不是注入前缀的一部分，允许临时文件 + 替换
        string temp = path + ".tmp";
        File.WriteAllText(temp, json, Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }

    // ── 读写条目 ────────────────────────────────────────────

    private IReadOnlyList<ContextEntry> ReadEntries(string path)
    {
        var entries = new List<ContextEntry>();

        foreach (string line in File.ReadLines(path, Utf8NoBom))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                ContextLine? parsed = JsonSerializer.Deserialize<ContextLine>(line);

                if (parsed?.Text is null) continue;

                entries.Add(new ContextEntry(
                    parsed.Role == "assistant" ? ContextEntryRole.Assistant : ContextEntryRole.User,
                    parsed.Text,
                    parsed.At));
            }
            catch (Exception)
            {
                // 单行损坏跳过，不影响其余内容
            }
        }

        return entries;
    }

    private static string SerializeEntry(ContextEntryRole role, string text) =>
        JsonSerializer.Serialize(new ContextLine(
            role == ContextEntryRole.Assistant ? "assistant" : "user",
            text,
            DateTimeOffset.UtcNow), JsonCompact);

    private long EstimatedOfActive(string profileId, ContextIndex index)
    {
        if (index.ActiveId is null) return 0;

        ContextSnapshot? snapshot = index.Contexts.FirstOrDefault(c => c.Id == index.ActiveId);

        return snapshot?.EstimatedTokens ?? 0;
    }

    private static ContextSnapshot FindOrCreateSnapshot(string profileId, ContextIndex index, string contextId)
    {
        ContextSnapshot? existing = index.Contexts.FirstOrDefault(c => c.Id == contextId);

        if (existing is not null) return existing;

        var created = new ContextSnapshot(contextId, DateTimeOffset.UtcNow, 0, 0, IsArchived: false);
        index.Contexts.Add(created);

        return created;
    }

    private static void ReplaceSnapshot(ContextIndex index, ContextSnapshot snapshot)
    {
        int position = index.Contexts.FindIndex(c => c.Id == snapshot.Id);

        if (position >= 0) index.Contexts[position] = snapshot;
        else index.Contexts.Add(snapshot);
    }

    private string Truncate(string? text)
    {
        text ??= string.Empty;

        return text.Length <= _options.MaxEntryChars
            ? text
            : text[.._options.MaxEntryChars] + "…(已截断)";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 删不掉不影响主流程；下次操作还会尝试
        }
    }

    // ── 序列化模型 ──────────────────────────────────────────

    private sealed class ContextIndex
    {
        public ContextTier Tier { get; set; } = ContextTier.InjectFull;

        public string? ActiveId { get; set; }

        /// <summary>已用到的最大编号（单调递增，删除后不复用）。</summary>
        public int LastNumber { get; set; }

        public List<ContextSnapshot> Contexts { get; set; } = [];
    }

    private sealed record ContextLine(string Role, string Text, DateTimeOffset At);
}

/// <summary>时间来源（便于测试固定时间）。</summary>
public interface IIOClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>默认时钟（叫 WallClock 以免与 Infrastructure.SystemClock 撞名）。</summary>
public sealed class WallClock : IIOClock
{
    public static WallClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
