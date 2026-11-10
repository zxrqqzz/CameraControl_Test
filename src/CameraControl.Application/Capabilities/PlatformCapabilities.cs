using System.IO.Compression;
using System.Text.Json;
using CameraControl.Application.Commands;
using CameraControl.Domain.Cameras;
using CameraControl.Domain.Ports;

namespace CameraControl.Application.Capabilities;

// ── CUA 授权模式 ────────────────────────────────────────

/// <summary>AI 授权模式。</summary>
public enum CuaAuthorizationMode
{
    /// <summary>只读：AI 只能观察，不能改变任何状态。</summary>
    Observe = 0,

    /// <summary>确认：可逆操作直接执行，物理/破坏性需人工确认。</summary>
    Confirm = 1,

    /// <summary>全自动：所有能力都可执行（仍需审计）。</summary>
    Full = 2
}

/// <summary>AI 授权状态（内存态）。</summary>
public interface ICuaAuthorization
{
    /// <summary>当前模式。</summary>
    CuaAuthorizationMode Mode { get; }

    /// <summary>是否已被要求停止。</summary>
    bool StopRequested { get; }

    /// <summary>设置模式。</summary>
    void SetMode(CuaAuthorizationMode mode);

    /// <summary>请求停止（下一次能力调用前应检查）。</summary>
    void RequestStop();

    /// <summary>清除停止标记。</summary>
    void ClearStop();
}

/// <summary>默认实现。</summary>
public sealed class CuaAuthorization : ICuaAuthorization
{
    private CuaAuthorizationMode _mode = CuaAuthorizationMode.Confirm;
    private volatile bool _stop;

    public CuaAuthorizationMode Mode => _mode;

    public bool StopRequested => _stop;

    public void SetMode(CuaAuthorizationMode mode) => _mode = mode;

    public void RequestStop() => _stop = true;

    public void ClearStop() => _stop = false;
}

// ── CUA 命令 ────────────────────────────────────────────

/// <summary>cua.getAuthorizationMode：读取授权模式。</summary>
public sealed record CuaGetAuthorizationModeCommand : ICommand
{
    public const string Id = "cua.getAuthorizationMode";

    public string CapabilityId => Id;
}

/// <summary>cua.stop：请求停止 AI 操作。</summary>
public sealed record CuaStopCommand : ICommand
{
    public const string Id = "cua.stop";

    public string CapabilityId => Id;
}

/// <summary>cua.requestElevation：请求提升授权（当前不支持）。</summary>
public sealed record CuaRequestElevationCommand(string Reason) : ICommand
{
    public const string Id = "cua.requestElevation";

    public string CapabilityId => Id;
}

/// <summary>cua.getAuthorizationMode 实现。</summary>
public sealed class CuaGetAuthorizationModeCapability : ICapabilityHandler<CuaGetAuthorizationModeCommand>
{
    private readonly ICuaAuthorization _authorization;

    public CuaGetAuthorizationModeCapability(ICuaAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        _authorization = authorization;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CuaGetAuthorizationModeCommand.Id,
        Title: "读取 AI 授权模式",
        Description: "返回当前授权模式与停止标记。模式：Observe（只读）/ Confirm（物理需确认）/ Full（全自动）。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CuaGetAuthorizationModeCommand),
        ParametersJsonSchema: """{ "type": "object", "properties": {} }""");

    public Task<object?> HandleAsync(
        CuaGetAuthorizationModeCommand command,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>(new
        {
            mode = _authorization.Mode.ToString(),
            stopRequested = _authorization.StopRequested,
            policy = _authorization.Mode switch
            {
                CuaAuthorizationMode.Observe => "只能调用只读能力",
                CuaAuthorizationMode.Confirm => "可逆能力直接执行；物理/破坏性需人工确认",
                _ => "全部能力可执行（仍写审计日志）"
            }
        });
}

/// <summary>cua.stop 实现。</summary>
public sealed class CuaStopCapability : ICapabilityHandler<CuaStopCommand>
{
    private readonly ICuaAuthorization _authorization;
    private readonly Capture.ICapturePreviewService _preview;

    public CuaStopCapability(ICuaAuthorization authorization, Capture.ICapturePreviewService preview)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(preview);

        _authorization = authorization;
        _preview = preview;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CuaStopCommand.Id,
        Title: "请求停止 AI 操作",
        Description: "设置停止标记并停止所有连续预览。已下发的运动不会自动停止，需要显式调用 robot.stop。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CuaStopCommand),
        ParametersJsonSchema: """{ "type": "object", "properties": {} }""");

    public async Task<object?> HandleAsync(
        CuaStopCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        _authorization.RequestStop();

        foreach (string cameraId in _preview.Active)
            await _preview.StopAsync(new CameraId(cameraId)).ConfigureAwait(false);

        return new { stopped = true, previewsStopped = true };
    }
}

