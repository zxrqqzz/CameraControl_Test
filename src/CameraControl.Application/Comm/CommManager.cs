using CameraControl.Domain.Ports;

namespace CameraControl.Application.Comm;

/// <summary>
/// 通讯连接管理（内存态）。
///
/// 职责：维护"逻辑 id → 通道"的映射、打开/关闭/发送、保留日志。
/// 具体驱动由 <see cref="ICommChannelFactory"/> 提供；未接入驱动时
/// 添加连接会明确失败（<see cref="CommErrorCodes.NotConfigured"/>），不会伪造成功。
/// </summary>
public interface ICommManager
{
    /// <summary>添加连接。</summary>
    ICommChannel Add(CommEndpoint endpoint);

    /// <summary>移除连接（未打开时也会移除）。</summary>
    Task RemoveAsync(string id);

    /// <summary>列出全部连接。</summary>
    IReadOnlyList<ICommChannel> List();

    /// <summary>查找。</summary>
    ICommChannel? Find(string id);

    /// <summary>打开。</summary>
    Task OpenAsync(string id, CancellationToken cancellationToken);

    /// <summary>关闭。</summary>
    Task CloseAsync(string id, CancellationToken cancellationToken);

    /// <summary>发送十六进制字符串。</summary>
    Task SendHexAsync(string id, string hex, CancellationToken cancellationToken);

    /// <summary>读取日志。</summary>
    IReadOnlyList<CommLogEntry> GetLog(string id, int maxEntries);

    /// <summary>工厂支持的类型（诊断用）。</summary>
    IReadOnlyList<CommKind> SupportedKinds { get; }
}

/// <summary>通讯连接管理实现。</summary>
public sealed class CommManager : ICommManager, IAsyncDisposable, IDisposable
{
    private readonly ICommChannelFactory _factory;
    private readonly Dictionary<string, ICommChannel> _channels = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public CommManager(ICommChannelFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public IReadOnlyList<CommKind> SupportedKinds => _factory.SupportedKinds;

    public ICommChannel Add(CommEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (string.IsNullOrWhiteSpace(endpoint.Id))
            throw new CameraOperationException(CommErrorCodes.NotConfigured, "连接 id 不能为空。");

        lock (_sync)
        {
            if (_channels.ContainsKey(endpoint.Id))
                throw new CameraOperationException(CommErrorCodes.Duplicate, $"连接 {endpoint.Id} 已存在。");
        }

        ICommChannel? channel = _factory.TryCreate(endpoint)
            ?? throw new CameraOperationException(
                CommErrorCodes.NotConfigured,
                $"暂未接入 {endpoint.Kind} 驱动（端点 {endpoint.Endpoint}）。" +
                $"当前支持：{(SupportedKinds.Count == 0 ? "无" : string.Join('/', SupportedKinds))}。" +
                "需要先接入对应厂商驱动后重试。");

        lock (_sync) _channels[endpoint.Id] = channel;

        return channel;
    }

    public async Task RemoveAsync(string id)
    {
        ICommChannel? channel;

        lock (_sync)
        {
            if (!_channels.Remove(id, out channel)) return;
        }

        try
        {
            if (channel.IsOpen) await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 移除阶段的关闭失败不应阻止移除
        }
    }

    public IReadOnlyList<ICommChannel> List()
    {
        lock (_sync) return [.. _channels.Values];
    }

    public ICommChannel? Find(string id)
    {
        lock (_sync) return _channels.GetValueOrDefault(id);
    }

    public Task OpenAsync(string id, CancellationToken cancellationToken) =>
        Require(id).OpenAsync(cancellationToken);

    public Task CloseAsync(string id, CancellationToken cancellationToken) =>
        Require(id).CloseAsync(cancellationToken);

    public Task SendHexAsync(string id, string hex, CancellationToken cancellationToken)
    {
        ICommChannel channel = Require(id);

        if (!channel.IsOpen)
            throw new CameraOperationException(CommErrorCodes.NotOpen, $"连接 {id} 未打开。");

        byte[] payload = ParseHex(hex);

        return channel.SendAsync(payload, cancellationToken);
    }

    public IReadOnlyList<CommLogEntry> GetLog(string id, int maxEntries) =>
        Require(id).GetLog(Math.Clamp(maxEntries, 1, 200));

    public async ValueTask DisposeAsync()
    {
        foreach (ICommChannel channel in List())
        {
            try
            {
                if (channel.IsOpen) await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 释放阶段吞异常
            }
        }

        lock (_sync) _channels.Clear();
    }

    public void Dispose()
    {
        // 退出路径：只发起关闭，不阻塞（同 CameraSessionRegistry 的理由）
        foreach (ICommChannel channel in List())
        {
            try
            {
                if (channel.IsOpen) _ = channel.CloseAsync(CancellationToken.None);
            }
            catch
            {
                // 退出路径不抛
            }
        }

        lock (_sync) _channels.Clear();
    }

    private ICommChannel Require(string id) =>
        Find(id) ?? throw new CameraOperationException(CommErrorCodes.NotFound, $"连接 {id} 不存在。");

    private static byte[] ParseHex(string hex)
    {
        string cleaned = hex.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        if (cleaned.Length == 0 || cleaned.Length % 2 != 0)
            throw new CameraOperationException(CommErrorCodes.NotConfigured, "十六进制串长度必须为非零偶数。");

        return Convert.FromHexString(cleaned);
    }
}
