using CameraControl.Application.Cua;
using CameraControl.Application.Diagnostics;
using CameraControl.Composition;
using CameraControl.Infrastructure.Ai;
using Microsoft.Extensions.DependencyInjection;

namespace CameraControl.Tools.Agent;

/// <summary>
/// AI 操控入口（CUA 命令行向导）。
///
/// 两个用途：
/// 1. 配置模型 API（baseUrl / model / apiKey，密钥经 DPAPI 加密存储）；
/// 2. 用自然语言下达任务，由 AI 通过命令总线调用本软件能力。
///
/// 安全：AI 的所有动作都经过 CommandBus 与 DefaultRiskPolicy，
/// 物理/破坏性操作会被拒绝（返回 CONFIRMATION_REQUIRED），不会绕过。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        using ServiceProvider services = ServiceRegistration.Build(
            new CompositionOptions(
                ConsoleLogging: false,
                MinimumLogLevel: LogLevel.Warning));

        var settingsStore = services.GetRequiredService<LlmSettingsStore>();

        // ── 配置模式 ──────────────────────────────────────────
        if (args.Contains("--configure", StringComparer.Ordinal))
            return await ConfigureAsync(settingsStore, args);

        // ── 运行模式 ──────────────────────────────────────────
        string? goal = GetValue(args, "--goal") ?? GetValue(args, "-g");
        if (string.IsNullOrWhiteSpace(goal))
        {
            PrintUsage();
            return 2;
        }

        if (!settingsStore.IsConfigured())
        {
            Console.Error.WriteLine("尚未配置模型 API。先用 --configure 设置：");
            Console.Error.WriteLine("  Agent --configure --base-url https://api.deepseek.com/v1 \\");
            Console.Error.WriteLine("        --model deepseek-chat --api-key-stdin");
            return 3;
        }

        int maxSteps = int.TryParse(GetValue(args, "--max-steps"), out int parsed) ? parsed : 8;
        bool showSteps = args.Contains("--show-steps", StringComparer.Ordinal);

        var agent = services.GetRequiredService<IAiAgent>();

        Console.WriteLine($"[模型] {agent.ProviderName}");
        Console.WriteLine($"[目标] {goal}");
        Console.WriteLine();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        AiTaskResult result = await agent.RunAsync(
            new AiTaskRequest(goal, maxSteps),
            CameraControl.Application.Commands.Actor.Ai("agent-cli", Guid.NewGuid().ToString("N")[..8]),
            CancellationToken.None);

        stopwatch.Stop();

        if (showSteps || !result.Completed)
        {
            foreach (AiStep step in result.Steps)
            {
                if (!string.IsNullOrWhiteSpace(step.AssistantText))
                    Console.WriteLine($"  [{step.Step}] 模型: {Trim(step.AssistantText, 200)}");

                foreach (AiToolInvocation invocation in step.Invocations)
                {
                    string mark = invocation.Ok ? "OK  " : "FAIL";
                    Console.WriteLine(
                        $"  [{step.Step}] {mark} {invocation.CapabilityId} " +
                        $"({invocation.ElapsedMs:F0}ms)" +
                        (invocation.ErrorCode is null ? "" : $"  {invocation.ErrorCode}"));
                }
            }

            Console.WriteLine();
        }

        if (result.Completed)
        {
            Console.WriteLine("=== 回复 ===");
            Console.WriteLine(result.Answer);
        }
        else
        {
            Console.Error.WriteLine($"=== 未完成（{result.StopReason}）===");
            Console.Error.WriteLine(result.Error);
        }

        Console.WriteLine();
        Console.WriteLine(
            $"用时 {stopwatch.Elapsed.TotalSeconds:F1}s，工具调用 {result.ToolCallCount} 次" +
            (result.Usage is null ? "" : $"，token {result.Usage.TotalTokens}"));

        return result.Completed ? 0 : 1;
    }

    private static async Task<int> ConfigureAsync(LlmSettingsStore store, string[] args)
    {
        LlmSettings settings = store.Load();
        bool changed = false;

        if (GetValue(args, "--base-url") is string baseUrl && baseUrl.Length > 0)
        {
            settings.BaseUrl = baseUrl;
            changed = true;
        }

        if (GetValue(args, "--model") is string model && model.Length > 0)
        {
            settings.Model = model;
            changed = true;
        }

        if (GetValue(args, "--provider-name") is string providerName && providerName.Length > 0)
        {
            settings.ProviderName = providerName;
            changed = true;
        }

        if (GetValue(args, "--timeout-seconds") is string timeoutText
            && int.TryParse(timeoutText, out int timeout))
        {
            settings.TimeoutSeconds = timeout;
            changed = true;
        }

        if (changed) store.Save(settings);

        // API Key：优先从 stdin 读，避免留在命令历史里
        if (args.Contains("--api-key-stdin", StringComparer.Ordinal))
        {
            Console.Write("请输入 API Key（回车确认，输入不回显）：");

            string? key = ReadSecret();
            Console.WriteLine();

            if (!string.IsNullOrWhiteSpace(key))
            {
                store.SetApiKey(key);
                Console.WriteLine("API Key 已加密保存（DPAPI，仅当前用户可解密）。");
            }
            else
            {
                Console.WriteLine("未输入，保持不变。");
            }
        }
        else if (GetValue(args, "--api-key") is string inlineKey && inlineKey.Length > 0)
        {
            Console.Error.WriteLine("警告：通过命令行传 API Key 会留在命令历史里，建议改用 --api-key-stdin。");
            store.SetApiKey(inlineKey);
        }

        LlmSettings current = store.Load();
        string? existingKey = store.GetApiKey();

        Console.WriteLine();
        Console.WriteLine($"  BaseUrl : {current.BaseUrl}");
        Console.WriteLine($"  Model   : {(string.IsNullOrWhiteSpace(current.Model) ? "(未设置)" : current.Model)}");
        Console.WriteLine($"  Timeout : {current.TimeoutSeconds}s");
        Console.WriteLine($"  API Key : {(existingKey is null ? "(未设置)" : Mask(existingKey))}");
        Console.WriteLine($"  状态    : {(store.IsConfigured() ? "已就绪" : "未完成配置")}");

        await Task.CompletedTask;
        return store.IsConfigured() ? 0 : 1;
    }

    private static string? ReadSecret()
    {
        var builder = new System.Text.StringBuilder();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter) break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0) builder.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar)) builder.Append(key.KeyChar);
        }

        return builder.ToString();
    }

    private static string Mask(string key) =>
        key.Length <= 8 ? "****" : $"{key[..4]}…{key[^4..]}（{key.Length} 字符）";

    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static string? GetValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        Agent — 用自然语言操控本软件（CUA）

        配置模型 API：
          Agent --configure --base-url <url> --model <name> [--api-key-stdin]

          选项：
            --base-url <url>       OpenAI 兼容的 base URL（如 https://api.deepseek.com/v1）
            --model <name>         模型名
            --api-key-stdin        从标准输入读 API Key（推荐，不进命令历史）
            --api-key <key>        直接传 Key（会留在命令历史里，不推荐）
            --timeout-seconds <n>  单次请求超时
            --provider-name <s>    显示名（仅日志用）

        下达任务：
          Agent --goal "<自然语言目标>" [--max-steps 8] [--show-steps]

          示例：
            Agent --goal "列出当前所有相机，并告诉我哪一台可以采集"
            Agent --goal "用 Integrated Camera 拍一张，报告分辨率"

        安全说明：
          AI 的全部操作都经过命令总线与风险策略；
          物理/破坏性操作会被拒绝（返回 CONFIRMATION_REQUIRED），不会绕过。
        """);
    }
}
