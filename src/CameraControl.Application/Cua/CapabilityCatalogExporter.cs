using System.Text;
using System.Text.Json;

namespace CameraControl.Application.Cua;

/// <summary>能力清单导出格式。</summary>
public enum CapabilityCatalogFormat
{
    /// <summary>结构化 JSON（机器/AI 直接消费）。</summary>
    Json = 0,

    /// <summary>紧凑 Markdown（塞进 LLM 上下文更省 token）。</summary>
    Markdown = 1
}

/// <summary>导出的单个能力。</summary>
/// <param name="Id">能力 id。</param>
/// <param name="Title">标题。</param>
/// <param name="Description">给 LLM 阅读的说明。</param>
/// <param name="Risk">风险等级名。</param>
/// <param name="RequiresConfirmation">是否需要人工确认。</param>
/// <param name="Parameters">
/// 参数 schema（已解析为 JSON 对象）。schema 非法时为 null，
/// 并在 <paramref name="SchemaError"/> 说明原因——**不静默丢弃**。
/// </param>
/// <param name="SchemaError">schema 解析失败原因。</param>
public sealed record ExportedCapability(
    string Id,
    string Title,
    string Description,
    string Risk,
    bool RequiresConfirmation,
    JsonElement? Parameters,
    string? SchemaError = null);

/// <summary>一次能力清单导出。</summary>
/// <param name="GeneratedAtUtc">导出时间。</param>
/// <param name="Count">能力数量。</param>
/// <param name="Capabilities">能力列表（按 id 升序，保证可复现）。</param>
public sealed record CapabilityCatalogExport(
    DateTimeOffset GeneratedAtUtc,
    int Count,
    IReadOnlyList<ExportedCapability> Capabilities);

/// <summary>
/// 能力清单导出器。
///
/// 这是 CUA 的"白名单真相源"：AI 只能看到这里列出的能力，
/// 也只能通过 <c>ICommandBus</c> 调用它们。清单与实现同源，不会漂移。
/// </summary>
public static class CapabilityCatalogExporter
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>把能力注册表导出为结构化对象。</summary>
    public static CapabilityCatalogExport Export(
        IEnumerable<Commands.CapabilityDescriptor> descriptors,
        DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var exported = new List<ExportedCapability>();

        foreach (Commands.CapabilityDescriptor descriptor in descriptors
                     .OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            JsonElement? parameters = null;
            string? schemaError = null;

            if (!string.IsNullOrWhiteSpace(descriptor.ParametersJsonSchema))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(descriptor.ParametersJsonSchema, ParseOptions);
                    parameters = document.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    // 不静默丢弃：把原因带出去，便于发现写错的 schema
                    schemaError = ex.Message;
                }
            }

            exported.Add(new ExportedCapability(
                descriptor.Id,
                descriptor.Title,
                descriptor.Description,
                descriptor.Risk.ToString(),
                descriptor.RequiresConfirmation,
                parameters,
                schemaError));
        }

        return new CapabilityCatalogExport(generatedAtUtc, exported.Count, exported);
    }

    /// <summary>渲染为紧凑 Markdown（适合塞进 LLM 系统提示）。</summary>
    public static string ToMarkdown(CapabilityCatalogExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        var builder = new StringBuilder();
        builder.AppendLine($"# Capabilities ({export.Count})");
        builder.AppendLine();

        foreach (ExportedCapability capability in export.Capabilities)
        {
            string confirm = capability.RequiresConfirmation ? "requires-confirmation" : "no-confirmation";
            builder.AppendLine($"- **{capability.Id}** [{capability.Risk}, {confirm}] {capability.Title}");
            builder.AppendLine($"  {capability.Description}");

            if (capability.Parameters is JsonElement schema)
                builder.AppendLine($"  params: {Compact(schema)}");
            else if (capability.SchemaError is not null)
                builder.AppendLine($"  params: <schema 解析失败: {capability.SchemaError}>");

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>压成单行 JSON，省 token。</summary>
    private static string Compact(JsonElement element) =>
        JsonSerializer.Serialize(element);
}
