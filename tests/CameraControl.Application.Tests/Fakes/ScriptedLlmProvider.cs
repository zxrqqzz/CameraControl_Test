using CameraControl.Application.Cua;

namespace CameraControl.Application.Tests.Fakes;

/// <summary>
/// 脚本化的假 LLM：按顺序返回预设响应，并记录所有请求。
/// 测试 AI 循环不需要联网，也不能依赖真实模型的不确定性。
/// </summary>
public sealed class ScriptedLlmProvider : ILlmProvider
{
    private readonly Queue<Func<LlmChatRequest, LlmChatResponse>> _script = new();

    public string Name => "脚本假模型";

    public List<LlmChatRequest> Requests { get; } = [];

    /// <summary>排入一轮"助手直接答复"。</summary>
    public ScriptedLlmProvider ThenAnswer(string text)
    {
        _script.Enqueue(_ => new LlmChatResponse(LlmMessage.Assistant(text)));
        return this;
    }

    /// <summary>排入一轮"助手请求调用工具"。</summary>
    public ScriptedLlmProvider ThenCallTool(string callId, string toolName, string argumentsJson)
    {
        _script.Enqueue(_ => new LlmChatResponse(LlmMessage.Assistant(
            null,
            [new LlmToolCall(callId, toolName, argumentsJson)])));
        return this;
    }

    /// <summary>排入一轮"助手一次调用多个工具"。</summary>
    public ScriptedLlmProvider ThenCallTools(params LlmToolCall[] calls)
    {
        _script.Enqueue(_ => new LlmChatResponse(LlmMessage.Assistant(null, calls)));
        return this;
    }

    /// <summary>排入一次抛出异常。</summary>
    public ScriptedLlmProvider ThenThrow(LlmException exception)
    {
        _script.Enqueue(_ => throw exception);
        return this;
    }

    public Task<LlmChatResponse> ChatAsync(LlmChatRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (_script.Count == 0)
            throw new LlmException(LlmErrorCodes.Protocol, "脚本已用尽（模型还在请求下一轮）。");

        return Task.FromResult(_script.Dequeue()(request));
    }
}
