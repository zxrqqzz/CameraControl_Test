namespace CameraControl.Domain.Privacy;

/// <summary>图像用途。用于隐私策略判定"是否允许落盘"。</summary>
public enum ImagePurpose
{
    /// <summary>界面预览（永远只进内存）。</summary>
    Preview = 0,

    /// <summary>标定取图（可能允许落盘，取决于设备隐私策略）。</summary>
    Calibration = 1,

    /// <summary>业务采集（可能允许落盘）。</summary>
    Production = 2,

    /// <summary>离线分析（诊断工具使用）。</summary>
    Diagnostics = 3
}

/// <summary>
/// 图像持久化策略端口。
///
/// 铁律：任何"把图像写到磁盘"的功能都必须先过这里。
/// UVC / 笔记本内置摄像头默认禁止落盘（只进内存），AI 调用同样受限。
/// </summary>
public interface IImagePersistencePolicy
{
    /// <summary>该相机在该用途下是否允许落盘。</summary>
    bool IsAllowed(Cameras.CameraDescriptor camera, ImagePurpose purpose);

    /// <summary>给出不允许的原因（用于日志与结构化错误）。</summary>
    string Explain(Cameras.CameraDescriptor camera, ImagePurpose purpose);
}
