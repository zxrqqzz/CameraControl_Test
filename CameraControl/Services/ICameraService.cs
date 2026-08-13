using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CameraControl.Services
{
    public interface ICameraService : IDisposable
    {
        /// <summary>
        /// 初始化相机（枚举、打开、配置）
        /// </summary>
        void Initialize();

        /// <summary>
        /// 开始采集（连续流或触发模式）
        /// </summary>
        /// <param name="triggerMode">true=触发模式，false=连续模式</param>
        void StartGrabbing(bool triggerMode);

        /// <summary>
        /// 停止采集
        /// </summary>
        void StopGrabbing();

        /// <summary>
        /// 触发一次拍照（仅在触发模式下有效）
        /// </summary>
        void TriggerOnce();

        /// <summary>
        /// 图像到达事件（回调中触发，携带 BitmapSource）
        /// </summary>
        event Action<BitmapSource> ImageReceived;

        /// <summary>
        /// 日志事件
        /// </summary>
        event Action<string> LogMessage;
    }
}