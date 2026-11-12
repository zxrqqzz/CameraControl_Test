using System.Text.Json;
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
internal static partial class Program
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

        // ── 脚本回放模式（无模型，验证"外部调用能否真的动软件"）──
        //
        // 走的是和 LLM 完全相同的链路：
        //   能力清单(catalog) → CommandBinder → ICommandBus → 实际动作
        // 唯一区别是"由谁决定调用哪个能力"——这里用脚本代替模型。
        string? replayPath = GetValue(args, "--replay");

        if (replayPath is not null)
            return await ProgramReplay.ReplayAsync(services, replayPath);

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
        // 配置是"多条并存 + 可切换"的。
        // key 只能在新增时写入，之后不可读、不可改（只能删条目重建）。
        if (args.Contains("--list", StringComparer.Ordinal))
        {
            return ListProfiles(store);
        }

        if (GetValue(args, "--use") is string useId)
        {
            return store.SetActive(useId)
                ? PrintProfiles(store, $"已切换到 {useId}。")
                : Fail($"没有 id 为 {useId} 的配置。");
        }

        if (GetValue(args, "--remove") is string removeId)
        {
            return store.RemoveProfile(removeId)
                ? PrintProfiles(store, $"已删除 {removeId}（其密钥一并删除）。")
                : Fail($"没有 id 为 {removeId} 的配置。");
        }

        if (args.Contains("--models", StringComparer.Ordinal))
        {
            return await ListModelsAsync(store).ConfigureAwait(false);
        }

        // 默认动作 = 新增一条配置
        string? baseUrl = GetValue(args, "--base-url");
        string? model = GetValue(args, "--model");

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            Console.Error.WriteLine("新增配置需要 --base-url 与 --model：");
            Console.Error.WriteLine("  Agent --configure --add --name DeepSeek \\");
            Console.Error.WriteLine("        --base-url api.deepseek.com --model deepseek-chat --api-key-stdin");
            return 2;
        }

        string? apiKey = null;

        if (args.Contains("--api-key-stdin", StringComparer.Ordinal))
        {
            Console.Write("请输入 API Key（回车确认，输入不回显）：");
            apiKey = ReadSecret();
            Console.WriteLine();
        }
        else if (GetValue(args, "--api-key") is string inlineKey && inlineKey.Length > 0)
        {
            Console.Error.WriteLine("警告：通过命令行传 API Key 会留在命令历史里，建议改用 --api-key-stdin。");
            apiKey = inlineKey;
        }

        LlmStrength strength = ParseStrength(GetValue(args, "--strength"));

        try
        {
            LlmProfile profile = store.AddProfile(
                GetValue(args, "--name") ?? string.Empty,
                baseUrl,
                model,
                apiKey,
                strength);

            Console.WriteLine(
                $"已新增配置 {profile.Id}：{profile.Name} → {profile.BaseUrl}，模型 {profile.Model}，" +
                $"强度 {profile.Strength}，key {(profile.HasKey ? profile.KeyHint : "(未设置)")}。");
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }

        return PrintProfiles(store, null);
    }

    /// <summary>列出全部配置，标出当前生效的一条。</summary>
    private static int ListProfiles(LlmSettingsStore store) => PrintProfiles(store, null);

    private static int PrintProfiles(LlmSettingsStore store, string? message)
    {
        if (message is not null) Console.WriteLine(message);

        LlmSettings settings = store.Load();
        LlmProfile? active = store.ActiveProfile();

        Console.WriteLine();
        Console.WriteLine("  id            当前  名称             baseUrl                          模型                强度      key");

        foreach (LlmProfile profile in settings.Profiles)
        {
            string mark = profile.Id == active?.Id ? "  *  " : "     ";

            Console.WriteLine(
                $"  {profile.Id,-12} {mark} {Truncate(profile.Name, 14),-14} " +
                $"{Truncate(profile.BaseUrl, 32),-32} {Truncate(profile.Model, 18),-18} " +
                $"{profile.Strength,-8} {profile.KeyHint ?? "(无)"}");
        }

        if (settings.Profiles.Count == 0) Console.WriteLine("  （还没有任何配置）");

        Console.WriteLine();
        Console.WriteLine($"  状态：{(store.IsConfigured() ? "已就绪" : "未完成配置（需要模型 + 可解密的 key）")}");

        if (active is not null && store.GetApiKey(active.Id) is null && active.HasKey)
        {
            Console.WriteLine("  注意：当前配置的 key 无法解密（可能换了 Windows 账户/机器），请删除后重新新增。");
        }

        return store.IsConfigured() ? 0 : 1;
    }

    /// <summary>拉取当前配置的模型清单（拉不到则展示内置兜底）。</summary>
    private static async Task<int> ListModelsAsync(LlmSettingsStore store)
    {
        LlmProfile? profile = store.ActiveProfile();

        if (profile is null) return Fail("还没有配置，先新增一条再拉模型。");

        string? apiKey = store.GetApiKey(profile.Id);

        using var catalog = new ModelCatalogService();

        ModelCatalogResult result = await catalog
            .FetchAsync(profile.BaseUrl, apiKey, CancellationToken.None)
            .ConfigureAwait(false);

        Console.WriteLine($"模型清单来源：{(result.Source == ModelCatalogSource.Remote ? "服务端 /models" : "内置兜底")}");

        if (result.Error is not null) Console.WriteLine($"  {result.Error}");

        Console.WriteLine();

        foreach (ModelEntry entry in result.Models)
        {
            string reasoning = entry.SupportsReasoningEffort ? " 可调推理力度" : string.Empty;

            Console.WriteLine(
                $"  [{entry.Tier,-8}] {entry.Id,-24}{reasoning}" +
                (entry.Note is null ? string.Empty : $"  — {entry.Note}"));
        }

        return 0;
    }

    private static LlmStrength ParseStrength(string? text) => text?.ToLowerInvariant() switch
    {
        "low" => LlmStrength.Low,
        "high" => LlmStrength.High,
        "medium" or null => LlmStrength.High,
        _ => LlmStrength.High
    };

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
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
/// <summary>
/// 脚本回放：读 JSON 数组，逐条通过命令总线执行。
///
/// JSON 形如：
/// [
///   { "capability": "device.list", "arguments": {} },
///   { "capability": "capture.single", "arguments": { "cameraId": "...", "includeImage": true } }
/// ]
/// </summary>
internal static class ProgramReplay
{
    public static async Task<int> ReplayAsync(ServiceProvider services, string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"脚本文件不存在：{path}");
            return 2;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement steps = document.RootElement;

