using System.Text.Json;
using CameraControl.Application.Capabilities;
using CameraControl.Application.Commands;
using CameraControl.Application.Cua;
using CameraControl.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace CameraControl.Tools.Capabilities;

/// <summary>
/// 能力清单导出工具（CUA 地基）。
///
/// 用途：
/// 1. 灌进 LLM 的系统提示（Markdown 格式更省 token）；
/// 2. 人工审计"这个软件到底对外暴露了哪些动作、风险等级是什么"；
/// 3. CI 里做快照比对，防止能力被悄悄增删。
///
/// 之所以走命令总线而不是直接读注册表：让导出行为本身也进审计日志。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        CapabilityCatalogFormat format = CapabilityCatalogFormat.Json;
        string? output = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--format" or "-f":
                    if (i + 1 >= args.Length) return Usage("--format 缺少值");
                    string value = args[++i];
                    format = value.Equals("markdown", StringComparison.OrdinalIgnoreCase)
                        ? CapabilityCatalogFormat.Markdown
                        : value.Equals("json", StringComparison.OrdinalIgnoreCase)
                            ? CapabilityCatalogFormat.Json
                            : CapabilityCatalogFormat.Json;
                    break;

                case "--out" or "-o":
                    if (i + 1 >= args.Length) return Usage("--out 缺少值");
                    output = args[++i];
                    break;

                case "--help" or "-h":
                    return Usage(null);

                default:
                    return Usage($"未知参数：{args[i]}");
            }
        }

        using ServiceProvider services = ServiceRegistration.Build(
            new CompositionOptions(ConsoleLogging: false, MinimumLogLevel: CameraControl.Application.Diagnostics.LogLevel.Warning));

        var bus = services.GetRequiredService<ICommandBus>();

        CommandResult result = await bus.ExecuteAsync(
            new CuaGetCapabilitiesCommand(format),
            Actor.Human("capabilities-cli"));

        if (!result.Ok)
        {
            Console.Error.WriteLine($"导出失败：{result.Error?.Code} {result.Error?.Message}");
            return 1;
        }

        string text = result.Data switch
        {
            string markdown => markdown,
            CapabilityCatalogExport export => JsonSerializer.Serialize(
                export,
                new JsonSerializerOptions { WriteIndented = true }),
            _ => result.Data?.ToString() ?? string.Empty
        };

        if (!string.IsNullOrWhiteSpace(output))
        {
            string path = Path.GetFullPath(output);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(path, text);
            Console.WriteLine($"已写入：{path}（{text.Length} 字符）");
        }
        else
        {
            Console.WriteLine(text);
        }

        return 0;
    }

    private static int Usage(string? error)
    {
        if (error is not null) Console.Error.WriteLine(error);

        Console.WriteLine("""
        能力清单导出（CUA 白名单真相源）

        用法：
          Capabilities [--format json|markdown] [--out <path>]

        选项：
          --format, -f   json（默认，结构化）| markdown（紧凑，适合塞进 LLM 提示）
          --out,    -o   输出文件路径；省略则打印到控制台

        示例：
          Capabilities -f markdown
          Capabilities -f json --out docs\能力快照.json
        """);

        return error is null ? 0 : 2;
    }
}
