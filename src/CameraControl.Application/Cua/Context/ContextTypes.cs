namespace CameraControl.Application.Cua.Context;

/// <summary>
/// 上下文档位。
///
/// <see cref="ArchiveOnly"/> 与 <see cref="InjectFull"/> 的区别只有一条：
/// **要不要作为模型输入**。两者都会把对话写进文档。
/// </summary>
public enum ContextTier
{
    /// <summary>关闭：不写文件，也不注入。等于没有这个功能。</summary>
    Off = 0,

    /// <summary>只存档：写入文档但**不**注入模型输入（第一档）。</summary>
    ArchiveOnly = 1,

    /// <summary>完整注入：写入文档，并作为固定前缀注入模型输入（第二档）。</summary>
    InjectFull = 2
}

/// <summary>对话条目的角色。</summary>
public enum ContextEntryRole
{
    /// <summary>用户输入。</summary>
    User = 0,

    /// <summary>模型的最终输出。</summary>
    Assistant = 1
}

/// <summary>一条对话记录。</summary>
/// <param name="Role">谁说的。</param>
/// <param name="Text">内容（已截断）。</param>
/// <param name="AtUtc">写入时间。</param>
public sealed record ContextEntry(ContextEntryRole Role, string Text, DateTimeOffset AtUtc)
{
    /// <summary>转成注入时的文本行。</summary>
    public string ToInjectionLine() =>
        (Role == ContextEntryRole.User ? "用户：" : "助手：") + Text;
}

/// <summary>一份上下文的概要（列表/界面用）。</summary>
/// <param name="Id">编号，如 <c>ctx-000001</c>。</param>
/// <param name="CreatedAtUtc">创建时间。</param>
/// <param name="EntryCount">条目数（一问一答算 2 条）。</param>
/// <param name="EstimatedTokens">估算占用。</param>
/// <param name="IsArchived">是否已归档（满额轮换或手动开启新对话）。</param>
public sealed record ContextSnapshot(
    string Id,
    DateTimeOffset CreatedAtUtc,
    int EntryCount,
    long EstimatedTokens,
    bool IsArchived);

/// <summary>
/// token 估算。
///
/// 为什么不引 tokenizer：每个模型的分词表不同，引一套就得跟着模型换；
/// 而这个数字只用来判断"要不要轮换"，误差 20% 完全可以接受。
/// </summary>
public interface ITokenEstimator
{
    /// <summary>估算字符串的 token 数。</summary>
    long Estimate(string? text);
}

/// <summary>
/// 按字符估算 token：中文（含全角标点）约 1 字 1 token，其余约 4 字符 1 token。
///
/// 与 DeepSeek 实际计费的偏差在 ±20% 内，用于阈值判断足够。
/// </summary>
public sealed class CharacterTokenEstimator : ITokenEstimator
{
    public long Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        long cjk = 0;
        long other = 0;

        foreach (char c in text)
        {
            if (IsCjk(c)) cjk++;
            else other++;
        }

        return cjk + ((other + 3) / 4);
    }

    /// <summary>中日韩文字与全角标点（这类字符基本 1 字 1 token）。</summary>
    private static bool IsCjk(char c) =>
        c is >= '\u4E00' and <= '\u9FFF'      // 基本汉字
          or >= '\u3400' and <= '\u4DBF'      // 扩展 A
          or >= '\u3000' and <= '\u303F'      // 中文标点
          or >= '\uFF00' and <= '\uFFEF'      // 全角
          or >= '\u3040' and <= '\u30FF';     // 日文假名
}

/// <summary>
/// 上下文存储。
///
/// 实现必须保证一条铁律：**已写入的字节永不改写**。
/// 这是缓存命中的前提——前缀一旦变动，服务端缓存全部失效。
/// </summary>
public interface IContextStore
{
    /// <summary>功能是否启用（由 <c>Mods/context/mod.json</c> 决定）。</summary>
    bool IsEnabled { get; }

    /// <summary>取某套配置的档位。</summary>
    ContextTier GetTier(string profileId);

    /// <summary>设置档位。</summary>
    void SetTier(string profileId, ContextTier tier);

    /// <summary>
    /// 构建注入前缀。档位不是 <see cref="ContextTier.InjectFull"/> 时返回空串。
    /// 返回内容对同一批条目必须**逐字节稳定**。
    /// </summary>
    string BuildPrefix(string profileId);

    /// <summary>
    /// 记录一轮对话（用户输入 + 模型最终输出）。
    /// 达到阈值会先归档当前上下文、再开新的，然后把这两条写进新上下文。
    /// </summary>
    void AppendTurn(string profileId, string userText, string assistantText);

    /// <summary>开启新对话：新建编号，**旧文档原样保留**（标记为已归档）。</summary>
    string StartNewContext(string profileId);

    /// <summary>清空：删除当前上下文，然后新建一个空的。</summary>
    string ClearCurrent(string profileId);

    /// <summary>列出该配置下的所有上下文（含归档）。</summary>
    IReadOnlyList<ContextSnapshot> List(string profileId);

    /// <summary>
    /// 读回**当前上下文**的全部条目（按写入顺序）。
    ///
    /// 用途：AI 页的对话区要把历史对话渲染出来（关掉程序再打开还能看到上下文里发生过什么）。
    /// 返回的是已写入的原始文本，不含思考/步骤/用量——那些只存在于本次运行的界面里。
    /// 功能关闭、没有当前上下文、文件损坏时一律返回空，不抛异常。
    /// </summary>
    IReadOnlyList<ContextEntry> ReadActiveEntries(string profileId);

    /// <summary>删除指定上下文。</summary>
    bool Delete(string profileId, string contextId);

    /// <summary>当前上下文编号（没有则返回 null）。</summary>
    string? GetActiveContextId(string profileId);
}
