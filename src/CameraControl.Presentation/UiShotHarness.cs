using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CameraControl.Application.Commands;
using CameraControl.Presentation.Markdown;
using CameraControl.Presentation.Services;
using CameraControl.Presentation.ViewModels;
using CameraControl.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CameraControl.Presentation;

/// <summary>
/// 界面目检工具：把每个窗口渲染成 PNG（<c>--shot &lt;目录&gt; [--ai-only] [--demo-chat]</c>）。
///
/// 为什么留着它：冒烟测试只能证明"XAML 能加载"，证明不了"看起来对不对"。
/// 改完样式跑一次 <c>--shot</c>，逐张看过去，比来回启动点界面快得多。
///
/// 参数：
///   --ai-only    只渲染 AI 页（迭代聊天界面时用，快）
///   --demo-chat  给 AI 页灌一段示例对话（气泡/思考/步骤/用量都能看到）
/// </summary>
internal static class UiShotHarness
{
    public static int Run(string outputDirectory, IServiceProvider services, bool aiOnly = false, bool demoChat = false, string? accent = null)
    {
        Directory.CreateDirectory(outputDirectory);

        try
        {
            if (!string.IsNullOrWhiteSpace(accent))
                ThemeService.Current.Preview(accent);   // 目检专用：换色不落盘

            RunCore(outputDirectory, services, aiOnly, demoChat);
            return 0;
        }
        catch (Exception ex)
        {
            string detail = ex.ToString();

            // XamlParseException 自带行号（ToString 不打印），补上便于定位
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                if (e is System.Windows.Markup.XamlParseException xaml)
                {
                    detail = $"XAML 位置：{xaml.BaseUri} 行 {xaml.LineNumber} 列 {xaml.LinePosition} {xaml.KeyContext}{Environment.NewLine}{detail}";
                    break;
                }
            }

            File.WriteAllText(Path.Combine(outputDirectory, "error.txt"), detail);
            return 2;
        }
    }

    private static void RunCore(string outputDirectory, IServiceProvider services, bool aiOnly, bool demoChat)
    {
        var viewModel = ActivatorUtilities.CreateInstance<MainViewModel>(services);
        viewModel.Dialog = new MessageBoxDialogService();

        if (demoChat)
        {
            SeedDemoConversation(viewModel);
        }
        else if (!aiOnly)
        {
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

        if (aiOnly)
        {
            if (tabs is not null) tabs.SelectedIndex = 3;
            Pump();
            Save(window.Content as FrameworkElement ?? window, Path.Combine(outputDirectory, "04-AI.png"));
            window.Close();
            return;
        }

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

        // 换色验证：同一页面换主色后，所有交互色应当一起变
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

    /// <summary>给 AI 页灌一段示例对话，用来目检气泡。</summary>
    private static void SeedDemoConversation(MainViewModel viewModel)
    {
        const string answer = """
        ## 相机情况

        当前有 **2 台** 设备，其中 `Integrated Camera` 可采集。

        | 名称 | 类型 | 可取流 |
        |---|---|---|
        | Integrated Camera | Uvc | 是 |
        | Integrated IR Camera | Uvc | 否（红外） |
        """;

        viewModel.AiMessages.Add(ChatMessage.User("列出当前所有相机，并告诉我哪一台可以采集（顺便看看红外那台能不能取流）"));

        ChatMessage done = ChatMessage.Assistant();
        done.RoundFor(1).AppendReasoning("先列设备，看看有没有可采集的；红外只列出、不取流，这是隐私约束里的硬规则。");
        done.RoundFor(2).AppendReasoning("拿到列表后按 CanCapture 分成两组，再用表格给结论。");
        done.AppendStep("[1] OK  camera.list (12ms)");
        done.AppendStep("[2] OK  capture.preview (238ms)");
        done.Text = answer;
        done.Document = MarkdownRenderer.Render(answer);
        done.UsageLine = "本次 3,140 tok";
        done.UsageDetail = "示例数据：真实明细在运行时生成。";
        viewModel.AiMessages.Add(done);

        ChatMessage streaming = ChatMessage.Assistant();
        streaming.RoundFor(1).AppendReasoning("正在读取当前曝光时间…");
        streaming.Text = "先读取相机当前的曝光参数，然后再判断是否需要调整…";
        streaming.IsStreaming = true;
        viewModel.AiMessages.Add(streaming);

        viewModel.ChatScrollTick++;
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

    /// <summary>裁掉透明边（RenderTargetBitmap 的取景常比实际渲染区大一圈）。</summary>
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

    /// <summary>把 UI 队列跑空（布局 + 绑定 + 模板套用）。</summary>
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
