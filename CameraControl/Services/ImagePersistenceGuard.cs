using CameraControl.Models;
using System;

namespace CameraControl.Services
{
    /// <summary>
    /// 图像落盘闸门（隐私保护）。
    ///
    /// 用户要求：笔记本自带 / 外接 UVC 摄像头可能拍到人脸，画面**只做内存临时缓存，
    /// 程序关闭后释放清空，必须彻底消失**。
    ///
    /// 规则:
    /// - UVC / 自带摄像头：**默认禁止写磁盘**；单台设备可被显式允许（此时保证失效，界面需提示）。
    /// - 海康工业相机等其它来源：默认不受限制（拍的是工件，可能需要存图追溯）。
    /// - 来源不明（null）：按最保守处理，禁止。
    ///
    /// 约定：将来任何"把相机帧写到磁盘"的功能（截图 / 录像 / 导出 / 临时文件）
    /// 都必须先调用 <see cref="TryAuthorize"/>，不允许绕过。
    /// </summary>
    public static class ImagePersistenceGuard
    {
        /// <summary>
        /// 某来源的设备默认是否允许落盘。
        /// 目前只把 UVC（笔记本自带 / USB 摄像头）纳入隐私保护。
        /// 若要一刀切"所有相机都禁止落盘"，把这里直接改成 <c>=&gt; false</c>。
        /// </summary>
        public static bool DefaultsToPersist(DeviceSource source) => source != DeviceSource.Uvc;

        /// <summary>该设备当前是否允许把图像写到磁盘</summary>
        public static bool IsPersistAllowed(CameraDevice? device)
        {
            if (device == null) return false;                         // 来源不明，一律禁止
            if (device.AllowImagePersist.HasValue)                    // 显式开关优先
                return device.AllowImagePersist.Value;
            return DefaultsToPersist(device.Source);
        }

        /// <summary>该设备是否处于隐私保护范围（受约束）</summary>
        public static bool IsPrivacyProtected(CameraDevice? device) =>
            device != null && !IsPersistAllowed(device);

        /// <summary>
        /// 申请把图像写到磁盘。返回 false 表示已阻止，<paramref name="reason"/> 是给用户看的说明。
        /// 所有保存图像的代码路径都必须先过这里。
        /// </summary>
        public static bool TryAuthorize(CameraDevice? device, string purpose, out string reason)
        {
            if (IsPersistAllowed(device))
            {
                reason = string.Empty;
                return true;
            }

            string name = string.IsNullOrWhiteSpace(device?.Name) ? "未知设备" : device!.Name;
            reason = $"已阻止把「{name}」的图像保存到磁盘（{purpose}）：该设备在隐私保护范围内，" +
                     "画面只做内存临时缓存，关闭程序后清空。";
            App.LogVM.AddLog("[隐私] " + reason);
            return false;
        }

        /// <summary>给界面用的策略说明文字</summary>
        public static string DescribePolicy(CameraDevice? device)
        {
            if (device == null) return string.Empty;

            if (!IsPrivacyProtected(device))
            {
                return device.Source == DeviceSource.Uvc
                    ? "已允许保存该设备的图像——此时无法保证画面彻底消失，请谨慎使用。"
                    : "该设备不在隐私保护范围内，图像可按需保存。";
            }

            return "该设备受隐私保护：画面只存在于内存，不写磁盘、不生成临时文件；" +
                   "程序关闭后随内存一起清空。";
        }
    }
}
