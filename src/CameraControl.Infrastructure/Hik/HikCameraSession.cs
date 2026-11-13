using System.Diagnostics;
using CameraControl.Application.Capture;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Capture;
using CameraControl.Domain.Ports;
using MvCameraControl;

namespace CameraControl.Infrastructure.Hik;

/// <summary>
/// 一台海康相机的会话。
///
/// 帧在回调里立即复制成托管字节数组并归还 SDK 缓存，
/// 因此不会因为上层处理慢而占住 SDK 的帧队列。
/// </summary>
public sealed class HikCameraSession : ICameraSession
{
    /// <summary>
    /// SDK 帧队列深度。
    /// TODO-REALDATA: 5 是**经验值，未用真实相机压测**。太小会丢帧，太大增加延迟与内存占用。
    /// </summary>
    private const uint ImageNodeNum = 5;

    /// <summary>
    /// 单帧采集默认超时。
    /// TODO-REALDATA: 3000ms 是**经验值，未用真实相机实测**。需覆盖曝光 + 传输 + 主机调度；
    /// GigE 长曝光或大分辨率场景可能不足，短曝光相机则可显著收紧。
    /// </summary>
    private const int DefaultCaptureTimeoutMs = 3000;

    private readonly IDevice _device;
    private readonly IClock _clock;
    private readonly Action _onDispose;
    private readonly object _sync = new();

    private TaskCompletionSource<Frame>? _pending;
    private int _frameSequence;
    private bool _callbackRegistered;
    private bool _grabbing;
    private bool _disposed;
    private bool _disposeNotified;

    public HikCameraSession(
        IDevice device,
        CameraDescriptor descriptor,
        IClock clock,
        Action onDispose)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(onDispose);

