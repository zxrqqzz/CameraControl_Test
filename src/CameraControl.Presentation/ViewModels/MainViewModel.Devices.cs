using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Capture;
using CameraControl.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CameraControl.Presentation.ViewModels;

/// <summary>设备块：枚举相机、连接 / 断开、单帧采集与预览。</summary>
public sealed partial class MainViewModel
{

    // ── 设备 ────────────────────────────────────────────────

    public ObservableCollection<CameraRowViewModel> Cameras { get; } = [];

    [ObservableProperty]
    private CameraRowViewModel? _selectedCamera;

    [ObservableProperty]
    private BitmapSource? _previewImage;

    [ObservableProperty]
    private string _imageInfo = string.Empty;

    [RelayCommand]
    private async Task RefreshCamerasAsync()
    {
        await RunGuardedAsync("刷新设备列表", async () =>
        {
            IReadOnlyList<CameraDescriptor> devices = await _provider.DiscoverAsync(CancellationToken.None);

            Cameras.Clear();
            foreach (CameraDescriptor descriptor in devices)
                Cameras.Add(new CameraRowViewModel(descriptor));

            SelectedCamera ??= Cameras.FirstOrDefault(c => c.CanCapture);

            Calibration.SyncCameras(Cameras);
            Calibration.SelectedCamera ??= SelectedCamera;

            Status = devices.Count == 0
                ? "未发现相机。请确认相机已上电、网线已连接、MVS 客户端已安装。"
                : $"发现 {devices.Count} 台设备（可采集 {devices.Count(c => c.CanCapture)} 台）。";
        });
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedCamera is not CameraRowViewModel row) return;

        if (!row.CanCapture)
        {
            Status = $"{row.DisplayName} 不允许取流（{row.Hint}）。";
            return;
        }

        await RunGuardedAsync($"连接 {row.DisplayName}", async () =>
        {
            await _sessions.OpenAsync(row.Descriptor.Id, CancellationToken.None);
            row.IsOpen = true;
            Status = $"已连接：{row.DisplayName}";
        });
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (SelectedCamera is not CameraRowViewModel row) return;

        await RunGuardedAsync($"断开 {row.DisplayName}", async () =>
        {
            await _sessions.CloseAsync(row.Descriptor.Id);
            row.IsOpen = false;
            PreviewImage = null;
            ImageInfo = string.Empty;
            Status = $"已断开：{row.DisplayName}";
        });
    }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (SelectedCamera is not CameraRowViewModel row) return;

        await RunGuardedAsync("单帧采集", async () =>
        {
            // 就绪门当前是占位实现：固定延时。真实振动判据见 docs/05。
            var request = CaptureGroupRequest.ForSingleCamera(
                row.Descriptor.Id,
                readiness: ReadinessCriteria.FixedDelayPlaceholder(TimeSpan.FromMilliseconds(200)));

            CaptureGroupResult result = await _capture.CaptureAsync(request, CancellationToken.None);

            if (result.Frames.Count == 0)
            {
                Status = "采集失败：未收到帧。";
                return;
            }

            Frame frame = result.Frames[0];
            BitmapSource? bitmap = FrameToBitmap.Convert(frame.Image);

            if (bitmap is null)
            {
                Status = $"不支持的像素格式：{frame.Image.PixelFormat}";
                return;
            }

            PreviewImage = bitmap;
            ImageInfo = $"{frame.Image.Width}×{frame.Image.Height}  {frame.Image.PixelFormat}  " +
                        $"{FrameToBitmap.FormatBytes(frame.Image.Pixels.Length)}  " +
                        $"时间戳精度：{result.TimestampQuality}";
            Status = $"采集成功（{result.Frames.Count} 帧，偏斜 {result.Skew.TotalMilliseconds:F1}ms）。";
        });
    }
}
