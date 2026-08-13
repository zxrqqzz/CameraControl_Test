//using MvCameraControl;
//using System;
//using System.Windows.Interop;
//using System.Windows.Media;
//using System.Windows.Media.Imaging;

//namespace CameraControl.Services
//{
//    public class HikCameraService : ICameraService
//    {
//        private IDevice _camera;
//        private bool _isGrabbing;
//        private readonly object _lock = new object();

//        public event Action<BitmapSource> ImageReceived;
//        public event Action<string> LogMessage;

//        public void Initialize()
//        {
//            try
//            {
//                LogMessage?.Invoke("初始化 SDK...");
//                int ret = SDKSystem.Initialize();
//                if (ret != MvError.MV_OK)
//                {
//                    LogMessage?.Invoke($"SDK 初始化失败，错误码: {ret:X}");
//                    return;
//                }

//                // 枚举设备
//                List<IDeviceInfo> deviceInfos;
//                ret = DeviceEnumerator.EnumDevices(
//                    DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice,
//                    out deviceInfos);
//                if (ret != MvError.MV_OK || deviceInfos.Count == 0)
//                {
//                    LogMessage?.Invoke("未找到相机！");
//                    return;
//                }

//                // 选择第一个相机
//                var deviceInfo = deviceInfos[0];
//                _camera = DeviceFactory.CreateDevice(deviceInfo);
//                ret = _camera.Open();
//                if (ret != MvError.MV_OK)
//                {
//                    LogMessage?.Invoke($"打开相机失败，错误码: {ret:X}");
//                    _camera = null;
//                    return;
//                }

//                // 如果是 GigE 相机，设置最佳包大小
//                if (_camera is IGigEDevice gigECamera)
//                {
//                    gigECamera.GetOptimalPacketSize(out int packetSize);
//                    _camera.Parameters.SetIntValue("GevSCPSPacketSize", packetSize);
//                }

//                // 注册图像回调
//                _camera.StreamGrabber.FrameGrabedEvent += OnFrameGrabed;

//                LogMessage?.Invoke("相机初始化成功！");
//            }
//            catch (Exception ex)
//            {
//                LogMessage?.Invoke($"初始化异常: {ex.Message}");
//            }
//        }

//        public void StartGrabbing(bool triggerMode)
//        {
//            if (_camera == null)
//            {
//                LogMessage?.Invoke("相机未初始化！");
//                return;
//            }

//            lock (_lock)
//            {
//                if (_isGrabbing)
//                {
//                    LogMessage?.Invoke("采集已启动，请勿重复操作");
//                    return;
//                }

//                // 设置触发模式
//                if (triggerMode)
//                {
//                    _camera.Parameters.SetEnumValueByString("TriggerSource", "Software");
//                    _camera.Parameters.SetEnumValueByString("TriggerMode", "On");
//                }
//                else
//                {
//                    _camera.Parameters.SetEnumValueByString("TriggerMode", "Off");
//                }

//                // 设置缓存节点数
//                _camera.StreamGrabber.SetImageNodeNum(5);

//                // 开始采集
//                int ret = _camera.StreamGrabber.StartGrabbing();
//                if (ret != MvError.MV_OK)
//                {
//                    LogMessage?.Invoke($"开始采集失败，错误码: {ret:X}");
//                    return;
//                }

//                _isGrabbing = true;
//                LogMessage?.Invoke($"采集已启动（触发模式：{triggerMode}）");
//            }
//        }

//        public void StopGrabbing()
//        {
//            lock (_lock)
//            {
//                if (!_isGrabbing || _camera == null) return;

//                int ret = _camera.StreamGrabber.StopGrabbing();
//                if (ret != MvError.MV_OK)
//                {
//                    LogMessage?.Invoke($"停止采集失败，错误码: {ret:X}");
//                }
//                else
//                {
//                    _isGrabbing = false;
//                    LogMessage?.Invoke("采集已停止");
//                }
//            }
//        }

//        public void TriggerOnce()
//        {
//            if (_camera == null || !_isGrabbing)
//            {
//                LogMessage?.Invoke("相机未就绪或未开始采集，无法触发");
//                return;
//            }

//            int ret = _camera.Parameters.SetCommandValue("TriggerSoftware");
//            if (ret != MvError.MV_OK)
//            {
//                LogMessage?.Invoke($"触发失败，错误码: {ret:X}");
//            }
//            else
//            {
//                LogMessage?.Invoke("触发拍照成功");
//            }
//        }

//        private void OnFrameGrabed(object sender, FrameGrabbedEventArgs e)
//        {
//            try
//            {
//                // 获取图像数据
//                var image = e.FrameOut.Image;
//                // 转换为 BitmapSource（深拷贝，避免后续释放影响）
//                var bitmap = image.ToBitmap();
//                var bitmapSource = ConvertBitmapToBitmapSource(bitmap);

//                // 触发事件，让 ViewModel 更新 UI
//                ImageReceived?.Invoke(bitmapSource);

//                // 释放图像缓存（必须）
//                _camera.StreamGrabber.FreeImageBuffer(e.FrameOut);
//            }
//            catch (Exception ex)
//            {
//                LogMessage?.Invoke($"回调处理异常: {ex.Message}");
//            }
//        }

//        private BitmapSource ConvertBitmapToBitmapSource(System.Drawing.Bitmap bitmap)
//        {
//            var hBitmap = bitmap.GetHbitmap();
//            try
//            {
//                return Imaging.CreateBitmapSourceFromHBitmap(
//                    hBitmap,
//                    IntPtr.Zero,
//                    System.Windows.Int32Rect.Empty,
//                    BitmapSizeOptions.FromEmptyOptions());
//            }
//            finally
//            {
//                // 释放 GDI 句柄
//                Gdi32.DeleteObject(hBitmap);
//            }
//        }

//        // 辅助方法：释放 GDI 对象
//        private static class Gdi32
//        {
//            [System.Runtime.InteropServices.DllImport("gdi32.dll")]
//            public static extern bool DeleteObject(IntPtr hObject);
//        }

//        public void Dispose()
//        {
//            StopGrabbing();
//            if (_camera != null)
//            {
//                _camera.StreamGrabber.FrameGrabedEvent -= OnFrameGrabed;
//                _camera.Close();
//                _camera.Dispose();
//                _camera = null;
//            }
//            SDKSystem.Finalize();
//            LogMessage?.Invoke("相机资源已释放");
//        }
//    }
//}