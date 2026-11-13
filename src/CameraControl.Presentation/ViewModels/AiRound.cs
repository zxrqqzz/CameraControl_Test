using CommunityToolkit.Mvvm.ComponentModel;

namespace CameraControl.Presentation.ViewModels;

/// <summary>
/// 一轮 AI 的执行视图（思考 + 本轮流式答复）。
///
/// 之所以要单独一个可观察对象，是为了让**思考与答复逐段刷新**：
/// 如果只在任务结束时一次性赋值，流式就白做了。
/// </summary>
public sealed partial class AiRound : ObservableObject
{
    public AiRound(int step) => Step = step;

    /// <summary>第几轮（从 1 开始）。</summary>
    public int Step { get; }

    /// <summary>轮次标题。</summary>
    public string Header => $"第 {Step} 轮";

    [ObservableProperty]
    private string _reasoning = string.Empty;

    [ObservableProperty]
    private string _answer = string.Empty;

    /// <summary>是否已经有思考内容（没有就不显示这一块）。</summary>
    public bool HasReasoning => Reasoning.Length > 0;

    /// <summary>追加思考增量。</summary>
    public void AppendReasoning(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        Reasoning += text;
        OnPropertyChanged(nameof(HasReasoning));
    }

    /// <summary>追加答复增量。</summary>
    public void AppendAnswer(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        Answer += text;
    }
}
