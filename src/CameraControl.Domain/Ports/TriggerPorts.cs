using CameraControl.Domain.Capture;

namespace CameraControl.Domain.Ports;

/// <summary>
/// 触发源。
/// P0 实现软件触发；P2 扩展硬件触发（PLC / 光源控制器），调用方只换 <see cref="TriggerSpec"/>。
/// </summary>
public interface ITriggerSource
{
    TriggerSourceKind Kind { get; }

    /// <summary>
    /// 触发一次。
    /// 软件触发：向已武装的目标会话下发触发命令。
    /// 硬件触发：等待外部脉冲（不主动下发），<paramref name="targets"/> 仅用于确认武装状态。
    /// </summary>
    Task FireAsync(
        TriggerSpec spec,
        IReadOnlyList<ICameraSession> targets,
        CancellationToken cancellationToken);
}

/// <summary>
/// 就绪门：等待系统稳定到可以采集。
///
/// **工艺要求**：触发条件是"振动衰减到阈值以下并持续一段时间"，
/// 而不是"发完停止指令立即拍"——残余微振会污染高精度多视角融合。
/// </summary>
public interface IReadinessGate
{
    Task<ReadinessResult> WaitAsync(ReadinessCriteria criteria, CancellationToken cancellationToken);
}

/// <summary>系统时钟抽象（便于测试）。</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// 设置存储端口。
/// 实现方负责原子写（临时文件 + 替换）与损坏回退。
/// </summary>
public interface ISettingsStore
{
    /// <summary>尝试读取；不存在或损坏返回 false。</summary>
    bool TryLoad<T>(string key, out T? value);

    /// <summary>保存（原子写）。</summary>
    void Save<T>(string key, T value);
}