/// <summary>cua.requestElevation 实现（拒绝）。</summary>
public sealed class CuaRequestElevationCapability : ICapabilityHandler<CuaRequestElevationCommand>
{
    public CapabilityDescriptor Descriptor { get; } = new(
        Id: CuaRequestElevationCommand.Id,
        Title: "请求提升授权",
        Description:
            "AI 不能自行提权。当前返回拒绝：授权模式只能由人在界面上修改，" +
            "否则等于 AI 自己给自己发权限。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(CuaRequestElevationCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": { "reason": { "type": "string", "description": "为什么要提权" } },
          "required": ["reason"]
        }
        """);

    public Task<object?> HandleAsync(
        CuaRequestElevationCommand command,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>(new
        {
            granted = false,
            reason = "AI 不能自行提升授权；请在界面「AI」页由人工调整授权模式。",
            requestedReason = command.Reason
        });
}

// ── 设备补充 ────────────────────────────────────────────

/// <summary>设备别名存储。</summary>
public interface IDeviceAliasStore
{
    /// <summary>取别名；无则 null。</summary>
    string? GetAlias(string cameraId);

    /// <summary>设置别名；null 表示清除。</summary>
    void SetAlias(string cameraId, string? alias);

    /// <summary>全部别名。</summary>
    IReadOnlyDictionary<string, string> All { get; }
}

/// <summary>基于设置文件保存的别名存储。</summary>
public sealed class DeviceAliasStore : IDeviceAliasStore
{
    private const string Key = "deviceAliases";

    private readonly ISettingsStore _settings;

    public DeviceAliasStore(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public IReadOnlyDictionary<string, string> All =>
        _settings.TryLoad<Dictionary<string, string>>(Key, out var map) && map is not null ? map : new();

    public string? GetAlias(string cameraId) => All.GetValueOrDefault(cameraId);

    public void SetAlias(string cameraId, string? alias)
    {
        var map = new Dictionary<string, string>(All, StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(alias)) map.Remove(cameraId);
        else map[cameraId] = alias;

        _settings.Save(Key, map);
    }
}

/// <summary>device.get：读取单台设备信息。</summary>
public sealed record DeviceGetCommand(string CameraId) : ICommand
{
    public const string Id = "device.get";

    public string CapabilityId => Id;
}

/// <summary>device.rename：设置显示别名。</summary>
public sealed record DeviceRenameCommand(string CameraId, string? Alias) : ICommand
{
    public const string Id = "device.rename";

    public string CapabilityId => Id;
}

/// <summary>device.get 实现。</summary>
public sealed class DeviceGetCapability : ICapabilityHandler<DeviceGetCommand>
{
    private readonly ICameraProvider _provider;
    private readonly IDeviceAliasStore _aliases;

    public DeviceGetCapability(ICameraProvider provider, IDeviceAliasStore aliases)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(aliases);

        _provider = provider;
        _aliases = aliases;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: DeviceGetCommand.Id,
        Title: "读取设备信息",
        Description: "按 id 返回一台设备的描述；不存在时 found=false。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(DeviceGetCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "cameraId": { "type": "string" } }, "required": ["cameraId"] }
        """);

    public async Task<object?> HandleAsync(
        DeviceGetCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CameraDescriptor> devices =
            await _provider.DiscoverAsync(cancellationToken).ConfigureAwait(false);

        CameraDescriptor? found = devices.FirstOrDefault(
            d => string.Equals(d.Id.Value, command.CameraId, StringComparison.Ordinal));

        if (found is null) return new { found = false, cameraId = command.CameraId };

        return new
        {
            found = true,
            cameraId = found.Id.Value,
            displayName = _aliases.GetAlias(found.Id.Value ?? string.Empty) ?? found.DisplayName,
            vendor = found.Vendor,
            model = found.Model,
            kind = found.Kind.ToString(),
            isInfrared = found.IsInfraredOnly,
            canCapture = found.CanCapture
        };
    }
}

/// <summary>device.rename 实现。</summary>
public sealed class DeviceRenameCapability : ICapabilityHandler<DeviceRenameCommand>
{
    private readonly IDeviceAliasStore _aliases;

    public DeviceRenameCapability(IDeviceAliasStore aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        _aliases = aliases;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: DeviceRenameCommand.Id,
        Title: "重命名设备",
        Description: "给设备设置显示别名（只改显示，不改设备本身）。alias 为空表示清除。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(DeviceRenameCommand),
        ParametersJsonSchema: """
        {
          "type": "object",
          "properties": {
            "cameraId": { "type": "string" },
            "alias": { "type": "string", "description": "为空则清除别名" }
          },
          "required": ["cameraId"]
        }
        """);

