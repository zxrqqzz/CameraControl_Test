using System.Windows;
using CameraControl.Application.Commands;
using CameraControl.Presentation.Views;

namespace CameraControl.Presentation.Services;

/// <summary>
/// 确认门的 WPF 实现：弹模态窗问人。
///
/// 线程安全：AI 循环在后台线程跑，弹窗必须回到 UI 线程。
/// 这里用 <see cref="Application.Current"/> 的 Dispatcher 编组；
/// 若当前应用没有 WPF Application（例如单元测试或命令行宿主），
/// 则**失败关闭**——直接返回 false，绝不静默放行。
/// </summary>
public sealed class WpfConfirmationGate : IConfirmationGate
{
    public Task<bool> RequestAsync(
        CapabilityDescriptor descriptor,
        Actor actor,
        string? summary,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(false);

        // 注意：这里的 Application 必须全限定，否则会命中 CameraControl.Application 命名空间
        System.Windows.Application? app = System.Windows.Application.Current;
        if (app is null)
        {
            // 没有 UI 宿主：不能假装得到确认
            return Task.FromResult(false);
        }

        if (app.Dispatcher.CheckAccess())
            return Task.FromResult(ShowDialog(descriptor, actor, summary));

        return app.Dispatcher
            .InvokeAsync(() => ShowDialog(descriptor, actor, summary))
            .Task;
    }

    private static bool ShowDialog(CapabilityDescriptor descriptor, Actor actor, string? summary)
    {
        Window? owner = System.Windows.Application.Current?.MainWindow;

        var dialog = new ConfirmationDialog(descriptor, actor, summary)
        {
            Owner = owner is { IsLoaded: true } ? owner : null
        };

        // ShowDialog 会启动嵌套消息泵，期间 UI 仍然响应
        bool? result = dialog.ShowDialog();
        return result == true;
    }
}
