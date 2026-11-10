using System.Collections.ObjectModel;
using System.Globalization;
using CameraControl.Application.Calibration;
using CameraControl.Application.Capabilities;
using CameraControl.Application.Commands;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CameraControl.Presentation.ViewModels;

/// <summary>
/// 标定面板：采图 → 检测 → 求解 → 落盘。
///
/// 设计原则：**不自己算**。所有动作都通过命令总线调用能力，
/// 与 AI 走完全相同的代码路径——这样"人点按钮"和"AI 调能力"不会出现两套行为。
/// </summary>
public sealed partial class CalibrationPanelViewModel : ObservableObject
{
    private readonly ICommandBus _bus;

    public CalibrationPanelViewModel(ICommandBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
    }

    /// <summary>可选相机（与设备页共用同一集合）。</summary>
    public ObservableCollection<CameraRowViewModel> Cameras { get; } = [];

    [ObservableProperty]
    private CameraRowViewModel? _selectedCamera;

    [ObservableProperty]
    private int _gridCols = 12;

    [ObservableProperty]
    private int _gridRows = 13;

    [ObservableProperty]
    private double _squareMm = 30;

    [ObservableProperty]
    private int _viewCount;

    /// <summary>
    /// "已采集视角：N" 的整句文本。
    ///
    /// 刻意不在 XAML 里用多个 &lt;Run&gt; 拼接：
    /// Run.Text 绑定默认 TwoWay，是全项目禁止的写法（见 XamlBindingGuardTests）。
    /// </summary>
    public string ViewCountText => $"已采集视角：{ViewCount}";

    partial void OnViewCountChanged(int value) => OnPropertyChanged(nameof(ViewCountText));

    [ObservableProperty]
    private string _status = "先在「设备」页刷新并打开相机，再回到这里采集。";

    [ObservableProperty]
    private string _result = string.Empty;

    [ObservableProperty]
    private bool _busy;

    /// <summary>把已发现的相机同步进标定页。</summary>
    public void SyncCameras(IEnumerable<CameraRowViewModel> cameras)
    {
        ArgumentNullException.ThrowIfNull(cameras);

        Cameras.Clear();

        foreach (CameraRowViewModel camera in cameras)
            Cameras.Add(camera);

        SelectedCamera ??= Cameras.FirstOrDefault(c => c.CanCapture);
    }

    [RelayCommand]
    private async Task ConfigureAsync()
    {
        if (SelectedCamera is null) { Status = "请先选择相机。"; return; }

        await RunAsync(
            new CalibrationConfigureCommand(SelectedCamera.Id, GridCols, GridRows, SquareMm),
            "棋盘已配置（已清空旧视角）。");
    }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (SelectedCamera is null) { Status = "请先选择相机。"; return; }

        await RunAsync(
            new CalibrationDetectCornersCommand(SelectedCamera.Id),
            "已检测并追加一个视角。");
    }

    [RelayCommand]
    private async Task SolveAsync()
    {
        if (SelectedCamera is null) { Status = "请先选择相机。"; return; }

        // 图像尺寸取当前会话里相机的实际输出：先用默认值，失败时提示用户
        await RunAsync(
            new CalibrationApplyCommand(SelectedCamera.Id, 1280, 720),
            "求解完成并已保存。");
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (SelectedCamera is null) { Status = "请先选择相机。"; return; }

        await RunAsync(new CalibrationResetCommand(SelectedCamera.Id), "已清空视角。");
    }

    [RelayCommand]
    private async Task RefreshStateAsync()
    {
        if (SelectedCamera is null) return;

        CommandResult result = await _bus
            .ExecuteAsync(new CalibrationGetStateCommand(SelectedCamera.Id), Actor.Human("标定面板"))
            .ConfigureAwait(true);

        ApplyState(result);
    }

    private async Task RunAsync(ICommand command, string successMessage)
    {
        Busy = true;

        try
        {
            CommandResult result = await _bus
                .ExecuteAsync(command, Actor.Human("标定面板"))
                .ConfigureAwait(true);

            if (!result.Ok)
            {
                Status = result.Error is null
                    ? "操作失败。"
                    : $"[{result.Error.Code}] {result.Error.Message}";
                return;
            }

            Status = successMessage;
            ApplyState(result);

            await RefreshStateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"操作异常：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private void ApplyState(CommandResult result)
    {
        if (result.Data is CalibrationSessionState state)
        {
            ViewCount = state.ViewCount;
            return;
        }

        if (result.Data is null) return;

        // 求解结果：把关键数字摊平成一行，便于肉眼核对
        if (result.Data is CalibrationSolveSummary summary)
        {
            Result = summary.ToSummary();
        }
    }


}
