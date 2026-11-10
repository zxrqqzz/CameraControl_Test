namespace CameraControl.Domain.Ports;

/// <summary>通讯相关错误码。</summary>
public static class CommErrorCodes
{
    /// <summary>连接不存在。</summary>
    public const string NotFound = "COMM_NOT_FOUND";

    /// <summary>尚未打开。</summary>
    public const string NotOpen = "COMM_NOT_OPEN";

    /// <summary>该类型/端点暂不支持（未接入驱动）。</summary>
    public const string NotConfigured = "COMM_NOT_CONFIGURED";

    /// <summary>重复添加。</summary>
    public const string Duplicate = "COMM_DUPLICATE";
}

/// <summary>通讯连接类型。</summary>
public enum CommKind
{
    /// <summary>未知/未声明。</summary>
    Unknown = 0,

    /// <summary>Modbus TCP（常见于 PLC）。</summary>
    ModbusTcp = 1,

    /// <summary>串口。</summary>
    Serial = 2,

    /// <summary>TCP 透传。</summary>
    Tcp = 3
}

/// <summary>通讯连接描述。</summary>
/// <param name="Id">逻辑 id（如 <c>plc1</c>）。</param>
/// <param name="Kind">连接类型。</param>
/// <param name="Endpoint">端点，如 <c>192.168.0.10:502</c> 或 <c>COM3:9600</c>。</param>
public sealed record CommEndpoint(string Id, CommKind Kind, string Endpoint);

/// <summary>通讯日志条目（内存环形缓冲）。</summary>
/// <param name="At">时间。</param>
/// <param name="Direction">方向：out/in/info/error。</param>
/// <param name="Text">内容（十六进制或文本）。</param>
public sealed record CommLogEntry(DateTimeOffset At, string Direction, string Text);

/// <summary>一条通讯通道。</summary>
public interface ICommChannel
{
    /// <summary>端点信息。</summary>
    CommEndpoint Endpoint { get; }

    /// <summary>是否已打开。</summary>
    bool IsOpen { get; }

    /// <summary>打开连接。</summary>
    Task OpenAsync(CancellationToken cancellationToken);

    /// <summary>关闭连接。</summary>
    Task CloseAsync(CancellationToken cancellationToken);

    /// <summary>发送数据（字节）。</summary>
    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    /// <summary>最近日志（新的在前）。</summary>
    IReadOnlyList<CommLogEntry> GetLog(int maxEntries);
}

/// <summary>
/// 通讯通道工厂。
/// 返回 null 表示该类型尚无驱动实现——**不要伪造成功**。
/// </summary>
public interface ICommChannelFactory
{
    /// <summary>尝试创建通道；不支持返回 null。</summary>
    ICommChannel? TryCreate(CommEndpoint endpoint);

    /// <summary>已支持的类型，用于诊断。</summary>
    IReadOnlyList<CommKind> SupportedKinds { get; }
}
