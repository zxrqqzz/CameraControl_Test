using MvCameraControl;
using System;

namespace CameraControl.Services
{
    /// <summary>
    /// 海康 MVS SDK 的引用计数式初始化封装。
    ///
    /// 采集服务(<see cref="HikCameraService"/>)和扫描器(<see cref="HikDeviceScanner"/>)都要用 SDK，
    /// 如果各自 Initialize/Finalize，扫描一次就会把正在取流的相机一起注销掉。
    /// 这里统一做引用计数: 只有最后一个使用者释放时才真正调用 SDKSystem.Finalize()。
    /// </summary>
    public static class HikSdkRuntime
    {
        private static readonly object Sync = new object();
        private static int _refCount;
        private static bool _initialized;

        /// <summary>SDK 当前是否已初始化</summary>
        public static bool IsInitialized
        {
            get { lock (Sync) return _initialized; }
        }

        /// <summary>
        /// 申请一次 SDK 使用权。返回 <see cref="MvError.MV_OK"/> 表示成功，
        /// 其它值为 SDK 初始化失败的原始错误码。
        /// </summary>
        public static int Acquire()
        {
            lock (Sync)
            {
                if (!_initialized)
                {
                    int ret = SDKSystem.Initialize();
                    _initialized = ret == MvError.MV_OK;
                    if (!_initialized) return ret;
                }
                _refCount++;
                return MvError.MV_OK;
            }
        }

        /// <summary>归还一次使用权; 最后一个归还者触发 SDK 反初始化</summary>
        public static void Release()
        {
            lock (Sync)
            {
                if (_refCount > 0) _refCount--;
                if (_refCount > 0 || !_initialized) return;

                try { SDKSystem.Finalize(); }
                catch { /* 反初始化失败不影响进程退出 */ }
                finally { _initialized = false; }
            }
        }
    }
}
