using CameraControl.Application.Commands;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Capabilities;

/// <summary>device.list 命令：枚举当前可用相机。</summary>
public sealed record DeviceListCommand : ICommand
{
    public const string Id = "device.list";

    public string CapabilityId => Id;
}

/// <summary>设备投影。</summary>
public sealed record DeviceDto(
    string Id,
    string DisplayName,
    string Vendor,
    string Model,
    string? SerialNumber,
    string Kind,
    string Mount,
    bool IsInfraredOnly,
    bool SupportsHardwareTrigger,
    bool CanCapture);

/// <summary>device.list 能力实现（只读）。</summary>
public sealed class DeviceListCapability : ICapabilityHandler<DeviceListCommand>
{
    private readonly ICameraProvider _provider;

    public DeviceListCapability(ICameraProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: DeviceListCommand.Id,
        Title: "列出相机",
        Description: "枚举当前可用相机，只读取设备信息，不打开设备。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(DeviceListCommand),
        ParametersJsonSchema: """{ "type": "object", "properties": {} }""");

    public async Task<object?> HandleAsync(
        DeviceListCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CameraDescriptor> devices = await _provider
            .DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);

        return devices
            .Select(d => new DeviceDto(
                d.Id.Value,
                d.DisplayName,
                d.Vendor,
                d.Model,
                d.SerialNumber,
                d.Kind.ToString(),
                d.Mount.ToString(),
                d.IsInfraredOnly,
                d.SupportsHardwareTrigger,
                d.CanCapture))
            .ToList();
    }
}
