using CameraControl.Application.Diagnostics;
using CameraControl.Domain.Ports;
using CameraControl.Domain.Robotics;
using YMConnect;

namespace CameraControl.Infrastructure.YmConnect;

/// <summary>
/// 安川 Motoman 机器人适配器（基于 YMConnect SDK）。
///
/// 事实来源：<c>https://developer.motoman.com/en/YMConnect</c> 的 C# 参考，
/// 以及旧项目实际跑过的连接/读位置代码。
///
/// 关键约束（文档明确写出）：
/// - MotionManager **仅 YRC1000 及更新控制器可用**，且控制器参数 <c>S2C425</c> 必须为 0；
/// - 运动前需要伺服 ON、Hold OFF、示教器处于远程模式；
/// - 运动是**轨迹式**：AddPointToTrajectory → MotionStart，不会随添加立刻动
///   （除非 MotionStart 已调用且轨迹已空）。
///
/// 所有"仅凭文档推断、尚未在真机验证"的地方都用 <c>TODO-实测</c> 标注。
/// </summary>
public sealed class YmConnectRobot : IRobot
{
    private const int ProgressPollIntervalMs = 100;

    private readonly RobotSettingsStore _settings;
    private readonly IClock _clock;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MotomanController? _controller;
    private string _connectedIp = string.Empty;

