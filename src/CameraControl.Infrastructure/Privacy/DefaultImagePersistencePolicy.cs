using CameraControl.Domain.Cameras;
using CameraControl.Domain.Ports;
using CameraControl.Domain.Privacy;

namespace CameraControl.Infrastructure.Privacy;

/// <summary>
/// 默认图像持久化策略。
///
/// 规则（用户明确要求，不要放宽）：
/// - 任何相机的 <see cref="ImagePurpose.Preview"/> 一律不落盘；
/// - UVC（含笔记本内置）与红外摄像头：**任何用途都不允许落盘**；
/// - 工业相机：标定/业务/诊断允许落盘。
/// </summary>
public sealed class DefaultImagePersistencePolicy : IImagePersistencePolicy
{
    public bool IsAllowed(CameraDescriptor camera, ImagePurpose purpose)
    {
        if (camera is null) return false;

        // 预览永远只在内存
        if (purpose == ImagePurpose.Preview) return false;

        // 隐私保护范围：UVC / 红外
        if (camera.Kind == CameraKind.Uvc) return false;
        if (camera.IsInfraredOnly) return false;

        return true;
    }

    public string Explain(CameraDescriptor camera, ImagePurpose purpose)
    {
        if (camera is null) return "设备未知，按最保守策略处理：不允许落盘。";

        if (purpose == ImagePurpose.Preview)
            return "预览画面只允许存在于内存，不落盘。";

        if (camera.Kind == CameraKind.Uvc)
            return $"「{camera.DisplayName}」是 UVC/内置摄像头，受隐私保护：画面只进内存，不写磁盘。";

        if (camera.IsInfraredOnly)
            return $"「{camera.DisplayName}」是红外摄像头，仅用于人脸解锁，不取流也不落盘。";

        return $"「{camera.DisplayName}」不在隐私保护范围内，允许落盘。";
    }
}
