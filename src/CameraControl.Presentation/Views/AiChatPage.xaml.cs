using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CameraControl.Presentation.ViewModels;

namespace CameraControl.Presentation.Views;

/// <summary>
/// AI 页：对话流 + 输入卡。
///
/// 这里只留两件"非绑定"的事（其余都在 <see cref="MainViewModel"/> 里）：
///   1) 输入框 Enter 发送 / Shift+Enter 换行（键盘语义）；
///   2) 对话流自动滚到底（滚动位置不是可绑定属性，只能代码里滚）。
/// </summary>
public partial class AiChatPage : UserControl
{
    private MainViewModel? _subscribed;

    public AiChatPage() => InitializeComponent();

    /// <summary>DataContext 变化时重新挂钩：滚到底令牌。</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribed is not null)
            _subscribed.PropertyChanged -= OnViewModelPropertyChanged;

        if (e.NewValue is not MainViewModel viewModel) return;

        _subscribed = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 首次进来（可能已经灌了历史）直接落到底部
        Dispatcher.BeginInvoke(new Action(ScrollChatToEnd), DispatcherPriority.Loaded);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ChatScrollTick)) return;

        // 布局还没跑完就滚会滚不到位，排到后台优先级里再滚
        Dispatcher.BeginInvoke(new Action(ScrollChatToEnd), DispatcherPriority.Background);
    }

    private void ScrollChatToEnd()
    {
        // 页面还没 load 完 / 正在换模板时滚动会抛异常，直接跳过
        if (ChatScroll?.IsLoaded != true) return;

        try
        {
            ChatScroll.ScrollToEnd();
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"对话流滚动失败：{ex.Message}");
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
