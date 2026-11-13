using CameraControl.Application.Commands;
using CameraControl.Application.Cua;
using CameraControl.Application.Cua.Context;
using CameraControl.Application.Diagnostics;
using CameraControl.Infrastructure.Ai;
using CameraControl.Presentation.Markdown;
using CameraControl.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CameraControl.Presentation.ViewModels;

/// <summary>
/// AI 对话块：执行一轮任务、停止、历史回放、进度事件与滚动令牌。
/// </summary>
public sealed partial class MainViewModel
{

    // ── 对话流：消息 / 流式 / 停止 ──────────────────────────

    [ObservableProperty]
    private bool _isAiRunning;

    /// <summary>正在生成的那条助手消息（流式增量都写它）。</summary>
    private ChatMessage? _activeTurn;

    private CancellationTokenSource? _aiCancellation;

    /// <summary>停止：立即中断（含正在执行的工具调用）。</summary>
    [RelayCommand]
    private void StopAi()
    {
        try
        {
            _aiCancellation?.Cancel();
            Status = "已请求停止…";
        }
        catch (Exception ex)
        {
            Status = $"停止失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 把本地上下文里的历史对话灌进对话流（启动、切换配置、换上下文时调用）。
    ///
    /// 只取最近 <see cref="MaxHistoryMessages"/> 条：历史可能有几百条，
    /// 全渲染出来既慢又没人往下翻；更早的还能在「上下文管理」里看到。
    /// </summary>
    private void LoadAiHistory()
    {
        AiMessages.Clear();

        try
        {
            LlmProfile? active = _llmSettings.ActiveProfile();

            _activeUsageContextId = active is null || !_contextStore.IsEnabled
                ? null
                : _contextStore.GetActiveContextId(active.Id);

            if (active is not null && _contextStore.IsEnabled)
            {
                IReadOnlyList<ContextEntry> entries = _contextStore.ReadActiveEntries(active.Id);

                // 用量流水按"助手消息顺序"对应；从**末尾**对齐——
                // 流水是后来才加的，老对话没有记录，最老的几轮必然对不上，宁可不显示也不要张冠李戴。
                IReadOnlyList<Services.TurnUsageEntry> usage = Services.UsageJournal.Read(_activeUsageContextId);

                var assistants = new List<ChatMessage>();

                foreach (ContextEntry entry in entries.TakeLast(MaxHistoryMessages))
                {
                    if (entry.Role == ContextEntryRole.User)
                    {
                        AiMessages.Add(ChatMessage.User(entry.Text, entry.AtUtc));
                        continue;
                    }

                    ChatMessage assistant = BuildHistoryAssistant(entry);
                    assistants.Add(assistant);
                    AiMessages.Add(assistant);
                }

                int offset = assistants.Count - usage.Count;

                for (int i = 0; i < assistants.Count; i++)
                {
                    int index = i - offset;

                    if (index < 0 || index >= usage.Count) continue;

                    Services.TurnUsageEntry item = usage[index];

                    assistants[i].UsageLine = $"本次 {item.TotalTokens:N0} tok";
                    assistants[i].UsageDetail =
                        "—— 本轮（来自落盘记录）——" + Environment.NewLine +
                        $"未缓存输入：{item.UncachedInputTokens:N0} tok{Environment.NewLine}" +
                        $"缓存读取：{item.CachedTokens:N0} tok{Environment.NewLine}" +
                        $"输出：{item.CompletionTokens:N0} tok{Environment.NewLine}" +
                        $"合计：{item.TotalTokens:N0} tok";
                }
            }
        }
        catch (Exception ex)
        {
            // 历史读不出来不影响新对话
            _logger.Warn("读取 AI 历史对话失败", new Dictionary<string, object?>
            {
                ["error"] = ex.Message
            });
        }

        RefreshSessionUsage();
        NotifyChatChanged();
    }

    private const int MaxHistoryMessages = 200;

    /// <summary>历史里的助手条目：只有正文（当时没存思考/步骤/用量）。</summary>
    private static ChatMessage BuildHistoryAssistant(ContextEntry entry)
    {
        ChatMessage message = ChatMessage.Assistant(entry.AtUtc);
        message.Text = entry.Text;
        message.Document = MarkdownRenderer.Render(entry.Text);
        return message;
    }

    /// <summary>对话流变化后刷新"是否为空"，并请求视图滚到底。</summary>
    private void NotifyChatChanged()
    {
        OnPropertyChanged(nameof(HasAiMessages));
        ChatScrollTick++;
    }

    /// <summary>
    /// 进度回调。注意：<see cref="Progress{T}"/> 会回到 UI 线程，
    /// 所以这里可以直接改绑定属性。
    /// </summary>
    private void OnAiProgress(AiProgressEvent e)
    {
        ChatMessage? turn = _activeTurn;
        if (turn is null) return;

        switch (e.Kind)
        {
            case AiProgressKind.ReasoningDelta:
                turn.RoundFor(e.Step).AppendReasoning(e.Text);
                break;

            case AiProgressKind.ContentDelta:
                turn.AppendText(e.Text);
                break;
        }

        ChatScrollTick++;
    }

    /// <summary>
    /// 运行 AI 任务。
    ///
    /// 注意：这个方法曾经因为一次整段替换**丢了 [RelayCommand]**，
    /// 导致界面上按钮绑不到 `RunAiCommand`、点击毫无反应且没有任何日志。
    /// 现在入口会先记一条日志，`IsBusy` 挡掉也会记——避免再出现"静默无反应"。
    /// </summary>
    [RelayCommand]
    private async Task RunAiAsync()
    {
        _logger.Info("AI 任务触发", new Dictionary<string, object?>
        {
            ["goal"] = AiGoal,
            ["busy"] = IsBusy
        });

        if (string.IsNullOrWhiteSpace(AiGoal))
        {
            Status = "请先输入目标。";
            _logger.Warn("AI 任务被跳过：目标为空");
            return;
        }

        // 一问一答进对话流：用户消息立刻上屏，助手消息先占位再流式长
        string goal = AiGoal.Trim();

        ChatMessage userMessage = ChatMessage.User(goal);
        ChatMessage turn = ChatMessage.Assistant();
        turn.IsStreaming = true;

        AiMessages.Add(userMessage);
        AiMessages.Add(turn);
        _activeTurn = turn;
        AiGoal = string.Empty;
        NotifyChatChanged();

        using var cancellation = new CancellationTokenSource();
        _aiCancellation = cancellation;
        IsAiRunning = true;

        await RunGuardedAsync("AI 任务", async () =>
        {
            try
            {
                AiTaskResult result = await _agent.RunAsync(
                    new AiTaskRequest(
                        goal,
                        MaxSteps: 8,
                        ContextProfileId: _llmSettings.ActiveProfile()?.Id),
                    Actor.Ai("wpf-agent", Guid.NewGuid().ToString("N")[..8]),
                    cancellation.Token,
                    new Progress<AiProgressEvent>(OnAiProgress));

                foreach (AiStep step in result.Steps)
                {
                    if (!string.IsNullOrWhiteSpace(step.AssistantText))
                        turn.AppendStep($"[{step.Step}] 模型：{step.AssistantText}");

                    foreach (AiToolInvocation invocation in step.Invocations)
                    {
                        string mark = invocation.Ok ? "OK  " : "FAIL";
                        turn.AppendStep(
                            $"[{step.Step}] {mark} {invocation.CapabilityId} ({invocation.ElapsedMs:F0}ms)" +
                            (invocation.ErrorCode is null ? "" : $"  {invocation.ErrorCode}"));
                    }
                }

                ApplyAiUsage(result.Usage, turn);

                if (result.Completed)
                {
                    turn.Text = result.Answer ?? "(模型没有给出文字答复)";
                    Status = $"AI 任务完成，工具调用 {result.ToolCallCount} 次。";
                }
                else
                {
                    turn.Text = result.Error ?? "任务未完成。";
                    turn.IsFailed = true;
                    Status = $"AI 任务未完成（{result.StopReason}）。";
                }

                FinishTurn(turn);
            }
            finally
            {
                IsAiRunning = false;
                _aiCancellation = null;
                _activeTurn = null;
                turn.IsStreaming = false;
                RefreshContextInfo();
                NotifyChatChanged();
            }
        });
    }

    /// <summary>收尾：把正文渲染成 Markdown 文档（失败退回纯文本），然后滚到底。</summary>
    private void FinishTurn(ChatMessage turn)
    {
        if (!string.IsNullOrEmpty(turn.Text))
        {
            try
            {
                turn.Document = MarkdownRenderer.Render(turn.Text);
            }
            catch (Exception ex)
            {
                _logger.Warn("Markdown 渲染失败，退回纯文本", new Dictionary<string, object?>
                {
                    ["error"] = ex.Message
                });
            }
        }

        NotifyChatChanged();
    }
}
