using System.ComponentModel;
using System.Windows.Input;
using System.Windows;
using CameraControl.Presentation.Markdown;
using CameraControl.Presentation.ViewModels;

namespace CameraControl.Presentation;

/// <summary>主窗口。业务逻辑都在 <see cref="MainViewModel"/> 里，这里只做界面粘合。</summary>
public partial class MainWindow : Window
{
    private MainViewModel? _subscribed;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>DataContext 变化时重新挂钩：密钥输入缓冲 + Markdown 答复渲染。</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribed is not null)
            _subscribed.PropertyChanged -= OnViewModelPropertyChanged;

        if (e.NewValue is not MainViewModel viewModel) return;

        _subscribed = viewModel;


        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        RenderAnswer(viewModel.AiAnswer);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 只关心答复：其余属性 WPF 会自己跟着绑定更新
        if (e.PropertyName == nameof(MainViewModel.AiAnswer) && _subscribed is not null)
            RenderAnswer(_subscribed.AiAnswer);
    }

    /// <summary>
    /// 把 Markdown 渲染成 FlowDocument。
    ///
    /// 为什么放代码后台而不是绑一个转换器：FlowDocument 不是普通值，
    /// 每次要新建实例、还要挂链接点击；用转换器会不停重建、且难处理异常。
    /// 这里集中一处，渲染失败也不会连累界面（见 <see cref="MarkdownRenderer"/>）。
    /// </summary>
    private void RenderAnswer(string? markdown)
    {
        try
        {
            AnswerViewer.Document = MarkdownRenderer.Render(markdown);
        }
        catch (Exception ex)
        {
            // 渲染失败退回纯文本，绝不让"看不懂格式"变成"程序崩了"
            var document = new System.Windows.Documents.FlowDocument(
                new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(
                    $"{markdown}\n\n（Markdown 渲染失败：{ex.Message}，已按纯文本显示）")));

            AnswerViewer.Document = document;
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