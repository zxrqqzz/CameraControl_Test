using CameraControl.Models;
using System;
using System.Windows.Media.Imaging;

namespace CameraControl.Services
{
    /// <summary>
    /// 相机采集服务接口(一台相机一条采集通道)。
    ///
    /// 目前有两个实现:
    /// - <see cref="HikCameraService"/>: 海康 MVS SDK(GigE Vision / USB3 Vision), 功能完整。
    /// - <see cref="UvcCameraService"/>: UVC / 笔记本摄像头, 采集通道预留，当前只做设备记录。
    /// </summary>
    public interface ICameraService : IDisposable
    {
        /// <summary>相机是否已连接</summary>
        bool IsConnected { get; }

        /// <summary>是否正在取流</summary>
        bool IsGrabbing { get; }

        /// <summary>该通道是否已经具备取流能力(未实现的通道返回 false)</summary>
        bool SupportsStreaming { get; }

        /// <summary>初始化(SDK 初始化、枚举设备)</summary>
        void Initialize();

        /// <summary>连接相机(创建/打开设备，可反复调用)</summary>
        bool Connect();

        /// <summary>
        /// 按设备列表中的相机连接(device 为 null 时等价于 <see cref="Connect()"/>)。
        /// 实现方需要根据 SerialNumber / DevicePath / Ip 在自己的枚举结果里找回设备。
        /// </summary>
        bool Connect(CameraDevice? device);

        /// <summary>断开相机(停止取流并关闭设备，可再次连接)</summary>
        void Disconnect();

        /// <summary>
        /// 开始取流
        /// </summary>
        /// <param name="triggerMode">true=软触发模式(配合 TriggerOnce 使用)，false=连续模式</param>
        bool StartGrabbing(bool triggerMode);

        /// <summary>停止取流</summary>
        void StopGrabbing();

        /// <summary>软触发拍照一次(仅在软触发取流状态下有效)</summary>
        void TriggerOnce();

        /// <summary>
        /// 同步阻塞获取一帧图像(需处于取流状态，不经过回调)
        /// </summary>
        /// <param name="timeoutMs">等待超时(毫秒)</param>
        BitmapSource? AcquireOneFrame(uint timeoutMs = 1000);

        /// <summary>单次采集: 自动开流取一帧显示，完成后恢复原状态</summary>
        void CaptureOnce();

        /// <summary>连续采集开关: 未取流则开始连续取流，正在取流则停止</summary>
        void ToggleContinuousCapture();

        /// <summary>图像到达事件(Bitmap 已 Freeze，可跨线程使用)</summary>
        event Action<BitmapSource>? ImageReceived;

        /// <summary>日志事件</summary>
        event Action<string>? LogMessage;
    }
}
