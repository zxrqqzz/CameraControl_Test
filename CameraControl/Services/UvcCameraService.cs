using CameraControl.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace CameraControl.Services
{
    /// <summary>
    /// UVC / USB 摄像头(含笔记本自带摄像头)的采集通道。
    ///
    /// 实现方式: Windows.Media.Capture + MediaFrameReader(WinRT)，
    /// 以 <see cref="MediaCaptureSharingMode.SharedReadOnly"/> 只读共享模式打开设备，
    /// 不会独占摄像头，也不会抢走 Windows Hello 的使用权。
    ///
    /// ── 隐私约束（用户要求，不要破坏）──
    /// 笔记本自带摄像头可能拍到人脸，画面**只允许存在于内存**：
    /// - 不写磁盘、不建临时文件、不序列化、不把像素数据写进日志；
    /// - 只保留最新一帧，旧帧立即被覆盖；
    /// - <see cref="StopGrabbing"/> / <see cref="Disconnect"/> / <see cref="Dispose"/> 时立刻清空引用；
    /// - 程序退出后内存随进程消失。
    /// 任何"保存图像"的功能都必须先经 <see cref="ImagePersistenceGuard"/> 授权。
    ///
    /// 红外(IR)摄像头仅用于 Windows Hello 人脸解锁，本程序不尝试取流。
    /// </summary>
    public class UvcCameraService : ICameraService
    {
        /// <summary>单次采集等待首帧的超时(毫秒)</summary>
        private const int FirstFrameTimeoutMs = 5000;

        private readonly object _sync = new object();
        private readonly object _frameSync = new object();

        private CameraDevice? _device;
        private string? _mediaDeviceId;

        private MediaCapture? _capture;
        private MediaFrameReader? _reader;

        /// <summary>仅内存的最后帧缓冲，绝不落盘。</summary>
        private BitmapSource? _lastFrame;

        private TaskCompletionSource<bool>? _firstFrameSignal;

        // ────────────────────────────────────────────────────────────────
        // 状态
        // ────────────────────────────────────────────────────────────────

        public bool IsConnected => _capture != null;

        public bool IsGrabbing
        {
            get { lock (_sync) return _reader != null; }
        }

        /// <summary>UVC 通道现在具备真实取流能力</summary>
        public bool SupportsStreaming => true;

        public event Action<BitmapSource>? ImageReceived;

        public event Action<string>? LogMessage;

        /// <summary>当前绑定的设备</summary>
        public CameraDevice? Device => _device;

        /// <summary>内存里是否还留着一帧(用于验证隐私清理是否生效)</summary>
        public bool HasBufferedFrame
        {
            get { lock (_frameSync) return _lastFrame != null; }
        }

        /// <summary>当前帧的分辨率描述(未取流时为空)</summary>
        public string FrameSizeText { get; private set; } = string.Empty;

        public void Initialize()
        {
            Log("UVC 通道已就绪(Windows.Media.Capture, 共享只读模式)。");
        }

        // ────────────────────────────────────────────────────────────────
        // 连接 / 断开
        // ────────────────────────────────────────────────────────────────

        public bool Connect() => Connect(null);

        public bool Connect(CameraDevice? device)
        {
            if (device == null)
            {
                Log("UVC 通道需要指定具体设备。");
                return false;
            }

            // 红外摄像头只给人脸解锁用，不取流
            if (device.IsInfraredOnly)
            {
                _device = device;
                Log($"「{device.Name}」是红外摄像头，仅用于 Windows Hello 人脸解锁，本程序不取流。");
                return false;
            }

            // 高完整性(管理员)进程会被 Windows 拒绝访问摄像头，提前说清楚，省得用户去查隐私设置
            if (IsRunningElevated())
            {
                Log("[重要] 当前程序以管理员身份运行，Windows 会拒绝高完整性进程访问摄像头（错误 0x80070005）。");
                Log("[重要] 请关闭程序，用普通用户身份（直接双击）重新启动。");
            }

            try
            {
                // Release 模式下设备可能被其它程序占用，先断开旧的
                DisconnectInternal();

                string? mediaId = FindMediaDeviceId(device);
                if (mediaId == null)
                {
                    Log($"未在系统里找到摄像头「{device.Name}」，可能已被拔出或被禁用。");
                    return false;
                }

                _mediaDeviceId = mediaId;
                _device = device;
                _capture = new MediaCapture();
                FrameSizeText = string.Empty;

                Log($"UVC 设备已连接: {device.Name}");
                Log("[隐私] " + ImagePersistenceGuard.DescribePolicy(device));
                return true;
            }
            catch (Exception e)
            {
                Log($"连接 UVC 设备失败: {e.Message}");
                DisconnectInternal();
                return false;
            }
        }

        /// <summary>在系统摄像头列表里按名称/实例路径找回 Windows 设备 Id</summary>
        private static string? FindMediaDeviceId(CameraDevice device)
        {
            var devices = DeviceInformation
                .FindAllAsync(DeviceClass.VideoCapture)
                .AsTask().ConfigureAwait(false).GetAwaiter().GetResult();

            string name = device.Name ?? string.Empty;
            string instance = device.DevicePath ?? string.Empty;

            // 1) 友好名精确匹配
            foreach (DeviceInformation info in devices)
            {
                if (string.Equals(info.Name, name, StringComparison.OrdinalIgnoreCase))
                    return info.Id;
            }

            // 2) 用设备实例路径(USB\VID_..&PID_..&MI_..\...)匹配符号链接
            if (instance.Length > 0)
            {
                string normalizedInstance = instance.Trim('\\');
                foreach (DeviceInformation info in devices)
                {
                    string normalizedId = NormalizeDeviceId(info.Id);
                    if (normalizedId.EndsWith(normalizedInstance, StringComparison.OrdinalIgnoreCase))
                        return info.Id;
                }
            }

            // 3) 只有一块摄像头时退回第一块
            if (devices.Count == 1) return devices[0].Id;

            return null;
        }

        /// <summary>把 "\\\\?\\USB#VID_..#6&amp;..#{guid}" 规整成 "USB\VID_..\6&amp;.."</summary>
        private static string NormalizeDeviceId(string id)
        {
            string text = id ?? string.Empty;
            if (text.StartsWith(@"\\?\", StringComparison.Ordinal)) text = text.Substring(4);

            int brace = text.IndexOf("#{", StringComparison.Ordinal);
            if (brace > 0) text = text.Substring(0, brace);

            return text.Replace('#', '\\').Trim('\\');
        }

        public void Disconnect()
        {
            if (_device != null) Log($"UVC 设备已断开: {_device.Name}");
            DisconnectInternal();
        }

        private void DisconnectInternal()
        {
            StopGrabbing();

            try { _capture?.Dispose(); } catch { }
            _capture = null;
            _mediaDeviceId = null;

            ClearFrameBuffer();
            FrameSizeText = string.Empty;
        }

        // ────────────────────────────────────────────────────────────────
        // 取流
        // ────────────────────────────────────────────────────────────────

        public bool StartGrabbing(bool triggerMode)
        {
            if (_capture == null || _mediaDeviceId == null)
            {
                Log("UVC 设备未连接，无法开始取流。");
                return false;
            }

            lock (_sync)
            {
                if (_reader != null) return true;   // 已在取流
            }

            // 取流是异步的，但 ICameraService 是同步接口；
            // 所有 await 都用 ConfigureAwait(false)，避免阻塞 UI 线程时死锁。
            return StartGrabbingAsync(_mediaDeviceId).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private async Task<bool> StartGrabbingAsync(string mediaDeviceId)
        {
            try
            {
                var settings = new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = mediaDeviceId,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    // 共享只读: 不独占设备，不会让 Windows Hello 之类的程序失效
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly
                };

                await _capture!.InitializeAsync(settings).AsTask().ConfigureAwait(false);

                MediaFrameSource? frameSource = _capture.FrameSources.Values
                    .FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
                    ?? _capture.FrameSources.Values.FirstOrDefault();

                if (frameSource == null)
                {
                    Log("该摄像头没有可用的视频源。");
                    return false;
                }

                MediaFrameReader reader = await _capture
                    .CreateFrameReaderAsync(frameSource, MediaEncodingSubtypes.Bgra8)
                    .AsTask().ConfigureAwait(false);

                reader.FrameArrived += OnFrameArrived;

                MediaFrameReaderStartStatus status =
                    await reader.StartAsync().AsTask().ConfigureAwait(false);

                if (status != MediaFrameReaderStartStatus.Success)
                {
                    Log($"开始取流失败: {status}");
                    reader.FrameArrived -= OnFrameArrived;
                    try { reader.Dispose(); } catch { }
                    return false;
                }

                lock (_sync) _reader = reader;

                MediaFrameFormat? format = frameSource.CurrentFormat;
                string formatText = format?.VideoFormat is VideoMediaFrameFormat videoFormat
                    ? $"{videoFormat.Width}×{videoFormat.Height}"
                    : (format?.Subtype ?? "未知格式");

                Log($"连续采集已开始（{formatText}）。");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Log("打开摄像头被系统拒绝（0x80070005）。按下面顺序排查：");
                Log("  1) 设置 → 隐私和安全性 → 相机：确认「相机访问」和「允许桌面应用访问你的相机」都是开；");
                Log("  2) 联想 Vantage / Smart Appearance 里的「摄像头隐私模式」是否开启；");
                Log("  3) 笔记本的物理摄像头挡板 / Fn 快捷键是否关了摄像头；");
                Log("  4) 摄像头是否被其它程序（会议软件、Windows Hello 等）独占；");
                Log("  5) 先用系统自带的「相机」应用测一下：它也打不开就说明是机器层面限制。");
                return false;
            }
            catch (Exception e)
            {
                Log($"开始取流异常: {e.Message}");
                return false;
            }
        }

        public void StopGrabbing()
        {
            MediaFrameReader? reader;
            lock (_sync)
            {
                reader = _reader;
                _reader = null;
            }

            if (reader != null)
            {
                try
                {
                    reader.FrameArrived -= OnFrameArrived;
                    reader.StopAsync().AsTask().Wait(2000);
                }
                catch { }
                finally
                {
                    try { reader.Dispose(); } catch { }
                    Log("采集已停止。");
                }
            }

            // 无论之前有没有在取流，都要丢掉内存里的帧(隐私要求)
            ClearFrameBuffer();
        }

        /// <summary>帧回调(线程池线程): SoftwareBitmap → BitmapSource(已 Freeze) → 仅内存缓冲</summary>
        private unsafe void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            MediaFrameReference? frame = null;
            SoftwareBitmap? software = null;
            SoftwareBitmap? converted = null;

            try
            {
                frame = sender.TryAcquireLatestFrame();
                if (frame == null) return;

                software = frame.VideoMediaFrame?.SoftwareBitmap;
                if (software == null) return;

                if (software.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                {
                    converted = SoftwareBitmap.Convert(software, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    if (converted == null) return;
                    software = converted;
                }

                BitmapSource? bitmap = ToBitmapSource(software);
                if (bitmap == null) return;

                PushFrame(bitmap);
            }
            catch (Exception e)
            {
                Log("处理视频帧异常: " + e.Message);
            }
            finally
            {
                converted?.Dispose();
                frame?.Dispose();
            }
        }

        /// <summary>把 Bgra8 的 SoftwareBitmap 复制成 WPF BitmapSource(纯内存)</summary>
        private static unsafe BitmapSource? ToBitmapSource(SoftwareBitmap bitmap)
        {
            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            if (width <= 0 || height <= 0) return null;

            using BitmapBuffer buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
            BitmapPlaneDescription plane = buffer.GetPlaneDescription(0);
            int stride = plane.Stride > 0 ? plane.Stride : width * 4;

            int required = stride * height;
            byte[] pixels = new byte[required];

            using IMemoryBufferReference reference = buffer.CreateReference();
            ((IMemoryBufferByteAccess)(object)reference).GetBuffer(out byte* source, out uint capacity);

            int copy = Math.Min(required, (int)capacity);
            if (copy <= 0) return null;
            Marshal.Copy((IntPtr)source, pixels, 0, copy);

            var image = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            image.Freeze();
            return image;
        }

        // ────────────────────────────────────────────────────────────────
        // 采集命令
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 接收一帧并放进内存缓冲。只做内存持有 + 事件转发，不做任何持久化。
        /// </summary>
        public void PushFrame(BitmapSource frame)
        {
            if (frame == null) return;

            lock (_frameSync)
                _lastFrame = frame;      // 只保留最新一帧，旧帧立即可被 GC 回收

            FrameSizeText = $"{frame.PixelWidth}×{frame.PixelHeight}";

            ImageReceived?.Invoke(frame);
            _firstFrameSignal?.TrySetResult(true);
        }

        /// <summary>清空内存中的帧缓冲</summary>
        public void ClearFrameBuffer()
        {
            lock (_frameSync)
                _lastFrame = null;
        }

        /// <summary>单次采集: 未取流则临时开流，等到一帧后停止</summary>
        public void CaptureOnce()
        {
            if (_capture == null)
            {
                Log("UVC 设备未连接，无法单次采集。");
                return;
            }

            bool startedHere = false;
            if (!IsGrabbing)
            {
                if (!StartGrabbing(false)) return;
                startedHere = true;
            }

            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _firstFrameSignal = signal;

            bool got = signal.Task.Wait(FirstFrameTimeoutMs);

            _firstFrameSignal = null;
            if (got) Log("单次采集完成。");
            else Log("单次采集超时，未收到画面。");

            if (startedHere) StopGrabbing();
        }

        public void ToggleContinuousCapture()
        {
            if (_capture == null)
            {
                Log("UVC 设备未连接，无法连续采集。");
                return;
            }

            if (IsGrabbing) StopGrabbing();
            else StartGrabbing(false);
        }

        /// <summary>同步取最近一帧(不阻塞等新帧)</summary>
        public BitmapSource? AcquireOneFrame(uint timeoutMs = 1000)
        {
            lock (_frameSync)
                return _lastFrame;
        }

        public void TriggerOnce()
        {
            Log("UVC 摄像头不支持软触发，请使用「单次采集」。");
        }

        public void Dispose()
        {
            Disconnect();
            _device = null;

            // 隐私: 进程退出前显式丢掉帧引用
            ClearFrameBuffer();
        }

        // ────────────────────────────────────────────────────────────────

        private void Log(string message) => LogMessage?.Invoke(message);

        /// <summary>
        /// 当前进程是否以管理员(高完整性)身份运行。
        /// Windows 会拒绝高完整性进程访问摄像头，这是 UVC 打不开的常见原因。
        /// </summary>
        private static bool IsRunningElevated()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>读取 SoftwareBitmap 像素缓冲所需的 COM 接口</summary>
        [ComImport]
        [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private unsafe interface IMemoryBufferByteAccess
        {
            void GetBuffer(out byte* buffer, out uint capacity);
        }
    }
}
