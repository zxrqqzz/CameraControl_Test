using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CameraControl.Application.Commands;
using CameraControl.Presentation.Services;
using CameraControl.Presentation.ViewModels;
using CameraControl.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CameraControl.Presentation;

/// <summary>
/// 界面目检工具：把每个窗口渲染成 PNG（<c>--shot &lt;目录&gt;</c>）。
///
/// 为什么留着它：冒烟测试只能证明"XAML 能加载"，证明不了"看起来对不对"。
/// 改完样式跑一次 <c>--shot</c>，逐张看过去，比来回启动点界面快得多，
/// 也方便把改动前后的截图并排比对。
///
/// 只读不写业务数据：自己构造 ViewModel 与示例参数，跑完直接退出。
/// </summary>
internal static class UiShotHarness
{
    public static int Run(string outputDirectory, IServiceProvider services)
    {
        Directory.CreateDirectory(outputDirectory);

        try
        {
            RunCore(outputDirectory, services);
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "error.txt"), ex.ToString());
            return 2;
        }
    }

    private static void RunCore(string outputDirectory, IServiceProvider services)
    {
        var viewModel = ActivatorUtilities.CreateInstance<MainViewModel>(services);
        viewModel.Dialog = new MessageBoxDialogService();

        try
        {
            viewModel.RefreshCamerasCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            viewModel.RunSelfCheckCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            viewModel.RefreshMetricsCommand.Execute(null);
            viewModel.RefreshAuditCommand.Execute(null);
        }
        catch
        {
            // 没有相机也要能出图
        }

        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 1200,
            Height = 780,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            ShowInTaskbar = false
        };

        window.Show();
        Pump();

        var tabs = window.FindName("MainTabs") as TabControl;
        string[] names = ["01-设备", "02-标定", "03-系统", "04-AI"];

        for (int i = 0; tabs is not null && i < names.Length; i++)
        {
            tabs.SelectedIndex = i;
            Pump();
            Save(window.Content as FrameworkElement ?? window, Path.Combine(outputDirectory, names[i] + ".png"));
        }

        var settings = new AiSettingsWindow
        {
            DataContext = viewModel,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            ShowInTaskbar = false
        };

        settings.Show();
        Pump();
        Save(settings.Content as FrameworkElement ?? settings, Path.Combine(outputDirectory, "05-AI设置.png"));
        settings.Close();

        var descriptor = new CapabilityDescriptor(
            "robot.move",
            "机器人移动到点位",
            "把机械臂移动到指定点位。",
            RiskLevel.Physical,
            typeof(object));

        var dialog = new ConfirmationDialog(
            descriptor,
            new Actor(ActorKind.Ai, "dsh"),
            "机器人将移动到 P1（预计位移 320 mm）")
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0
        };

        dialog.Show();
        Pump();
        Save(dialog.Content as FrameworkElement ?? dialog, Path.Combine(outputDirectory, "06-确认弹窗.png"));
        dialog.Close();

        var contextWindow = new ContextManagerWindow(
            services.GetRequiredService<CameraControl.Application.Cua.Context.IContextStore>(),
            "shot-profile",
            new RefusingDialogService())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            ShowInTaskbar = false
        };

        contextWindow.Show();
        Pump();
        Save(contextWindow.Content as FrameworkElement ?? contextWindow,
            Path.Combine(outputDirectory, "07-上下文管理.png"));
        contextWindow.Close();

        // 换色验证：同一页面换主色后，所有交互色应当一起变（也留给人眼比对）。
        // 用 Preview 而不是 Apply —— 目检不能改动用户保存的偏好。
        if (tabs is not null)
        {
            ThemeService.Current.Preview("pop-cyan");
            tabs.SelectedIndex = 0;
            Pump();
            Save(window.Content as FrameworkElement ?? window,
                Path.Combine(outputDirectory, "08-换色-青-设备.png"));

            ThemeService.Current.Preview("pop-pink");
            tabs.SelectedIndex = 1;
            Pump();
            Save(window.Content as FrameworkElement ?? window,
                Path.Combine(outputDirectory, "09-换色-品红-标定.png"));

            ThemeService.Current.Preview(ThemeService.DefaultKey);
            Pump();
        }

        // 调色卡本体（Popup 是独立窗口，只能单独渲染它的 Child）
        if (window.FindName("PaletteToggle") is ToggleButton paletteToggle &&
            window.FindName("PalettePopup") is Popup palettePopup &&
            palettePopup.Child is FrameworkElement paletteCard)
        {
            paletteToggle.IsChecked = true;
            Pump();
            Save(paletteCard, Path.Combine(outputDirectory, "10-调色卡.png"));
            paletteToggle.IsChecked = false;
            Pump();
        }

        window.Close();
    }

    private static void Save(FrameworkElement element, string path)
    {
        element.UpdateLayout();

        int width = (int)Math.Ceiling(element.ActualWidth);
        int height = (int)Math.Ceiling(element.ActualHeight);

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"窗口没有布局尺寸：{path}");

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(TrimTransparentEdges(bitmap, width, height)));

        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// 裁掉透明边。RenderTargetBitmap 按元素尺寸出图，但实际渲染区常比它窄一圈，
    /// 右边会留一条全透明带——不裁掉的话看图时容易被当成"多了一条黑边"。
    /// </summary>
    private static BitmapSource TrimTransparentEdges(RenderTargetBitmap bitmap, int width, int height)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        int maxX = 0;
        int maxY = 0;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;

            for (int x = 0; x < width; x++)
            {
                if (pixels[row + (x * 4) + 3] == 0) continue;

                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX <= 2 || maxY <= 2)
            return bitmap;

        return new CroppedBitmap(bitmap, new Int32Rect(0, 0, maxX + 1, maxY + 1));
    }

    /// <summary>把 UI 队列彻底跑空（布局 + 绑定 + 模板套用）。</summary>
    private static void Pump()
    {
        for (int i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();

            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));

            Dispatcher.PushFrame(frame);
        }
    }
}
