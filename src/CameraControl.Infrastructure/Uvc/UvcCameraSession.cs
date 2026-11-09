using CameraControl.Domain.Cameras;
using CameraControl.Domain.Capture;
using CameraControl.Domain.Ports;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace CameraControl.Infrastructure.Uvc;

/// <summary>
/// UVC（含笔记本内置摄像头）会话，基于 WinRT <see cref="MediaCapture"/>。
///
/// 实现选择：用 <c>CapturePhotoToStreamAsync</c> 抓单帧到**内存流**，
/// 再用 <c>BitmapDecoder.GetPixelDataAsync</c> 取托管像素数组。理由：
/// - 与本项目"即取即用单帧"的语义天然吻合；
/// - 不需要 COM 互操作（.NET 5+ 的 CsWinRT 不支持 <c>[ComImport]</c> 强转）；
/// - 代价是单帧延迟略高（约 100–400ms），连续预览需另加取流通道（P4+）。
///
/// **隐私约定（不可放宽）**：
/// - 画面只存在内存里，本类**不写任何文件**；
/// - 红外/Windows Hello 设备在 Provider 层就被拒绝打开；
/// - 程序关闭后不保留任何帧数据。
/// </summary>
public sealed class UvcCameraSession : ICameraSession
{
    private readonly MediaCapture _capture;
    private readonly CameraDescriptor _descriptor;
    private readonly IClock _clock;
    private bool _disposed;

    private UvcCameraSession(MediaCapture capture, CameraDescriptor descriptor, IClock clock)
    {
        _capture = capture;
        _descriptor = descriptor;
        _clock = clock;
    }

    public CameraId Id => _descriptor.Id;

    public CameraDescriptor Descriptor => _descriptor;

    public bool IsOpen => !_disposed;

    /// <summary>打开设备（共享只读，不独占摄像头）。</summary>
    public static async Task<UvcCameraSession> OpenAsync(
        string instanceId,
        CameraDescriptor descriptor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var capture = new MediaCapture();

        // 用 WinRT 自己枚举出真实设备 Id（手拼符号链接会得到 0x80070490）
        string deviceId = await ResolveDeviceIdAsync(instanceId, descriptor.DisplayName, cancellationToken)
            .ConfigureAwait(false);

        var settings = new MediaCaptureInitializationSettings
        {
            // 共享只读：不独占，会议软件等仍可使用同一摄像头
            SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            VideoDeviceId = deviceId
        };

        try
        {
            await capture.InitializeAsync(settings).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            capture.Dispose();
            throw TranslateOpenError(ex, deviceId);
        }

        return new UvcCameraSession(capture, descriptor, clock);
    }

    /// <summary>
    /// 解析 WinRT 设备 Id：先按实例路径末段匹配 <see cref="DeviceClass.VideoCapture"/>，
    /// 再按显示名匹配，最后才退回规则拼接。
    /// </summary>
    private static async Task<string> ResolveDeviceIdAsync(
        string instanceId,
        string displayName,
        CancellationToken cancellationToken)
    {
        string instanceTail = instanceId[(instanceId.LastIndexOf('\\') + 1)..];

        try
        {
            DeviceInformationCollection devices = await DeviceInformation
                .FindAllAsync(DeviceClass.VideoCapture)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            DeviceInformation? match = devices.FirstOrDefault(d =>
                d.Id.Contains(instanceTail, StringComparison.OrdinalIgnoreCase));

            match ??= devices.FirstOrDefault(d =>
                string.Equals(d.Name, displayName, StringComparison.OrdinalIgnoreCase));

            if (match is not null) return match.Id;
        }
        catch (Exception)
        {
            // 枚举失败就走回退路径
        }

        return ToDeviceInformationId(instanceId);
    }

    /// <summary>
    /// 注册表实例路径 → 相机接口符号链接（回退用）。
    /// 必须带相机接口类 GUID，否则 MediaCapture 返回 0x80070490。
    /// </summary>
    internal static string ToDeviceInformationId(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        const string cameraInterfaceClass = "{e5323777-f976-4f5b-9b55-b94699c46e44}";

        return @"\\?" + instanceId.Replace('\\', '#') + "#" + cameraInterfaceClass + @"\global";
    }

