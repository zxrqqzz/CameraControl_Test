namespace CameraControl.Domain.Calibration;

/// <summary>
/// 一次标定的结果记录（可持久化）。
/// </summary>
/// <param name="CameraId">相机 id（与会话 id 一致）。</param>
/// <param name="Intrinsics">内参。</param>
/// <param name="ViewCount">参与求解的视角数。</param>
/// <param name="HomographyRmsePx">单应对齐的均方根误差（像素）。</param>
/// <param name="SolvedAtUtc">求解时间。</param>
/// <param name="GridDescription">棋盘描述，如 <c>12x13@30mm</c>。</param>
/// <param name="Note">备注（标定人、环境等）。</param>
/// <param name="DistortionNote">
/// 畸变模型的说明。当前求解器**不估计畸变**，这里如实记录，避免把结果误当成完整标定。
/// </param>
public sealed record CalibrationRecord(
    string CameraId,
    CameraIntrinsics Intrinsics,
    int ViewCount,
    double HomographyRmsePx,
    DateTimeOffset SolvedAtUtc,
    string GridDescription,
    string? Note = null,
    string DistortionNote = "未估计畸变（闭合解）；广角/鱼眼镜头结果不可直接使用。")
{
    /// <summary>是否已标定。</summary>
    public bool IsValid => Intrinsics.Fx > 0 && Intrinsics.Fy > 0;
}

/// <summary>
/// 标定结果存储端口。
/// </summary>
public interface ICalibrationStore
{
    /// <summary>读取指定相机的标定结果；未标定返回 null。</summary>
    Task<CalibrationRecord?> GetAsync(string cameraId, CancellationToken cancellationToken);

    /// <summary>保存（覆盖同名记录）。</summary>
    Task SaveAsync(CalibrationRecord record, CancellationToken cancellationToken);

    /// <summary>列出全部标定结果。</summary>
    Task<IReadOnlyList<CalibrationRecord>> ListAsync(CancellationToken cancellationToken);

    /// <summary>删除；不存在返回 false。</summary>
    Task<bool> RemoveAsync(string cameraId, CancellationToken cancellationToken);
}

/// <summary>标定相关错误码。</summary>
public static class CalibrationStoreErrorCodes
{
    public const string NotCalibrated = "CALIB_NOT_CALIBRATED";
    public const string CameraNotFound = "CALIB_CAMERA_NOT_FOUND";
    public const string InvalidArguments = "CALIB_INVALID_ARGUMENTS";
    public const string SolveFailed = "CALIB_SOLVE_FAILED";
    public const string StoreFailed = "CALIB_STORE_FAILED";
}
