namespace CameraControl.Application.Commands;

/// <summary>
/// 一个能力的执行者。
/// 每个能力一个实现，由 <see cref="CapabilityRegistry"/> 注册后即可被总线调用。
/// </summary>
/// <typeparam name="TCommand">该能力对应的命令类型。</typeparam>
public interface ICapabilityHandler<in TCommand> where TCommand : ICommand
{
    CapabilityDescriptor Descriptor { get; }

    Task<object?> HandleAsync(TCommand command, CommandContext context, CancellationToken cancellationToken);
}

/// <summary>能力清单（只读视图），用于白名单校验与导出给 LLM。</summary>
public interface ICapabilityCatalog
{
    IReadOnlyList<CapabilityDescriptor> All { get; }

    CapabilityDescriptor? Find(string capabilityId);
}

/// <summary>
/// 能力注册表。
///
/// 采用"注册时绑定委托"而不是运行时反射：既保留泛型编译期类型安全，
/// 又让总线分派保持 O(1) 字典查找。
/// </summary>
public sealed class CapabilityRegistry : ICapabilityCatalog
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public IReadOnlyList<CapabilityDescriptor> All =>
        [.. _entries.Values.Select(e => e.Descriptor).OrderBy(d => d.Id, StringComparer.Ordinal)];

    public CapabilityDescriptor? Find(string capabilityId) =>
        capabilityId is not null && _entries.TryGetValue(capabilityId, out Entry? entry)
            ? entry.Descriptor
            : null;

    /// <summary>注册一个能力。重复 id 或命令类型不匹配会立即抛异常（失败关闭）。</summary>
    public CapabilityRegistry Register<TCommand>(ICapabilityHandler<TCommand> handler)
        where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(handler);

        CapabilityDescriptor descriptor = handler.Descriptor
            ?? throw new InvalidOperationException("能力的 Descriptor 不能为 null。");

        if (descriptor.CommandType != typeof(TCommand))
        {
            throw new InvalidOperationException(
                $"能力 {descriptor.Id} 声明的命令类型是 {descriptor.CommandType.Name}，" +
                $"但处理器实现的是 {typeof(TCommand).Name}。");
        }

        if (!_entries.TryAdd(descriptor.Id, new Entry(
                descriptor,
                (command, context, ct) => handler.HandleAsync((TCommand)command, context, ct))))
        {
            throw new InvalidOperationException($"能力 id 重复注册：{descriptor.Id}。");
        }

        return this;
    }

    internal bool TryGetEntry(string capabilityId, out Entry entry) =>
        _entries.TryGetValue(capabilityId, out entry!);

    /// <summary>内部条目：描述符 + 已绑定类型的执行委托。</summary>
    internal sealed record Entry(
        CapabilityDescriptor Descriptor,
        Func<ICommand, CommandContext, CancellationToken, Task<object?>> Invoke);
}

/// <summary>命令总线：人与 AI 共用的唯一执行入口。</summary>
public interface ICommandBus
{
    Task<CommandResult> ExecuteAsync(ICommand command, Actor actor, CancellationToken cancellationToken = default);
}