    public YmConnectRobot(RobotSettingsStore settings, IClock clock, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    public string AdapterName => "YMConnect";

    // ── 状态 ────────────────────────────────────────────────

    public async Task<RobotState> GetStateAsync(CancellationToken cancellationToken)
    {
        RobotSettings settings = _settings.Load();

        if (!settings.IsConfigured)
            return RobotState.NotConnected(_clock.UtcNow, "未配置机器人 IP（设置 → 机器人）。");

        try
        {
            return await Task.Run(() =>
            {
                _gate.Wait(cancellationToken);

                try
                {
                    MotomanController controller = ConnectOrReuse(settings);

                    if (!TryReadPose(controller, out RobotPose? pose, out string? poseError))
                        return RobotState.NotConnected(_clock.UtcNow, poseError);

                    string alarmText = ReadActiveAlarmsText(controller);

                    return new RobotState(
                        RobotConnectionState.Connected,
                        pose,
                        _clock.UtcNow,
                        IsMoving: false,
                        Message: string.IsNullOrWhiteSpace(alarmText) ? null : alarmText);
                }
                finally
                {
                    _gate.Release();
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("读取机器人状态失败", new Dictionary<string, object?>
            {
                ["ip"] = settings.Ip,
                ["error"] = ex.Message
            });

            return RobotState.NotConnected(_clock.UtcNow, ex.Message);
        }
    }

    // ── 运动 ────────────────────────────────────────────────

    public async Task<RobotMotionResult> MoveToAsync(
        RobotPose target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        RobotSettings settings = _settings.Load();

        if (!settings.IsConfigured)
        {
            return new RobotMotionResult(
                false, null,
                RobotErrorCodes.NotConnected,
                "未配置机器人 IP（设置 → 机器人）。");
        }

        return await Task.Run(() =>
        {
            _gate.Wait(cancellationToken);

            try
            {
                MotomanController controller = ConnectOrReuse(settings);

                // 1) 清空轨迹：避免上一次残留点被一起执行
                StatusInfo status = controller.MotionManager.ClearAllTrajectory();
                if (!IsOk(status, out string? error))
                    return Fail(RobotErrorCodes.Rejected, $"清空轨迹失败：{error}");

                // 2) 目标点。RobotCoordinate = 基座标系下的笛卡尔位姿（mm / 度）。
                //
                // TODO-实测：欧拉角约定。原生库导出含 FrameToZYXeuler/ZYXeulerToFrame，
                // 说明是 ZYX（先绕 X 滚转、再 Y 俯仰、最后 Z 偏航）。
                // 本项目 RobotPose 的 Rx/Ry/Rz 语义需在真机上核对一次。
                var destination = new PositionData
                {
                    CoordinateType = CoordinateType.RobotCoordinate,
                    AxisData = [target.X, target.Y, target.Z, target.Rx, target.Ry, target.Rz, 0, 0]
                };

                // 第 4 个参数：0 = 精确到点（不混合）。
                // TODO-实测：官方文档示例写的是 4 个参数且第 4 个为 MotionAccelDecel，
                // 但 1.1.10 实际签名的第 4 参数是 int（编译期确认）。
                // 语义（精度 or 插值类型）需在真机核对；标定取位希望"精确停到点"，故取 0。
                var motion = new LinearMotion(ControlGroupId.R1, destination, settings.SpeedMmPerSec, 0);

                status = controller.MotionManager.AddPointToTrajectory(motion);
                if (!IsOk(status, out error))
                    return Fail(RobotErrorCodes.Rejected, $"添加轨迹点失败：{error}");

                // 3) 运动前置：伺服 ON、解除 Hold。
                //
                // 文档把这些列为 8015 报警的常见补救（254 Servo Off、253 Hold Is On）。
                // 若控制器处于远程模式且已上电，这两步是无害的；
                // TODO-实测：某些安全配置下 SetServos 可能被拒绝——此时记录但不中断，
                // 让后续 MotionStart 以真实报警码失败，比在这里武断报错更准确。
                StatusInfo servo = controller.ControlCommands.SetServos(SignalStatus.ON);
                if (!IsOk(servo, out string? servoError))
                    _logger.Warn("机器人伺服上电失败（继续尝试运动）", new Dictionary<string, object?> { ["error"] = servoError });

                StatusInfo hold = controller.ControlCommands.SetHold(SignalStatus.OFF);
                if (!IsOk(hold, out string? holdError))
                    _logger.Warn("机器人解除 Hold 失败（继续尝试运动）", new Dictionary<string, object?> { ["error"] = holdError });

                // 4) 启动
                status = controller.MotionManager.MotionStart();
                if (!IsOk(status, out error))
                    return Fail(RobotErrorCodes.Rejected, $"启动运动失败：{error}");

                // 5) 等待到位：轮询剩余轨迹步数
                DateTimeOffset deadline = _clock.UtcNow + timeout;

                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        SafeStop(controller);
                        return Fail(RobotErrorCodes.Rejected, "运动被取消，已下发停止。");
                    }

                    status = controller.MotionManager.GetRemainingStepsInTrajectory(out int remaining);

                    if (!IsOk(status, out error))
                    {
                        SafeStop(controller);
                        return Fail(RobotErrorCodes.Rejected, $"查询轨迹进度失败：{error}");
                    }

                    if (remaining <= 0) break;

                    if (_clock.UtcNow > deadline)
                    {
                        SafeStop(controller);
                        return Fail(
                            RobotErrorCodes.Timeout,
                            $"运动超时（{timeout.TotalSeconds:F1}s），已下发停止。剩余 {remaining} 步。");
                    }

                    Thread.Sleep(ProgressPollIntervalMs);
                }

                // 6) 读回实际位姿
                TryReadPose(controller, out RobotPose? achieved, out _);

                return new RobotMotionResult(true, achieved);
            }
            catch (Exception ex)
            {
                _logger.Error("机器人运动异常", new Dictionary<string, object?>
                {
                    ["ip"] = settings.Ip,
                    ["error"] = ex.Message
                });

                return Fail(RobotErrorCodes.Rejected, ex.Message);
            }
            finally
            {
                _gate.Release();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        _gate.Wait(cancellationToken);

        try
        {
            if (_controller is null) return;

            // 停止并清空轨迹：避免"停了但残留点还在"导致下一步立刻动
            StatusInfo status = _controller.MotionManager.MotionStop(clearTrajectory: true);

            if (!IsOk(status, out string? error))
            {
                _logger.Warn("机器人停止返回失败", new Dictionary<string, object?> { ["error"] = error });
            }
        }
        catch (Exception ex)
        {
            // 停止失败也必须让调用方知道，但不要再抛——急停路径不该级联失败
            _logger.Error("机器人停止异常", new Dictionary<string, object?> { ["error"] = ex.Message });
        }
        finally
        {
            _gate.Release();
        }
    }, cancellationToken);

    // ── 连接管理 ────────────────────────────────────────────

    private MotomanController ConnectOrReuse(RobotSettings settings)
    {
        if (_controller is not null && string.Equals(_connectedIp, settings.Ip, StringComparison.Ordinal))
            return _controller;

        // IP 变了或尚未连接
        DisposeController();

        // 官方 2 参数重载：内部使用库默认超时/重试
        MotomanController? controller = MotomanController.OpenConnection(settings.Ip, out StatusInfo status);

        // ⚠ 已知缺陷：YMConnect_CS 1.1.10 的 StatusInfo.IsOk() 判断方向是反的，
        // 必须直接用 StatusCode == 0。
        if (controller is null || status.StatusCode != 0)
        {
            try { controller?.CloseConnection(); }
            catch { /* 忽略关闭异常 */ }

            throw new Domain.Ports.CameraOperationException(
                RobotErrorCodes.NotConnected,
                $"连接机器人失败：Code={status.StatusCode}, {status.Message}。请检查：① IP；" +
                "② 控制器 YCP 通讯服务；③ 网络连通；④ 防火墙。");
        }

        _controller = controller;
        _connectedIp = settings.Ip;

        _logger.Info("机器人已连接", new Dictionary<string, object?> { ["ip"] = settings.Ip });

        return controller;
    }

    private void DisposeController()
    {
        if (_controller is null) return;

        try { _controller.CloseConnection(); }
        catch { /* 旧项目同样忽略关闭异常：连接可能已断 */ }

        _controller = null;
        _connectedIp = string.Empty;
    }

    // ── 读取工具 ────────────────────────────────────────────

    private static bool TryReadPose(MotomanController controller, out RobotPose? pose, out string? error)
    {
        pose = null;
        error = null;

        try
        {
            // 基座标系笛卡尔位姿（mm / 度）
            // TODO-实测：ReadPositionData 的 index/specifier 在非用户坐标系下传 0（旧项目实测如此）。
            StatusInfo status = controller.ControlGroup.ReadPositionData(
                ControlGroupId.R1,
                CoordinateType.BaseCoordinate,
                0,
                0,
                out PositionData data);

            if (status.StatusCode != 0)
            {
                error = $"读取位姿失败：Code={status.StatusCode}, {status.Message}";
                return false;
            }

            double[] axis = data.AxisData;

            if (axis is null || axis.Length < 6)
            {
                error = "读取位姿失败：返回的轴数据不足 6 个分量。";
                return false;
            }

            pose = new RobotPose(axis[0], axis[1], axis[2], axis[3], axis[4], axis[5]);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ReadActiveAlarmsText(MotomanController controller)
    {
        try
        {
            StatusInfo status = controller.Faults.GetActiveAlarms(out ActiveAlarms alarms);

            if (status.StatusCode != 0) return $"读取报警失败：Code={status.StatusCode}, {status.Message}";

            // TODO-实测：ActiveAlarms 的结构成员未在 C# 参考页逐字段列出，
            // 官方示例直接用 Console.WriteLine(alarms) 打印（形如 "0 alarms active."）。
            // 这里先用其 ToString()，避免猜测字段名；实测后改成结构化读取。
            return alarms.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            return $"读取报警异常：{ex.Message}";
        }
    }

    private void SafeStop(MotomanController controller)
    {
        try { controller.MotionManager.MotionStop(clearTrajectory: true); }
        catch (Exception ex)
        {
            _logger.Warn("超时/取消后下发停止失败", new Dictionary<string, object?> { ["error"] = ex.Message });
        }
    }

    private static bool IsOk(StatusInfo status, out string? error)
    {
        if (status is null)
        {
            error = "返回状态为空。";
            return false;
        }

        // 不使用 status.IsOk()：1.1.10 实现相反
        if (status.StatusCode == 0)
        {
            error = null;
            return true;
        }

        error = $"Code={status.StatusCode}, {status.Message}";
        return false;
    }

    private static RobotMotionResult Fail(string code, string message) => new(false, null, code, message);
}