    public Task<object?> HandleAsync(
        DeviceRenameCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        _aliases.SetAlias(command.CameraId, command.Alias);

        return Task.FromResult<object?>(new
        {
            cameraId = command.CameraId,
            alias = _aliases.GetAlias(command.CameraId)
        });
    }
}

// ── 系统补充 ────────────────────────────────────────────

/// <summary>system.getSettings：读取非敏感配置。</summary>
public sealed record SystemGetSettingsCommand : ICommand
{
    public const string Id = "system.getSettings";

    public string CapabilityId => Id;
}

/// <summary>system.getEnvironment：运行环境。</summary>
public sealed record SystemGetEnvironmentCommand : ICommand
{
    public const string Id = "system.getEnvironment";

    public string CapabilityId => Id;
}

/// <summary>system.exportDiagnostics：导出诊断包。</summary>
public sealed record SystemExportDiagnosticsCommand(string? OutputPath = null) : ICommand
{
    public const string Id = "system.exportDiagnostics";

    public string CapabilityId => Id;
}

/// <summary>system.getEnvironment 实现。</summary>
public sealed class SystemGetEnvironmentCapability : ICapabilityHandler<SystemGetEnvironmentCommand>
{
    private readonly IClock _clock;

    public SystemGetEnvironmentCapability(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: SystemGetEnvironmentCommand.Id,
        Title: "运行环境",
        Description: "返回操作系统、架构、.NET 版本、数据目录等只读信息。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(SystemGetEnvironmentCommand),
        ParametersJsonSchema: """{ "type": "object", "properties": {} }""");

    public Task<object?> HandleAsync(
        SystemGetEnvironmentCommand command,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>(new
        {
            os = Environment.OSVersion.ToString(),
            architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            machine = Environment.MachineName,
            processorCount = Environment.ProcessorCount,
            dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CameraControl"),
            utcNow = _clock.UtcNow
        });
}

/// <summary>system.getSettings 实现。</summary>
public sealed class SystemGetSettingsCapability : ICapabilityHandler<SystemGetSettingsCommand>
{
    // 说明：AI 的 BaseUrl/Model/是否已配置 Key 由界面层持有，
    // 不在这里暴露——Application 层拿不到 Infrastructure 的 LlmSettingsStore，
    // 也不应该为了这一个只读展示去穿透分层。

    public CapabilityDescriptor Descriptor { get; } = new(
        Id: SystemGetSettingsCommand.Id,
        Title: "读取设置",
        Description: "返回可公开的配置项。**不含任何密钥**（API Key 只返回是否已配置）。",
        Risk: RiskLevel.ReadOnly,
        CommandType: typeof(SystemGetSettingsCommand),
        ParametersJsonSchema: """{ "type": "object", "properties": {} }""");

    public Task<object?> HandleAsync(
        SystemGetSettingsCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<object?>(new
        {
            dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CameraControl"),
            secretsExposed = false,
            note = "密钥类配置不通过本能力暴露；请在界面查看。"
        });
    }
}

/// <summary>system.exportDiagnostics 实现。</summary>
public sealed class SystemExportDiagnosticsCapability : ICapabilityHandler<SystemExportDiagnosticsCommand>
{
    public CapabilityDescriptor Descriptor { get; } = new(
        Id: SystemExportDiagnosticsCommand.Id,
        Title: "导出诊断包",
        Description:
            "把日志与环境信息打包成 zip。默认写到数据目录 diagnostics 下。" +
            "不包含图像与密钥。",
        Risk: RiskLevel.Reversible,
        CommandType: typeof(SystemExportDiagnosticsCommand),
        ParametersJsonSchema: """
        { "type": "object", "properties": { "outputPath": { "type": "string", "description": "可选，zip 路径" } } }
        """);

    public Task<object?> HandleAsync(
        SystemExportDiagnosticsCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CameraControl");

        string target = string.IsNullOrWhiteSpace(command.OutputPath)
            ? Path.Combine(dataDirectory, "diagnostics", $"diag-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip")
            : command.OutputPath;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        using (var archive = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            string logDirectory = Path.Combine(dataDirectory, "logs");

            if (Directory.Exists(logDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(logDirectory, "*.log"))
                    archive.CreateEntryFromFile(file, $"logs/{Path.GetFileName(file)}");
            }

            ZipArchiveEntry environment = archive.CreateEntry("environment.json");
            using var writer = new StreamWriter(environment.Open());
            writer.Write(JsonSerializer.Serialize(new
            {
                os = Environment.OSVersion.ToString(),
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                machine = Environment.MachineName,
                at = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        return Task.FromResult<object?>(new { exported = true, path = target });
    }
}
