using System.Text.Json;
using CameraControl.Application.Commands;
using CameraControl.Application.Cua;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Capabilities;

/// <summary>cua.getCapabilities 命令：导出能力清单（AI 的白名单真相源）。</summary>
/// <param name="Format">导出格式：Json / Markdown。</param>
public sealed record CuaGetCapabilitiesCommand(CapabilityCatalogFormat Format = CapabilityCatalogFormat.Json)
    : ICommand
{
    public const string Id = "cua.getCapabilities";

    public string CapabilityId => Id;
}

/// <summary>
/// cua.getCapabilities 能力实现。
///
/// 清单由 <see cref="CapabilityRegistry"/> 实时生成，与实现同源——
/// 新增能力会自动出现在清单里，不存在"文档说能做但代码没有"的情况。
/// </summary>
public sealed class CuaGetCapabilitiesCapability : ICapabilityHandler<CuaGetCapabilitiesCommand>
{
    private readonly ICapabilityCatalog _catalog;
    private readonly IClock _clock;

    public CuaGetCapabilitiesCapability(ICapabilityCatalog catalog, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(clock);

        _catalog = catalog;
        _clock = clock;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CuaGetCapabilitiesCommand.Id,
        Title: "获取能力清单",
        Description: "返回本软件对外暴露的全部能力：id、说明、风险等级、是否需要人工确认、参数 JSON Schema。" +
                     "AI 应先调用本能力，再根据清单决定调用哪些命令。清单是白名单唯一真相源。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CuaGetCapabilitiesCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "format": { "enum": ["Json", "Markdown"], "default": "Json",
                        "description": "Markdown 更省 token，适合放进系统提示；Json 便于程序解析" }
          }
        }
        """);

    public Task<object?> HandleAsync(
        CuaGetCapabilitiesCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        CapabilityCatalogExport export = CapabilityCatalogExporter.Export(
            _catalog.All,
            _clock.UtcNow);

        return command.Format == CapabilityCatalogFormat.Markdown
            ? Task.FromResult<object?>(CapabilityCatalogExporter.ToMarkdown(export))
            : Task.FromResult<object?>(export);
    }
}
