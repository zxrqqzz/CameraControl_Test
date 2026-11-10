namespace CameraControl.Domain.Robotics;

/// <summary>机器人连接状态。</summary>
public enum RobotConnectionState
{
    /// <summary>未配置或未连接。</summary>
    Disconnected = 0,

    /// <summary>已连接，可通讯。</summary>
    Connected = 1,

    /// <summary>已连接但处于报警/急停状态。</summary>
    Faulted = 2
}

/// <summary>机器人状态快照。</summary>
/// <param name="State">连接状态。</param>
/// <param name="Pose">当前位姿（未连接时为 null）。</param>
/// <param name="At">采样时间（本地时钟）。</param>
/// <param name="IsMoving">是否在运动。</param>
/// <param name="Message">附加说明（报警文本等）。</param>
public sealed record RobotState(
    RobotConnectionState State,
    RobotPose? Pose,
    DateTimeOffset At,
    bool IsMoving,
    string? Message = null)
{
    public static RobotState NotConnected(DateTimeOffset at, string? message = null) =>
        new(RobotConnectionState.Disconnected, null, at, false, message ?? "机器人未连接。");
}

/// <summary>运动结果。</summary>
/// <param name="Ok">是否成功到达。</param>
/// <param name="Pose">结束位姿。</param>
/// <param name="ErrorCode">失败错误码。</param>
/// <param name="Message">说明。</param>
public sealed record RobotMotionResult(bool Ok, RobotPose? Pose, string? ErrorCode = null, string? Message = null);

/// <summary>
/// 机器人端口。
///
/// 设计要点：
/// - 只暴露"绝对位姿运动 + 停止 + 读状态"，不暴露关节/IO/速度曲线等厂商细节；
///   需要时再扩展，避免端口被某一家控制器绑死。
/// - <see cref="StopAsync"/> 是**安全动作**，风险等级为 Reversible，
///   任何情况下都不应被策略拒绝（包括 AI 发起）。
/// </summary>
public interface IRobot
{
    /// <summary>端口实现名（审计用），如 YMConnect / 模拟器。</summary>
    string AdapterName { get; }

    Task<RobotState> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>以绝对位姿运动（阻塞到位或超时）。</summary>
    Task<RobotMotionResult> MoveToAsync(
        RobotPose target,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>立即停止（安全动作）。</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>机器人错误码。</summary>
public static class RobotErrorCodes
{
    public const string NotConnected = "ROBOT_NOT_CONNECTED";
    public const string Faulted = "ROBOT_FAULTED";
    public const string Timeout = "ROBOT_MOVE_TIMEOUT";
    public const string Rejected = "ROBOT_COMMAND_REJECTED";
    public const string NotImplemented = "ROBOT_MODE_NOT_IMPLEMENTED";
}

/// <summary>
/// 未接入机器人时的占位实现：状态为"未连接"，运动请求一律拒绝。
///
/// 存在的意义：让上层（含 AI）能查询"机器人是否可用"并得到**明确答案**，
/// 而不是抛异常或假装成功。
/// </summary>
public sealed class UnavailableRobot : IRobot
{
    private readonly Ports.IClock _clock;

    public UnavailableRobot(Ports.IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public string AdapterName => "未接入";

    public Task<RobotState> GetStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(RobotState.NotConnected(_clock.UtcNow, "未配置机器人：P5 尚未接入控制器。"));

    public Task<RobotMotionResult> MoveToAsync(
        RobotPose target,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        Task.FromResult(new RobotMotionResult(
            false,
            null,
            RobotErrorCodes.NotConnected,
            "机器人未接入，无法执行运动。"));

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
/// <summary>
/// 机器人软限位校验（工作空间保护）。
///
/// 为什么放在领域层：这是**安全语义**，不属于某个具体适配器；
/// 不同适配器/配置都能提供实现，能力层只认"通过/越界"。
/// </summary>
public interface IRobotSafetyLimits
{
    /// <summary>是否配置了任何限位。</summary>
    bool HasLimits { get; }

    /// <summary>校验一个位姿；返回空数组表示通过。</summary>
    IReadOnlyList<string> Validate(RobotPose pose);
}

/// <summary>不限制（默认实现：未配置限位时使用）。</summary>
public sealed class UnlimitedRobotSafetyLimits : IRobotSafetyLimits
{
    private static readonly string[] None = [];

    public bool HasLimits => false;

    public IReadOnlyList<string> Validate(RobotPose pose) => None;
}