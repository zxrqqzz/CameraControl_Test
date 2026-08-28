using MvCameraControl;
using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CameraControl.Services
{
    /// <summary>
    /// 海康相机采集服务: 设备枚举/连接、取流控制、软触发、图像回调与像素格式转换全部封装于此，
    /// 界面层(MainViewModel)只做命令转发与结果显示。
    /// </summary>
    public class HikCameraService : ICameraService
    {
        // 相机IP与PC网卡IP
        private const string CameraIp = "192.168.1.10";
        private const string NetExportIp = "192.168.1.100";

        // 同步取一帧的默认等待时间(毫秒)
        private const uint GrabTimeoutMs = 1000;

        // 图像缓存队列深度
        private const uint ImageNodeNum = 5;

        private IDevice? _camera;
        private bool _sdkInitialized;
        private bool _callbackRegistered;
        private TaskCompletionSource<BitmapSource>? _captureTcs;

        public bool IsConnected => _camera != null && _camera.IsConnected;

        public bool IsGrabbing { get; private set; }

        /// <summary>SDK 参数接口(供参数设置界面动态读写属性)，未连接时为 null</summary>
        public IParameters? Parameters => _camera?.Parameters;

        public event Action<BitmapSource>? ImageReceived;

        public event Action<string>? LogMessage;

        /// <summary>
        /// SDK 初始化 + 枚举设备 + 连接
        /// </summary>
        public void Initialize()
        {
            if (!_sdkInitialized)
            {
                int ret = SDKSystem.Initialize();
                _sdkInitialized = ret == MvError.MV_OK;
                Log(_sdkInitialized ? "SDK 初始化完成" : $"SDK 初始化失败: 0x{ret:X}");
            }

            EnumDevices();
            Connect();
        }

        /// <summary>
        /// 枚举设备，获取设备信息
        /// </summary>
        private void EnumDevices()
        {
            List<IDeviceInfo> devInfoList;
            int ret = DeviceEnumerator.EnumDevicesEx(
                 DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice, "", out devInfoList);
            if (ret == MvError.MV_OK)
            {
                Log($"枚举设备成功，设备数量: {devInfoList.Count}");
                foreach (var dev in devInfoList)
                    Log($"型号: {dev.ModelName}, 厂商: {dev.ManufacturerName}, 序列号: {dev.SerialNumber}");
            }
            else
            {
                Log($"枚举设备失败: 0x{ret:X}");
            }
        }

        /// <summary>
        /// 连接相机(创建/打开设备，可反复调用)
        /// </summary>
        public bool Connect()
        {
            try
            {
                if (_camera == null)
                {
                    _camera = DeviceFactory.CreateDeviceByIp(CameraIp, NetExportIp);
                    Log($"已创建相机对象: {_camera}");
                }

                int ret = _camera.Open();
                if (ret != MvError.MV_OK)
                {
                    Log($"打开相机失败: 0x{ret:X}");
                    return false;
                }

                Log($"相机 {_camera} 连接成功");
                return true;
            }
            catch (Exception e)
            {
                Log($"打开相机失败: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 断开相机(停止取流并关闭设备，可再次连接)
        /// </summary>
        public void Disconnect()
        {
            try
            {
                if (_camera == null) return;

                StopGrabbing();

                int ret = _camera.Close();
                if (ret != MvError.MV_OK)
                    Log($"关闭相机返回: 0x{ret:X}");
                else
                    Log("相机已断开");
            }
            catch (Exception e)
            {
                Log($"断开相机异常: {e.Message}");
            }
        }

        /// <summary>
        /// 开始取流
        /// </summary>
        /// <param name="triggerMode">true=软触发模式(配合 TriggerOnce 使用)，false=连续模式</param>
        public bool StartGrabbing(bool triggerMode)
        {
            if (!IsConnected)
            {
                Log("相机未连接, 无法开始采集");
                return false;
            }
            if (IsGrabbing)
            {
                Log("采集已在进行中");
                return true;
            }

            try
            {
                var camera = _camera!;
                int ret;
                if (triggerMode)
                {
                    // 软触发: 触发源为软件指令, 由 TriggerOnce() 发出触发
                    camera.Parameters.SetEnumValueByString("TriggerSelector", "FrameStart");
                    ret = camera.Parameters.SetEnumValueByString("TriggerSource", "Software");
                    if (ret != MvError.MV_OK)
                    {
                        Log($"设置触发源失败: 0x{ret:X}");
                        return false;
                    }
                    ret = camera.Parameters.SetEnumValueByString("TriggerMode", "On");
                }
                else
                {
                    // 连续模式: 关闭触发, 相机自由连续出图
                    ret = camera.Parameters.SetEnumValueByString("TriggerMode", "Off");
                }
                if (ret != MvError.MV_OK)
                {
                    Log($"设置触发模式失败: 0x{ret:X}");
                    return false;
                }

                RegisterFrameCallback();

                ret = camera.StreamGrabber.SetImageNodeNum(ImageNodeNum);
                if (ret != MvError.MV_OK)
                    Log($"设置缓存节点数返回: 0x{ret:X}");

                ret = camera.StreamGrabber.StartGrabbing();
                if (ret != MvError.MV_OK)
                {
                    Log($"开启取流失败: 0x{ret:X}");
                    return false;
                }

                IsGrabbing = true;
                Log(triggerMode ? "已进入软触发采集模式" : "连续采集已开始");
                return true;
            }
            catch (Exception e)
            {
                Log($"开始采集异常: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 停止取流
        /// </summary>
        public void StopGrabbing()
        {
            try
            {
                if (_camera == null || !IsGrabbing) return;

                int ret = _camera.StreamGrabber.StopGrabbing();
                IsGrabbing = false;
                if (ret != MvError.MV_OK)
                    Log($"停止取流返回: 0x{ret:X}");
                else
                    Log("采集已停止");
            }
            catch (Exception e)
            {
                IsGrabbing = false;
                Log($"停止采集异常: {e.Message}");
            }
        }

        /// <summary>
        /// 软触发拍照一次(仅在软触发取流状态下有效, 图像经回调送出)
        /// </summary>
        public void TriggerOnce()
        {
            if (_camera == null || !IsGrabbing)
            {
                Log("未处于采流状态, 无法软触发");
                return;
            }

            int ret = _camera.Parameters.SetCommandValue("TriggerSoftware");
            if (ret != MvError.MV_OK)
                Log($"软触发失败: 0x{ret:X}");
            else
                Log("软触发已发出");
        }

        /// <summary>
        /// 单次采集: 未取流则先以连续模式开流, 同步等待回调送来一帧后停止取流恢复原状态
        /// (不能与 AcquireOneFrame 的 GetImageBuffer 混用, 因为回调会先消费缓存帧导致取帧超时)
        /// </summary>
        public void CaptureOnce()
        {
            if (!IsConnected)
            {
                Log("相机未连接, 无法单次采集");
                return;
            }

            bool startedHere = false;
            if (!IsGrabbing)
            {
                if (!StartGrabbing(false)) return;
                startedHere = true;
            }

            var frame = WaitForFrameViaCallback(GrabTimeoutMs);
            if (frame != null)
            {
                // 帧已由回调通过 ImageReceived 送出
                Log("单次采集完成");
            }
            else
            {
                Log("单次采集未获取到图像");
            }

            if (startedHere)
                StopGrabbing();
        }

        /// <summary>
        /// 同步阻塞等待回调送出一帧(通过 TaskCompletionSource 实现),
        /// 返回的 BitmapSource 已由回调通过 ImageReceived 送出, 此处仅用于判断是否成功。
        /// </summary>
        private BitmapSource? WaitForFrameViaCallback(uint timeoutMs)
        {
            var tcs = new TaskCompletionSource<BitmapSource>();
            _captureTcs = tcs;
            try
            {
                if (tcs.Task.Wait(TimeSpan.FromMilliseconds(timeoutMs)))
                    return tcs.Task.Result;
            }
            finally
            {
                _captureTcs = null;
            }
            return null;
        }

        /// <summary>
        /// 连续采集开关: 未取流则开始连续取流, 正在取流则停止
        /// </summary>
        public void ToggleContinuousCapture()
        {
            if (!IsConnected)
            {
                Log("相机未连接, 无法连续采集");
                return;
            }

            if (IsGrabbing)
                StopGrabbing();
            else
                StartGrabbing(false);
        }

        /// <summary>
        /// 同步阻塞获取一帧图像并转为 BitmapSource(不经过回调, 需已 StartGrabbing)
        /// </summary>
        public BitmapSource? AcquireOneFrame(uint timeoutMs = 1000)
        {
            if (_camera == null || !IsGrabbing)
            {
                Log("请先开始采集再获取图像");
                return null;
            }

            IFrameOut? frameOut = null;
            try
            {
                int ret = _camera.StreamGrabber.GetImageBuffer(timeoutMs, out frameOut);
                if (ret != MvError.MV_OK || frameOut == null)
                {
                    Log($"获取图像超时或失败: 0x{ret:X}");
                    return null;
                }

                var bitmap = ConvertToBitmapSource(frameOut.Image);
                bitmap?.Freeze();
                if (bitmap != null)
                    Log($"已获取一帧: {frameOut.Image.Width}x{frameOut.Image.Height}, 帧号 {frameOut.FrameNum}");
                return bitmap;
            }
            catch (Exception e)
            {
                Log($"获取图像异常: {e.Message}");
                return null;
            }
            finally
            {
                // 必须归还缓存, 否则取流若干帧后无图可取
                try { if (frameOut != null) _camera.StreamGrabber.FreeImageBuffer(frameOut); } catch { }
            }
        }

        /// <summary>
        /// 注册图像回调(在开始取流前调用, 只需注册一次)
        /// </summary>
        private void RegisterFrameCallback()
        {
            if (_camera == null || _callbackRegistered) return;
            _camera.StreamGrabber.FrameGrabedEvent += OnFrameGrabed;
            _callbackRegistered = true;
        }

        /// <summary>
        /// 取流回调: 收到一帧 → 转 BitmapSource → 通过 ImageReceived 送出
        /// </summary>
        private void OnFrameGrabed(object? sender, FrameGrabbedEventArgs e)
        {
            try
            {
                var image = e.FrameOut?.Image;
                if (image == null) return;

                var bitmap = ConvertToBitmapSource(image);
                if (bitmap == null) return;
                bitmap.Freeze();

                ImageReceived?.Invoke(bitmap);
                _captureTcs?.TrySetResult(bitmap);
            }
            catch (Exception ex)
            {
                Log($"处理图像帧异常: {ex.Message}");
            }
            finally
            {
                try { if (_camera != null && e.FrameOut != null) _camera.StreamGrabber.FreeImageBuffer(e.FrameOut); } catch { }
            }
        }

        /// <summary>
        /// 将 SDK 图像数据转换为 WPF 的 BitmapSource:
        /// Mono8/RGB8/BGR8/BGRA8 直接映射; 其它格式(Bayer/16位/YUV等)先经 SDK 转成 RGB8 再显示
        /// </summary>
        private BitmapSource? ConvertToBitmapSource(IImage image)
        {
            if (image == null || image.Width == 0 || image.Height == 0) return null;

            int width = (int)image.Width;
            int height = (int)image.Height;

            PixelFormat? directFormat = image.PixelType switch
            {
                MvGvspPixelType.PixelType_Gvsp_Mono8 => PixelFormats.Gray8,
                MvGvspPixelType.PixelType_Gvsp_RGB8_Packed => PixelFormats.Rgb24,
                MvGvspPixelType.PixelType_Gvsp_BGR8_Packed => PixelFormats.Bgr24,
                MvGvspPixelType.PixelType_Gvsp_BGRA8_Packed => PixelFormats.Bgra32,
                _ => null,
            };

            if (directFormat is PixelFormat format)
            {
                byte[] data = image.PixelData;
                int stride = width * (format.BitsPerPixel / 8);
                return BitmapSource.Create(width, height, 96, 96, format, null, data, stride);
            }

            if (_camera == null)
            {
                Log($"不支持的像素格式且相机不可用: {image.PixelType}");
                return null;
            }

            IImage converted = null!;
            try
            {
                int ret = _camera.PixelTypeConverter.ConvertPixelType(
                    image, out converted, MvGvspPixelType.PixelType_Gvsp_RGB8_Packed);
                if (ret != MvError.MV_OK)
                {
                    Log($"像素格式转换失败: 0x{ret:X} ({image.PixelType})");
                    return null;
                }

                byte[] data = converted.PixelData;
                int stride = width * 3;
                return BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, data, stride);
            }
            finally
            {
                try { converted?.Dispose(); } catch { }
            }
        }

        /// <summary>日志输出</summary>
        private void Log(string message) => LogMessage?.Invoke(message);

        public void Dispose()
        {
            StopGrabbing();
            if (_callbackRegistered && _camera != null)
            {
                try { _camera.StreamGrabber.FrameGrabedEvent -= OnFrameGrabed; } catch { }
                _callbackRegistered = false;
            }
            try { _camera?.Dispose(); } catch { }
            _camera = null;

            if (_sdkInitialized)
            {
                SDKSystem.Finalize();
                _sdkInitialized = false;
            }
        }
    }
}
