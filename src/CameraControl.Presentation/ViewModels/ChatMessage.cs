using System.Collections.ObjectModel;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CameraControl.Presentation.ViewModels;

/// <summary>
/// AI 页对话流里的一条消息（用户或助手）。
///
/// 为什么要有这个类：以前整页只有"一份"答复（AiLiveAnswer / AiAnswer / AiRounds…），
/// 只能显示最后一次结果。改成消息列表后，每轮对话各占一条，
/// 思考 / 执行步骤 / 用量 都挂在**那条消息自己身上**，历史对话也能按同样形状渲染。
/// </summary>
public sealed partial class ChatMessage : ObservableObject
{
    private ChatMessage(bool isUser, DateTimeOffset at)
    {
        IsUser = isUser;
        Timestamp = at;

        Rounds.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRounds));
        Steps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSteps));
    }

    /// <summary>用户消息（右侧气泡）。</summary>
    public static ChatMessage User(string text, DateTimeOffset? at = null) =>
        new(true, at ?? DateTimeOffset.Now) { Text = text };

    /// <summary>助手消息（左侧无框）；运行中用，结束后补正文与用量。</summary>
    public static ChatMessage Assistant(DateTimeOffset? at = null) => new(false, at ?? DateTimeOffset.Now);

    public bool IsUser { get; }

    public bool IsAssistant => !IsUser;

    public DateTimeOffset Timestamp { get; }

    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm");

    /// <summary>正文原文：用户输入；助手则是流式累积的纯文本 / 完成后的 Markdown 原文。</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>助手正在生成（显示纯文本流，不显示 Markdown）。</summary>
    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>助手渲染好的 Markdown 文档（完成后才有）。</summary>
    [ObservableProperty]
    private FlowDocument? _document;

    /// <summary>本轮失败（取消/出错）——气泡用警示色标一下。</summary>
    [ObservableProperty]
    private bool _isFailed;

    /// <summary>思考过程：按轮次分组（每轮一个折叠面板）。</summary>
    public ObservableCollection<AiRound> Rounds { get; } = [];

    /// <summary>执行步骤：一行一次工具调用。</summary>
    public ObservableCollection<string> Steps { get; } = [];

    public bool HasRounds => Rounds.Count > 0;

    public bool HasSteps => Steps.Count > 0;

    public string ReasoningHeader => $"思考过程（{Rounds.Count} 轮）";

    public string StepsHeader => $"执行步骤（{Steps.Count} 步）";

    /// <summary>用量一行摘要（气泡右下角）；没有就是空串。</summary>
    [ObservableProperty]
    private string _usageLine = string.Empty;

    /// <summary>用量明细（点摘要弹出的小框里显示）。</summary>
    [ObservableProperty]
    private string _usageDetail = string.Empty;

    public bool HasUsage => UsageLine.Length > 0;

    /// <summary>用户气泡正文。</summary>
    public bool ShowUserText => IsUser;

    /// <summary>助手流式纯文本。</summary>
    public bool ShowStreamingText => IsAssistant && IsStreaming;

    /// <summary>助手渲染后的 Markdown。</summary>
    public bool ShowDocument => IsAssistant && Document is not null;

    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(ShowStreamingText));

    partial void OnDocumentChanged(FlowDocument? value) => OnPropertyChanged(nameof(ShowDocument));

    partial void OnUsageLineChanged(string value) => OnPropertyChanged(nameof(HasUsage));

    /// <summary>追加流式正文。</summary>
    public void AppendText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        Text += text;
    }

    /// <summary>取该轮的第 <paramref name="step"/> 组思考；没有就建一组。</summary>
    public AiRound RoundFor(int step)
    {
        AiRound? existing = Rounds.FirstOrDefault(r => r.Step == step);

        if (existing is not null) return existing;

        var created = new AiRound(step);
        Rounds.Add(created);
        OnPropertyChanged(nameof(ReasoningHeader));

        return created;
    }

    /// <summary>补一条执行步骤，并刷新标题里的计数。</summary>
    public void AppendStep(string line)
    {
        Steps.Add(line);
        OnPropertyChanged(nameof(StepsHeader));
    }
}