    /// <summary>
    /// 把 WinRT 的 HRESULT 翻译成可操作的错误。
    ///
    /// 注意：WinRT 的 Access Denied 抛的是 <see cref="Exception"/> 且 **Message 为空**，
    /// 只能靠 HResult 判断——不处理的话用户只会看到一句空话，这正是"设置全开却打不开"的由来。
    /// </summary>
    private static CameraOperationException TranslateOpenError(Exception ex, string deviceId)
    {
        const int AccessDenied = unchecked((int)0x80070005);
        const int NotFound = unchecked((int)0x80070490);
        const int DeviceInUse = unchecked((int)0x8007001F);

        int hr = ex.HResult;

        if (hr == AccessDenied || ex is UnauthorizedAccessException)
        {
            return new CameraOperationException(
                "UVC_ACCESS_DENIED",
                "无法访问摄像头（0x80070005 拒绝访问）。按顺序检查：" +
                "① 是否以管理员身份运行（Windows 会拒绝高完整性进程访问摄像头，请改用普通用户启动）；" +
                "② 系统设置 → 隐私 → 相机，是否允许桌面应用访问；" +
                "③ 是否被其它程序（会议软件）独占。",
                ex);
        }

        if (hr == NotFound)
        {
            return new CameraOperationException(
                "UVC_DEVICE_NOT_FOUND",
                $"摄像头不存在或无法枚举（0x80070490，设备 id={deviceId}）。",
                ex);
        }

        if (hr == DeviceInUse)
        {
            return new CameraOperationException(
                "UVC_DEVICE_BUSY",
                "摄像头被占用（0x8007001F）。请关闭正在使用它的程序后重试。",
                ex);
        }

        string message = string.IsNullOrWhiteSpace(ex.Message) ? $"HRESULT 0x{hr:X8}" : ex.Message;

        return new CameraOperationException(
            "UVC_OPEN_FAILED",
            $"打开 UVC 摄像头失败：{message}（设备 id={deviceId}）",
            ex);
    }

    public async Task<Frame> CaptureSingleAsync(CaptureOptions? options, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            // 全程内存：抓进 InMemoryRandomAccessStream，解码后即丢弃
            using var stream = new InMemoryRandomAccessStream();

            await _capture
                .CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), stream)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            stream.Seek(0);

            BitmapDecoder decoder = await BitmapDecoder
                .CreateAsync(stream)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            // GetPixelDataAsync 返回托管字节数组，无需 COM 指针操作
            PixelDataProvider data = await decoder
                .GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            byte[] pixels = data.DetachPixelData();

            int width = (int)decoder.PixelWidth;
            int height = (int)decoder.PixelHeight;
            int stride = width * 4;

            if (pixels.Length < stride * height)
                throw new CameraOperationException(
                    "UVC_BAD_BUFFER",
                    $"摄像头像素数据尺寸异常（{pixels.Length} < {stride * height}）。");

            var image = new FrameImage(width, height, stride, "Bgra32", pixels);

            return new Frame(
                FrameId: Guid.NewGuid().ToString("N"),
                CameraId: _descriptor.Id,
                TimestampUtc: _clock.UtcNow,
                OffsetFromGroup: TimeSpan.Zero,
                Image: image);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CameraOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string message = string.IsNullOrWhiteSpace(ex.Message) ? $"HRESULT 0x{ex.HResult:X8}" : ex.Message;
            throw new CameraOperationException("UVC_CAPTURE_FAILED", $"抓取画面失败：{message}", ex);
        }
    }

    // ── 触发：UVC 没有硬件触发；软件触发即"立即抓一帧" ────────────

    public Task ArmAsync(TriggerSpec trigger, CancellationToken cancellationToken)
    {
        if (trigger.Kind == TriggerSourceKind.Hardware)
        {
            throw new CameraOperationException(
                "TRIGGER_MODE_NOT_IMPLEMENTED",
                "UVC 摄像头不支持硬件触发（没有触发输入线）。");
        }

        return Task.CompletedTask;
    }

    public Task DisarmAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task TriggerSoftwareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ── 参数：UVC 能力因驱动而异，只做最小实现 ────────────────────

    public Task SetParameterAsync(string name, string value, CancellationToken cancellationToken) =>
        throw new CameraOperationException(
            "UVC_PARAMETER_UNSUPPORTED",
            $"UVC 摄像头不支持通过本程序设置参数 '{name}'（请在系统相机设置里调整）。");

    public Task<string?> GetParameterAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        try
        {
            _capture.Dispose();
        }
        catch (Exception)
        {
            // 设备可能已拔出
        }

        return ValueTask.CompletedTask;
    }
}
