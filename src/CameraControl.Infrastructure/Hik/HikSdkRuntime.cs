using MvCameraControl;

namespace CameraControl.Infrastructure.Hik;

/// <summary>
/// 海康 MVS SDK 的引用计数式初始化封装。
///
/// 采集与枚举共用同一个 SDK 实例：只有最后一个使用者释放时才真正 Finalize，
/// 避免"扫描一次就把正在取流的相机注销掉"。
/// </summary>
public static class HikSdkRuntime
{
    private static readonly object Sync = new();
    private static int _refCount;
    private static bool _initialized;

    public static bool IsInitialized
    {
        get { lock (Sync) return _initialized; }
    }

    /// <summary>申请一次使用权；返回 0 表示成功，其它为 SDK 错误码。</summary>
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

    /// <summary>归还使用权；最后一个归还者触发反初始化。</summary>
    public static void Release()
    {
        lock (Sync)
        {
            if (_refCount > 0) _refCount--;
            if (_refCount > 0 || !_initialized) return;

            try
            {
                SDKSystem.Finalize();
            }
            catch
            {
                // 反初始化失败不应影响进程退出
            }
            finally
            {
                _initialized = false;
            }
        }
    }
}