        if (steps.ValueKind != JsonValueKind.Array)
        {
            Console.Error.WriteLine("脚本必须是 JSON 数组。");
            return 2;
        }

        var bus = services.GetRequiredService<CameraControl.Application.Commands.ICommandBus>();
        var actor = CameraControl.Application.Commands.Actor.Ai("scripted-driver", "replay");

        Console.WriteLine("=== 脚本回放（与 AI 走同一条命令总线）===");
        Console.WriteLine();

        int failures = 0;
        int index = 0;

        foreach (JsonElement step in steps.EnumerateArray())
        {
            index++;

            string capability = step.GetProperty("capability").GetString() ?? string.Empty;
            JsonElement arguments = step.TryGetProperty("arguments", out JsonElement a)
                ? a
                : default;

            CameraControl.Application.Commands.ICommand? command =
                new CameraControl.Application.Cua.DefaultCommandBinder().Bind(capability, arguments, out string? bindError);

            if (command is null)
            {
                failures++;
                Console.WriteLine($"[{index}] {capability} → 绑定失败：{bindError}");
                continue;
            }

            CameraControl.Application.Commands.CommandResult result =
                await bus.ExecuteAsync(command, actor).ConfigureAwait(false);

            // 允许脚本声明"这一步应该被拒绝"（例如物理动作必须人工确认），
            // 这样风险策略的验证不会被算成失败。
            string? expectedError = step.TryGetProperty("expectError", out JsonElement e)
                ? e.GetString()
                : null;

            if (!result.Ok)
            {
                bool expected = expectedError is not null
                    && string.Equals(expectedError, result.Error?.Code, StringComparison.Ordinal);

                if (expected)
                {
                    Console.WriteLine(
                        $"[{index}] {capability} → 已按预期拒绝 [{result.Error?.Code}]");
                }
                else
                {
                    failures++;
                    Console.WriteLine(
                        $"[{index}] {capability} → 失败 [{result.Error?.Code}] {result.Error?.Message}");
                }

                continue;
            }

            if (expectedError is not null)
            {
                failures++;
                Console.WriteLine($"[{index}] {capability} → 预期拒绝但成功了（{expectedError}）");
                continue;
            }

            Console.WriteLine($"[{index}] {capability} → OK ({result.Elapsed.TotalMilliseconds:F0} ms)");
            PrintValue(result.Data, "       ");

            // 可选：把返回的 base64 PNG 落盘，用于肉眼确认"AI 真的拿到了图"
            string? savePng = step.TryGetProperty("savePng", out JsonElement sp) ? sp.GetString() : null;

            if (savePng is not null) SavePng(result.Data, savePng);
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"全部通过（{index} 步）。软件可被外部调用驱动。"
            : $"完成但有 {failures} 步失败（共 {index} 步）。");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>把结果里的 PngBase64 写文件（显式要求才落盘，默认不存）。</summary>
    private static void SavePng(object? value, string path)
    {
        try
        {
            string json = JsonSerializer.Serialize(value, JsonOptions);
            using JsonDocument document = JsonDocument.Parse(json);

            string? base64 = FindBase64(document.RootElement);

            if (base64 is null)
            {
                Console.WriteLine($"       （没有 PNG 可保存：{path}）");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path, Convert.FromBase64String(base64));

            Console.WriteLine($"       已保存 PNG：{path}（{new FileInfo(path).Length:N0} 字节）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"       保存 PNG 失败：{ex.Message}");
        }
    }

    /// <summary>在结果对象里找 PngBase64（可能在 frame 子对象里）。</summary>
    private static string? FindBase64(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals("PngBase64") && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();

                string? nested = FindBase64(property.Value);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? nested = FindBase64(item);
                if (nested is not null) return nested;
            }
        }

        return null;
    }

    /// <summary>打印结果摘要（PNG 只报长度，避免刷屏）。</summary>
    private static void PrintValue(object? value, string indent)
    {
        if (value is null) { Console.WriteLine($"{indent}(null)"); return; }

        string text = JsonSerializer.Serialize(value, JsonOptions);

        if (text.Length > 400) text = text[..400] + " …";

        Console.WriteLine($"{indent}{text}");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}