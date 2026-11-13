using System.ComponentModel;
using System.Windows.Input;
using System.Windows;
using CameraControl.Presentation.ViewModels;

namespace CameraControl.Presentation;

/// <summary>
/// 主窗口。业务逻辑都在 <see cref="MainViewModel"/> 里，这里只做界面粘合。
///
/// 现在只剩两件"非绑定"的事：
///   1) 输入框 Enter 发送 / Shift+Enter 换行（键盘语义）；
///   2) 对话流自动滚到底（滚动位置不是可绑定属性，只能代码里滚）。
/// Markdown 渲染已经搬进视图模型（每条消息自己带 FlowDocument），这里不再碰。
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? _subscribed;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>DataContext 变化时重新挂钩：滚到底令牌。</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribed is not null)
            _subscribed.PropertyChanged -= OnViewModelPropertyChanged;

        if (e.NewValue is not MainViewModel viewModel) return;

        _subscribed = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 首次进来（可能已经灌了历史）直接落到底部
        Dispatcher.BeginInvoke(new Action(ScrollChatToEnd), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ChatScrollTick)) return;

        // 布局还没跑完就滚会滚不到位，排到后台优先级里再滚
        Dispatcher.BeginInvoke(new Action(ScrollChatToEnd),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ScrollChatToEnd()
    {
        try
        {
            ChatScroll.ScrollToEnd();
        }
        catch
        {
            // 窗口还没成形时滚动会抛，忽略即可
        }
    }

    /// <summary>
    /// 输入框快捷键：Enter 发送，Shift+Enter 换行（聊天工具习惯）。
    /// 输入框最大行数由 XAML 的 MaxLines 控制（常量，改这里即可调）。
    /// </summary>
    private void OnGoalKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;

        if (DataContext is MainViewModel viewModel && viewModel.RunAiCommand.CanExecute(null))
            viewModel.RunAiCommand.Execute(null);

        e.Handled = true;
    }
}