        _device = device;
        Descriptor = descriptor;
        Id = descriptor.Id;
        _clock = clock;
        _onDispose = onDispose;
    }

    public CameraId Id { get; }

    public CameraDescriptor Descriptor { get; }

    public bool IsOpen => !_disposed && _device.IsConnected;

    // ────────────────────────────────────────────────────────────────
    // 参数
    // ────────────────────────────────────────────────────────────────

    public Task SetParameterAsync(string name, string value, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        int ret = _device.Parameters.SetEnumValueByString(name, value);
        if (ret != MvError.MV_OK)
        {
            throw new CameraOperationException(
                "CAMERA_PARAMETER_SET_FAILED",
                $"设置参数 {name}={value} 失败：0x{ret:X}。");
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetParameterAsync(string name, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        int ret = _device.Parameters.GetStringValue(name, out IStringValue? value);
        return Task.FromResult(ret == MvError.MV_OK ? value?.CurValue : null);
    }

    // ────────────────────────────────────────────────────────────────
    // 武装 / 触发
    // ────────────────────────────────────────────────────────────────

    public Task ArmAsync(TriggerSpec trigger, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(trigger);

        if (trigger.Kind == TriggerSourceKind.Hardware)
        {
            throw new CameraOperationException(
                CaptureErrorCodes.ModeNotImplemented,
                "海康适配器的硬件触发（Line0 外部脉冲）属于 P2 能力，当前版本未实现。");
        }

        IParameters p = _device.Parameters;
        EnsureOk(p.SetEnumValueByString("TriggerSelector", "FrameStart"), "TriggerSelector");
        EnsureOk(p.SetEnumValueByString("TriggerSource", "Software"), "TriggerSource");
        EnsureOk(p.SetEnumValueByString("TriggerMode", "On"), "TriggerMode");

        StartGrabbingCore();
        return Task.CompletedTask;
    }

    public Task DisarmAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return Task.CompletedTask;

        try
        {
            _device.Parameters.SetEnumValueByString("TriggerMode", "Off");
        }
        catch
        {
            // 相机可能已断开，解除武装失败不抛
        }

        StopGrabbingCore();
        return Task.CompletedTask;
    }

    public Task TriggerSoftwareAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        int ret = _device.Parameters.SetCommandValue("TriggerSoftware");
        if (ret != MvError.MV_OK)
        {
            throw new CameraOperationException(
                "TRIGGER_FAILED",
                $"软件触发失败：0x{ret:X}。相机是否已武装？");
        }

        return Task.CompletedTask;
    }

    // ────────────────────────────────────────────────────────────────
    // 采集
    // ────────────────────────────────────────────────────────────────

    public async Task<Frame> CaptureSingleAsync(CaptureOptions? options, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        bool startedHere = false;
        if (!_grabbing)
        {
            // 未武装：临时开流取一帧
            IParameters p = _device.Parameters;
            p.SetEnumValueByString("TriggerMode", "Off");
            StartGrabbingCore();
            startedHere = true;
        }

        try
        {
            return await WaitNextFrameAsync(DefaultCaptureTimeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (startedHere) StopGrabbingCore();
        }
    }

    private async Task<Frame> WaitNextFrameAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_sync)
        {
            _pending = tcs;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            await using CancellationTokenRegistration registration =
                timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token));

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CameraOperationException(
                CaptureErrorCodes.CaptureTimeout,
                $"等待相机帧超时（{timeoutMs}ms）。");
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pending, tcs)) _pending = null;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 取流
    // ────────────────────────────────────────────────────────────────

    private void StartGrabbingCore()
    {
        lock (_sync)
        {
            if (_grabbing) return;
        }

        if (!_callbackRegistered)
        {
            _device.StreamGrabber.FrameGrabedEvent += OnFrameGrabbed;
            _callbackRegistered = true;
        }

        _device.StreamGrabber.SetImageNodeNum(ImageNodeNum);

        int ret = _device.StreamGrabber.StartGrabbing();
        if (ret != MvError.MV_OK)
        {
            throw new CameraOperationException(
                "GRABBING_START_FAILED",
                $"开始取流失败：0x{ret:X}。");
        }

        lock (_sync)
        {
            _grabbing = true;
        }
    }

    private void StopGrabbingCore()
    {
        lock (_sync)
        {
            if (!_grabbing) return;
            _grabbing = false;
        }

        try
        {
            _device.StreamGrabber.StopGrabbing();
        }
        catch (Exception ex)
        {
            // 断开时停止取流失败可忽略
            Debug.WriteLine($"停止取流失败（{Id.Value}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 帧回调：立刻复制像素并归还 SDK 缓存。
    /// 这里不能做耗时操作，否则会阻塞取流线程。
    /// </summary>
    private void OnFrameGrabbed(object? sender, FrameGrabbedEventArgs e)
    {
        IFrameOut? frameOut = e.FrameOut;
        try
        {
            if (frameOut?.Image is null) return;

            FrameImage image = CopyPixels(frameOut.Image);

            var frame = new Frame(
                FrameId: $"{Id.Value}-{Interlocked.Increment(ref _frameSequence)}",
                CameraId: Id,
                TimestampUtc: _clock.UtcNow,
                OffsetFromGroup: TimeSpan.Zero,
                Image: image);

            lock (_sync)
            {
                _pending?.TrySetResult(frame);
            }
        }
        catch (Exception ex)
        {
            // 回调里的异常不能抛回 SDK
            Debug.WriteLine($"帧回调处理失败：{ex.Message}");
        }
        finally
        {
            try
            {
                if (frameOut is not null) _device.StreamGrabber.FreeImageBuffer(frameOut);
            }
            catch
            {
                // 归还失败无补救手段
            }
        }
    }

    /// <summary>把 SDK 图像复制成托管像素（必要时先转 RGB8）。</summary>
    private FrameImage CopyPixels(IImage source)
    {
        (int width, int height, string format, byte[] data, int stride) = Extract(source);
        return new FrameImage(width, height, stride, format, data);
    }

    private (int Width, int Height, string Format, byte[] Data, int Stride) Extract(IImage image)
    {
        int width = (int)image.Width;
        int height = (int)image.Height;

        switch (image.PixelType)
        {
            case MvGvspPixelType.PixelType_Gvsp_Mono8:
                return (width, height, "Gray8", image.PixelData, width);

            case MvGvspPixelType.PixelType_Gvsp_RGB8_Packed:
                return (width, height, "Rgb24", image.PixelData, width * 3);

            case MvGvspPixelType.PixelType_Gvsp_BGR8_Packed:
                return (width, height, "Bgr24", image.PixelData, width * 3);

            case MvGvspPixelType.PixelType_Gvsp_BGRA8_Packed:
                return (width, height, "Bgra32", image.PixelData, width * 4);

            default:
                int ret = _device.PixelTypeConverter.ConvertPixelType(
                    image, out IImage converted, MvGvspPixelType.PixelType_Gvsp_RGB8_Packed);

                if (ret != MvError.MV_OK || converted is null)
                {
                    throw new CameraOperationException(
                        "PIXEL_CONVERT_FAILED",
                        $"像素格式转换失败：0x{ret:X}（{image.PixelType}）。");
                }

                try
                {
                    return (width, height, "Rgb24", converted.PixelData, width * 3);
                }
                finally
                {
                    try
                    {
                        converted.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"释放转换后的图像失败：{ex.Message}");
                    }
                }
        }
    }

    private static void EnsureOk(int ret, string name)
    {
        if (ret != MvError.MV_OK)
        {
            throw new CameraOperationException(
                "CAMERA_PARAMETER_SET_FAILED",
                $"设置相机参数 {name} 失败：0x{ret:X}。");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HikCameraSession));
    }

    // ────────────────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        StopGrabbingCore();

        if (_callbackRegistered)
        {
            try
            {
                _device.StreamGrabber.FrameGrabedEvent -= OnFrameGrabbed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"注销帧回调失败：{ex.Message}");
            }

            _callbackRegistered = false;
        }

        // 释放顺序不变：先关流（StopGrabbingCore，已在上面），再 Close，最后 Dispose。
        try
        {
            _device.Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"关闭相机失败：{ex.Message}");
        }

        try
        {
            _device.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"释放相机句柄失败：{ex.Message}");
        }

        if (!_disposeNotified)
        {
            _disposeNotified = true;

            try
            {
                _onDispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"相机会话释放回调失败：{ex.Message}");
            }
        }

        return ValueTask.CompletedTask;
    }
}
