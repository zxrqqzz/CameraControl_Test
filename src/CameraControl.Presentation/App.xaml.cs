using System.IO;
using System.Text;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CameraControl.Application.Diagnostics;
using Cua = CameraControl.Application.Cua;
using CameraControl.Composition;
using CameraControl.Domain.Ports;
using CameraControl.Presentation.Services;
using CameraControl.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CameraControl.Presentation;

/// <summary>
/// 应用入口：装配组合根，并把 WPF 的确认门注入命令总线。
///
/// 这是唯一"WPF 知道具体实现"的地方——领域与应用层完全不知道界面存在。
///
/// 支持 <c>--smoke-test &lt;输出文件&gt;</c>：不开窗口，验证容器装配、
/// XAML 加载与关键服务解析，把结果写文件。用于 CI 与排障。
/// </summary>
public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 自动化运行（目检/冒烟）不要弹模态错误框：脚本会被挂死
        _headless = Array.IndexOf(e.Args, "--shot") >= 0 || Array.IndexOf(e.Args, "--smoke-test") >= 0;

        // 未捕获异常不要让窗口静默消失
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        _services = ServiceRegistration.Build(
            new CompositionOptions(ConsoleLogging: false),
            new WpfConfirmationGate());

        // 每次启动写一条 info：保证"日志在哪、这次跑的是哪个版本"可查。
        // 之前失败只显示在界面状态栏，日志里一片空白，没法排障。
        try
        {
            _services.GetService<IAppLogger>()?.Info("应用启动", new Dictionary<string, object?>
            {
                ["version"] = typeof(App).Assembly.GetName().Version?.ToString(),
                ["dataDirectory"] = ServiceRegistration.ResolveDataDirectory(),
                ["os"] = Environment.OSVersion.ToString(),
                ["args"] = string.Join(' ', e.Args)
            });
        }
        catch
        {
            // 记日志失败不能影响启动
        }

        // 外观偏好：套用上次选的主色（失败只记日志，回退默认黄）。
        // 必须放在建 MainWindow 之前——调色卡的选中态要读到 CurrentKey。
        try
        {
            ThemeService.Current.Initialize(
                _services.GetRequiredService<CameraControl.Domain.Ports.ISettingsStore>(),
                _services.GetService<IAppLogger>());
        }
        catch (Exception ex)
        {
            LogUnhandled(ex);
        }

        // 把 WPF 绑定错误接入日志。
        // 绑定失败不会抛异常，界面表现只有"点了没反应"——不接进来就永远查不到。
        try
        {
            IAppLogger? logger = _services.GetService<IAppLogger>();

            if (logger is not null)
            {
                PresentationTraceSources.Refresh();

                TraceSource? bindingSource = PresentationTraceSources.DataBindingSource;

                if (bindingSource is not null)
                {
                    bindingSource.Listeners.Add(new Diagnostics.WpfBindingTraceListener(logger));
                    bindingSource.Switch.Level = SourceLevels.Warning;
                }
            }
        }
        catch
        {
            // 诊断接不上也不能影响启动
        }

        string? smokeOutput = GetArgValue(e.Args, "--smoke-test");
        if (smokeOutput is not null)
        {
            bool captureTest = Array.IndexOf(e.Args, "--capture-test") >= 0;
            int code = RunSmokeTest(smokeOutput, captureTest);
            Shutdown(code);
            return;
        }

        // 界面目检：--shot <目录> 把每个窗口渲染成 PNG（只在开发时用）
        string? shotDirectory = GetArgValue(e.Args, "--shot");
        if (shotDirectory is not null)
        {
            Shutdown(UiShotHarness.Run(
                shotDirectory,
                _services,
                aiOnly: Array.IndexOf(e.Args, "--ai-only") >= 0,
                demoChat: Array.IndexOf(e.Args, "--demo-chat") >= 0));
            return;
        }

        // Composition 不引用 Presentation（避免反向依赖），
        // 所以视图模型由宿主用容器构造。
        var viewModel = ActivatorUtilities.CreateInstance<MainViewModel>(_services);

        viewModel.Dialog = new MessageBoxDialogService();
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();

        // 启动后自动跑一次自检，把环境问题早点摆到用户面前
        viewModel.RunSelfCheckCommand.Execute(null);
    }

    /// <summary>
    /// 无窗口冒烟测试：验证组合根能装配、XAML 能加载、关键服务能解析。
    ///
    /// 之所以做成写文件而不是控制台：WPF 是 WinExe 子系统，没有继承父进程控制台。
    /// </summary>
    private int RunSmokeTest(string outputPath, bool captureTest)
    {
        var report = new StringBuilder();
        int failures = 0;

        void Check(string name, Action action)
        {
            try
            {
                action();
                report.AppendLine($"OK   {name}");
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        report.AppendLine("=== CameraControl 冒烟测试 ===");

        Check("组合根构建", () =>
        {
            if (_services is null) throw new InvalidOperationException("服务容器未构建。");
        });

        Check("解析 ICameraProvider", () => _ = _services!.GetRequiredService<ICameraProvider>());
        Check("解析 ICaptureGroupService", () => _ = _services!.GetRequiredService<CameraControl.Application.Capture.ICaptureGroupService>());
        Check("解析 ISelfCheck", () => _ = _services!.GetRequiredService<ISelfCheck>());
        Check("解析 IAiAgent", () => _ = _services!.GetRequiredService<Cua.IAiAgent>());
        Check("解析 ICommandBus", () => _ = _services!.GetRequiredService<CameraControl.Application.Commands.ICommandBus>());

        MainViewModel? viewModel = null;

        Check("构造 MainViewModel", () =>
            viewModel = ActivatorUtilities.CreateInstance<MainViewModel>(_services!));

        // 这一步会解析 XAML（InitializeComponent），能抓出缺失的 StaticResource、绑定错误等。
        // 刻意只构造不 Show()：既验证了 XAML，又不会在冒烟测试时弹窗。
        Check("加载 MainWindow XAML", () =>
        {
            _ = new MainWindow { DataContext = viewModel };
        });

        // 回归：命令属性必须真实生成。
        // 曾经 RunAiAsync 误删 [RelayCommand]，按钮绑不到命令、点击毫无反应，
        // 而 WPF 绑定失败既不抛异常也不进日志——只能靠这条检查抓。
        Check("AI 运行命令存在", () =>
        {
            object? command = typeof(MainViewModel).GetProperty("RunAiCommand")?.GetValue(viewModel);

            if (command is null)
                throw new InvalidOperationException(
                    "RunAiCommand 为空：RunAiAsync 可能缺少 [RelayCommand]，界面按钮会点了没反应。");
        });

        // Markdown 渲染：覆盖标题/列表/表格/代码块/链接，确认不抛且真的产出了内容。
        // 渲染器在代码后台调用；正常运行时崩了只会看到空白，只能靠这里抓。
        // 上下文：只是解析一次，确认 DI 接线没断（开关关掉也算正常，不在这里判失败）。
        // 设置窗口是独立 XAML，主窗口加载成功不代表它没问题。
        Check("加载 AI 设置窗口 XAML", () =>
        {
            _ = new Views.AiSettingsWindow { DataContext = viewModel };
        });

        Check("解析 IContextStore", () =>
        {
            _ = _services!.GetRequiredService<CameraControl.Application.Cua.Context.IContextStore>();
        });

        // 上下文管理窗口同样是独立 XAML（列表行模板 + 空状态），
        // 它只在用户点「管理…」时才构造，主窗口加载成功覆盖不到这里。
        Check("加载上下文管理窗口 XAML", () =>
        {
            var store = _services!.GetRequiredService<CameraControl.Application.Cua.Context.IContextStore>();

            _ = new Views.ContextManagerWindow(store, "smoke-profile", new RefusingDialogService());
        });

        // 调色卡：换主色必须真的换掉资源。
        // 这条能挡住"有人把样式里的 Accent* 从 DynamicResource 改回 StaticResource"——
        // 那样界面看着没报错，换色却完全不生效，靠肉眼很难发现。
        Check("调色卡换主色", () =>
        {
            ThemeService theme = ThemeService.Current;
            string before = theme.CurrentKey;

            theme.Preview("pop-cyan");

            var expected = System.Windows.Media.Color.FromRgb(0x00, 0xB8, 0xD4);

            if (System.Windows.Application.Current?.Resources["AccentBrush"] is not
                System.Windows.Media.SolidColorBrush accent ||
                accent.Color != expected)
            {
                throw new InvalidOperationException("换主色后 AccentBrush 没有更新，界面颜色不会变。");
            }

            theme.Preview(before);
        });

        Check("Markdown 渲染", () =>
        {
            const string sample = """
            ## 相机情况
            当前有 **2 台** 设备，其中 `Integrated Camera` 可采集。

            - 可采集：1 台
            - 仅列出：1 台（红外）

            | 名称 | 类型 |
            |---|---|
            | Integrated Camera | Uvc |

            ```csharp
            var ok = true;
            ```

            [安全链接](https://example.com)
            [不安全链接](file:///C:/Windows)
            """;

            System.Windows.Documents.FlowDocument document =
                CameraControl.Presentation.Markdown.MarkdownRenderer.Render(sample);

            if (document.Blocks.Count < 4)
                throw new InvalidOperationException("Markdown 渲染块数异常：" + document.Blocks.Count);
        });

        if (viewModel is not null)
        {
            Check("执行启动自检", () => RunSelfCheck(viewModel));

            report.AppendLine();
            report.AppendLine("--- 自检明细 ---");
            foreach (string item in viewModel.SelfCheckItems)
                report.AppendLine("  " + item);

            // 直接把设备明细（名称/类型/是否红外）打出来，便于确认 UVC 是否被正确识别
            report.AppendLine();
            report.AppendLine("--- 设备明细 ---");

            try
            {
                var provider = _services!.GetRequiredService<CameraControl.Domain.Ports.ICameraProvider>();
                var devices = provider.DiscoverAsync(CancellationToken.None).GetAwaiter().GetResult();

                report.AppendLine($"  共 {devices.Count} 台：");

                foreach (var device in devices)
                {
                    string flag = device.IsInfraredOnly
                        ? "(红外，不取流)"
                        : device.CanCapture ? "(可采集)" : "(不可采集)";

                    report.AppendLine($"  · {device.DisplayName}  [{device.Kind}]  {flag}");
                    report.AppendLine($"    id = {device.Id.Value}");
                    report.AppendLine($"    厂商 = {device.Vendor}  型号 = {device.Model}");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"  枚举失败：{ex.Message}");
            }

            // 可选：真抓一帧，验证打开/采集链路与错误映射
            if (captureTest)
            {
                report.AppendLine();
                report.AppendLine("--- 采集测试 ---");

                try
                {
                    var provider = _services!.GetRequiredService<CameraControl.Domain.Ports.ICameraProvider>();
                    var devices = provider.DiscoverAsync(CancellationToken.None).GetAwaiter().GetResult();
                    var target = devices.FirstOrDefault(d => d.CanCapture);

                    if (target is null)
                    {
                        report.AppendLine("  没有可采集设备，跳过。");
                    }
                    else
                    {
                        report.AppendLine($"  目标：{target.DisplayName}");

                        var session = provider.OpenAsync(target.Id, CancellationToken.None)
                            .GetAwaiter().GetResult();

                        try
                        {
                            var frame = session.CaptureSingleAsync(null, CancellationToken.None)
                                .GetAwaiter().GetResult();

                            report.AppendLine(
                                $"  成功：{frame.Image.Width}×{frame.Image.Height} " +
                                $"{frame.Image.PixelFormat} {frame.Image.Pixels.Length} 字节");
                        }
                        finally
                        {
                            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }
                    }
                }
                catch (CameraControl.Domain.Ports.CameraOperationException ex)
                {
                    report.AppendLine($"  预期内失败：[{ex.ErrorCode}] {ex.Message}");
                }
                catch (Exception ex)
                {
                    report.AppendLine($"  失败：{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        report.AppendLine(failures == 0 ? "结论：全部通过" : $"结论：{failures} 项失败");

        try
        {
            string full = Path.GetFullPath(outputPath);
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(full, report.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 写不了报告也不能崩
        }

        return failures == 0 ? 0 : 1;

        // 局部函数：避免把异步调用混进同步 Check
        static void RunSelfCheck(MainViewModel vm)
        {
            vm.RunSelfCheckCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            if (vm.SelfCheckItems.Count == 0)
                throw new InvalidOperationException("自检没有产生任何结果。");
        }
    }

    private static string? GetArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出时的释放一律不得再抛：此时用户已经在关程序，
        // 抛异常只会变成"关不掉"或关停崩溃弹窗。
        try
        {
            _services?.Dispose();
        }
        catch (Exception ex)
        {
            LogUnhandled(ex);
        }

        base.OnExit(e);
    }

    /// <summary>
    /// 已弹过错误框的标记。
    ///
    /// 为什么要它：绑定类错误会**按数据项重复触发**——曾导致"点刷新后弹出一叠错误框"。
    /// 只弹第一次，其余仅记日志。
    /// </summary>
    private static bool _errorDialogShown;

    /// <summary>自动化运行（--shot / --smoke-test）：异常只落日志，不弹模态框，免得脚本被挂死。</summary>
    private static bool _headless;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogUnhandled(e.Exception);

        if (!_headless && !_errorDialogShown)
        {
            _errorDialogShown = true;

            string detail = e.Exception.InnerException is { } inner
                ? $"{e.Exception.Message}\n\n{inner.Message}"
                : e.Exception.Message;

            MessageBox.Show(
                $"发生未处理的异常：\n\n{detail}\n\n" +
                "（后续同类错误不再弹窗，仅写入日志。）\n\n" +
                "详情见目录 %LocalAppData%\\CameraControl\\logs",
                "CameraControl",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception ex) return;

        LogUnhandled(ex);

        if (_errorDialogShown) return;

        _errorDialogShown = true;

        MessageBox.Show(
            $"发生未处理的异常：\n\n{ex.Message}",
            "CameraControl",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    /// <summary>
    /// 同一个异常在窗口期内的重复计数（判重键 = 类型 + 消息 + 首行堆栈）。
    ///
    /// 为什么要节流：布局阶段抛出的异常会被反复重试——实测一次
    /// "StaticResource 找不到"在几分钟里抛了 6 万多次，每次带完整堆栈，
    /// 单日日志 448 MB，把工作区撑大一倍、VS 卡到崩。
    /// 现在同一签名在 <see cref="ErrorThrottleWindow"/> 内只完整记一次，
    /// 之后每累计 100 次补一条"已重复 N 次"，既不丢信息也不爆盘。
    /// </summary>
    private static readonly Dictionary<string, (DateTime AtUtc, int Count)> ErrorThrottle = [];

    private static readonly object ErrorThrottleSync = new();

    private static readonly TimeSpan ErrorThrottleWindow = TimeSpan.FromSeconds(60);

    private const int ErrorThrottleReportEvery = 100;

    /// <summary>把未处理异常写进日志（含内部异常与堆栈），记日志本身不得再抛。</summary>
    private void LogUnhandled(Exception exception)
    {
        try
        {
            string signature = BuildSignature(exception);

            if (!ShouldWriteErrorLog(signature, out int repeated))
            {
                if (repeated % ErrorThrottleReportEvery == 0)
                {
                    _services?.GetService<IAppLogger>()?.Warn(
                        $"同类异常已重复 {repeated} 次（后续同类仍只计数，不再展开堆栈）",
                        new Dictionary<string, object?> { ["signature"] = signature });
                }

                return;
            }

            var fields = new Dictionary<string, object?>
            {
                ["type"] = exception.GetType().FullName,
                ["message"] = exception.Message,
                ["inner"] = exception.InnerException?.Message,
                ["stack"] = exception.StackTrace
            };

            // XAML 解析错误：把行号也记上，否则只有一句"类型不匹配"，不知道改哪一行
            if (exception is System.Windows.Markup.XamlParseException xaml)
            {
                fields["xamlBaseUri"] = xaml.BaseUri;
                fields["xamlLine"] = xaml.LineNumber;
                fields["xamlColumn"] = xaml.LinePosition;
            }

            _services?.GetService<IAppLogger>()?.Error("未处理的异常", fields);
        }
        catch
        {
            // 记录失败不能级联
        }
    }

    /// <summary>判重：窗口期内同签名只放行第一次；返回当前累计重复次数。</summary>
    private static bool ShouldWriteErrorLog(string signature, out int repeated)
    {
        repeated = 0;

        try
        {
            lock (ErrorThrottleSync)
            {
                DateTime now = DateTime.UtcNow;

                if (ErrorThrottle.TryGetValue(signature, out (DateTime AtUtc, int Count) entry) &&
                    now - entry.AtUtc < ErrorThrottleWindow)
                {
                    entry.Count++;
                    ErrorThrottle[signature] = entry;
                    repeated = entry.Count;
                    return false;
                }

                // 表本身也不能无限长：异常花样多的时候定期清一次（不影响窗口判断的大局）
                if (ErrorThrottle.Count > 200)
                    ErrorThrottle.Clear();

                ErrorThrottle[signature] = (now, 1);
                return true;
            }
        }
        catch
        {
            // 判重失败时宁可多记一条，也不要丢异常
            return true;
        }
    }

    private static string BuildSignature(Exception exception)
    {
        string stack = exception.StackTrace ?? string.Empty;
        int newline = stack.IndexOf('\n');
        string firstFrame = newline > 0 ? stack[..Math.Min(newline, 160)] : stack[..Math.Min(stack.Length, 160)];

        return $"{exception.GetType().FullName}|{exception.Message}|{firstFrame}";
    }
}
