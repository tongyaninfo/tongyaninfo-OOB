using System.Globalization;
using System.Text.Json;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Infrastructure.Service.impl.OutOfBand;

/// <summary>发现资源、统一库存/指标和电源语义；厂商差异由派生驱动选择。</summary>
public abstract partial class RedfishDriverBase(Func<OutOfBandConnection, HttpClient>? clientFactory = null, IExceptionRecorder? exceptions = null) : IOutOfBandService
{
    public abstract string ControllerType { get; }

    private RedfishResult<RedfishTransport> CreateTransport(OutOfBandConnection connection, bool closeCommandConnections = false) => RedfishTransport.Create(connection,
        clientFactory, ControllerType == "iLO4", exceptions is null ? null : diagnostic => exceptions.RecordAsync(diagnostic.Cause,
            new() { Kind = "DeviceFailure", Source = "Driver", Operation = "DisposeSession", ErrorCode = diagnostic.Code, Message = diagnostic.SafeMessage }), closeCommandConnections);

    public async Task<ConnectionTestResult> TestConnectionAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default)
    {
        var created = CreateTransport(connection);
        if (!created.Success) return new() { ErrorCode = created.Failure!.Code, ErrorMessage = created.Failure.SafeMessage, Diagnostics = [created.Failure] };
        await using var transport = created.Data;
        var discovered = await DiscoverAsync(transport, cancellationToken);
        if (!discovered.Success) return new() { ErrorCode = discovered.Failure!.Code, ErrorMessage = discovered.Failure.SafeMessage, Warnings = transport.Warnings, Diagnostics = transport.Diagnostics };
        var resources = discovered.Data;
        var identity = Identity(resources);
        if (ControllerMismatch(identity))
            return new() { Success = false, Identity = identity, ErrorCode = "DeviceTypeMismatch", ErrorMessage = "设备品牌与选择的带外类型不一致。", Diagnostics = transport.Diagnostics };
        var capabilities = await CapabilitiesAsync(transport, resources, cancellationToken);
        return new() { Success = true, Identity = identity, Capabilities = capabilities, Warnings = transport.Warnings, Diagnostics = transport.Diagnostics };
    }

    public async Task<ConnectionTestResult> ReadPowerCapabilitiesAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default)
    {
        var created = CreateTransport(connection, closeCommandConnections: ControllerType == "iLO4");
        if (!created.Success) return new() { ErrorCode = created.Failure!.Code, ErrorMessage = created.Failure.SafeMessage, Diagnostics = [created.Failure] };
        await using var transport = created.Data;
        // 电源预检只依赖唯一主机和动作声明，不为读取功率、温度或控制器资料增加连接与失败面。
        var discovered = await DiscoverAsync(transport, cancellationToken, systemOnly: true);
        if (!discovered.Success) return new() { ErrorCode = discovered.Failure!.Code, ErrorMessage = discovered.Failure.SafeMessage, Warnings = transport.Warnings, Diagnostics = transport.Diagnostics };
        var identity = Identity(discovered.Data);
        if (ControllerMismatch(identity))
            return new() { Identity = identity, ErrorCode = "DeviceTypeMismatch", ErrorMessage = "设备品牌与选择的带外类型不一致。", Diagnostics = transport.Diagnostics };
        var capabilities = await PowerCapabilitiesAsync(transport, discovered.Data, cancellationToken);
        if (!capabilities.Success)
            return new() { Identity = identity, ErrorCode = capabilities.Failure!.Code, ErrorMessage = capabilities.Failure.SafeMessage, Warnings = transport.Warnings, Diagnostics = transport.Diagnostics };
        return new() { Success = true, Identity = identity, Capabilities = capabilities.Data, Warnings = transport.Warnings, Diagnostics = transport.Diagnostics };
    }

    private bool ControllerMismatch(DeviceIdentity identity) => ControllerType == "iLO4" && identity.Manufacturer?.Contains("Dell", StringComparison.OrdinalIgnoreCase) == true
        || ControllerType == "iDRAC8" && (identity.Manufacturer?.Contains("HPE", StringComparison.OrdinalIgnoreCase) == true
            || identity.Manufacturer?.Contains("HP", StringComparison.OrdinalIgnoreCase) == true);

    public async Task<DeviceCapabilities> GetCapabilitiesAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default)
    {
        var created = CreateTransport(connection);
        if (!created.Success) return new() { Warnings = [created.Failure!.SafeMessage], Diagnostics = [created.Failure] };
        await using var transport = created.Data;
        var discovered = await DiscoverAsync(transport, cancellationToken);
        return discovered.Success ? await CapabilitiesAsync(transport, discovered.Data, cancellationToken)
            : new() { Warnings = [.. transport.Warnings, discovered.Failure!.SafeMessage], Diagnostics = transport.Diagnostics };
    }

    public async Task<DeviceInventory> ReadInventoryAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default)
    {
        var created = CreateTransport(connection);
        if (!created.Success) return new() { Warnings = [created.Failure!.SafeMessage], Diagnostics = [created.Failure] };
        await using var transport = created.Data;
        var discovered = await DiscoverAsync(transport, cancellationToken);
        if (!discovered.Success) return new() { Warnings = [.. transport.Warnings, discovered.Failure!.SafeMessage], Diagnostics = transport.Diagnostics };
        var resources = discovered.Data;
        var components = new List<HardwareComponent>();
        await AppendCollectionAsync(transport, resources.System, "Processor", "CPU", components, cancellationToken, "Processors");
        await AppendCollectionAsync(transport, resources.System, "Memory", "内存", components, cancellationToken, "Memory");
        // 部分旧固件只公开汇总，保留已知数据并明确逐项信息缺失，不能猜测 CPU 或 DIMM。
        if (!components.Any(component => component.Kind == "Processor") && JsonValue.Get(resources.System, "ProcessorSummary") is { } processorSummary)
            components.Add(Component(processorSummary, "ProcessorSummary", "处理器汇总"));
        if (!components.Any(component => component.Kind == "Memory") && JsonValue.Get(resources.System, "MemorySummary") is { } memorySummary)
            components.Add(Component(memorySummary, "MemorySummary", "内存汇总"));
        AppendBatteries(resources.System, components);
        if (resources.Chassis is { } chassis) AppendBatteries(chassis, components);
        await AppendStorageAsync(transport, resources.System, components, cancellationToken);
        var power = await transport.TryGetAsync(resources.PowerLink, "电源信息", cancellationToken);
        if (power is { } powerValue)
        {
            var index = 0;
            foreach (var supply in JsonValue.Array(powerValue, "PowerSupplies"))
                components.Add(Component(supply, "PowerSupply", $"电源 {++index}"));
        }
        if (!components.Any(component => component.Kind == "Processor")) transport.Warnings.Add("CPU：设备未提供可读取的逐项处理器信息。");
        if (!components.Any(component => component.Kind == "Memory")) transport.Warnings.Add("内存：设备未提供可读取的逐条内存信息。");
        if (!ContainsKind(components, "Battery")) transport.Warnings.Add("电池：设备未提供可读取的电池或控制器后备电源信息。");
        return new() { Identity = Identity(resources), Components = components, Warnings = transport.Warnings, Diagnostics = transport.Diagnostics };
    }

    public async Task<TelemetryReading> ReadTelemetryAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default)
    {
        var created = CreateTransport(connection);
        if (!created.Success) return new() { Warnings = [created.Failure!.SafeMessage], Diagnostics = [created.Failure] };
        await using var transport = created.Data;
        var discovered = await DiscoverAsync(transport, cancellationToken);
        if (!discovered.Success) return new() { Warnings = [.. transport.Warnings, discovered.Failure!.SafeMessage], Diagnostics = transport.Diagnostics };
        return (await TelemetryAsync(transport, discovered.Data, cancellationToken)) with { Identity = Identity(discovered.Data) };
    }

    public async Task<PowerCommandResult> ExecutePowerActionAsync(OutOfBandConnection connection, PowerAction action, CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeBeforeSend = null)
    {
        if (!Enum.IsDefined(action)) return Unsupported();
        // iLO4 的写入不得复用发现阶段的 GET 连接；整轮采用与 UID 相同的独立连接策略。
        var created = CreateTransport(connection, closeCommandConnections: ControllerType == "iLO4");
        if (!created.Success) return new() { Status = "Rejected", ErrorCode = created.Failure!.Code, Message = created.Failure.SafeMessage, Diagnostics = [created.Failure] };
        await using var transport = created.Data;
        try
        {
            var discovered = await DiscoverAsync(transport, cancellationToken, systemOnly: true);
            if (!discovered.Success) return new() { Status = "Rejected", ErrorCode = discovered.Failure!.Code, Message = discovered.Failure.SafeMessage, Diagnostics = transport.Diagnostics };
            var resources = discovered.Data;
            var actualPowerState = JsonValue.Text(resources.System, "PowerState");
            var poweredOn = string.Equals(actualPowerState, "On", StringComparison.OrdinalIgnoreCase);
            var poweredOff = string.Equals(actualPowerState, "Off", StringComparison.OrdinalIgnoreCase);
            // 短按保留物理按钮语义，开机、关机状态都允许；未知或转换态仍必须阻断。
            // 其他动作继续按前置状态校验，关机后不能尝试重启或再次强制断电。
            var permitted = action switch { PowerAction.On => poweredOff, PowerAction.PushPowerButton => poweredOn || poweredOff, _ => poweredOn };
            if (!permitted)
                return new() { Status = "Rejected", ErrorCode = "PowerStateConflict", Message = "主机当前电源状态不允许所选动作，请刷新后核对；未发送命令。", Diagnostics = transport.Diagnostics };
            var capability = await PowerCapabilitiesAsync(transport, resources, cancellationToken);
            if (!capability.Success)
                return new() { Status = "Rejected", ErrorCode = capability.Failure!.Code,
                    Message = "电源能力读取失败，未发送命令：" + capability.Failure.SafeMessage, Diagnostics = transport.Diagnostics };
            if (!capability.Data.PowerActions.Contains(action)) return Unsupported() with { Diagnostics = transport.Diagnostics };
            // 上面的共同读取已核验动作结构和主机归属；发送层仍再次检查同源与目标路径。
            var target = JsonValue.Text(resources.System, "Actions", "#ComputerSystem.Reset", "target")!;
            var resetType = action.ToString();
            var systemLink = JsonValue.Link(resources.System)!;
            if (authorizeBeforeSend is not null)
            {
                ServiceResult<bool> authorization;
                try { authorization = await authorizeBeforeSend(cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    transport.Diagnostics.Add(new("AUTHORIZATION_UNAVAILABLE", "发送前权限核验暂不可用，电源命令未发送。", "SystemFailure", error));
                    return new() { Status = "Rejected", ErrorCode = "AUTHORIZATION_UNAVAILABLE", Message = "发送前权限核验暂不可用，电源命令未发送。", Diagnostics = transport.Diagnostics };
                }
                if (!authorization.Success || !authorization.Data)
                    return new() { Status = "Rejected", ErrorCode = authorization.ErrorCode ?? "FORBIDDEN",
                        Message = authorization.ErrorMessage ?? "没有电源操作权限，命令未发送。", Diagnostics = transport.Diagnostics };
            }
            // SendPowerAsync 自己掌握 sent 边界：发出后的取消已返回 Unknown，不会落入下面的发送前分支。
            return await transport.SendPowerAsync(target, systemLink, resetType, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            var cancelled = cancellationToken.IsCancellationRequested;
            var code = cancelled ? "PowerNotSentCancelled" : "Timeout";
            var message = cancelled ? "电源命令发送前已取消，未向设备发送命令。" : "电源命令发送前设备读取超时，未向设备发送命令。";
            // 驱动无法判断传入 token 是调用者取消还是应用总截止时间；后者由应用层按自己的 deadline 提升为故障。
            transport.Diagnostics.Add(new(code, message, cancelled ? "Business" : "DeviceFailure", exception));
            return new() { Status = "Rejected", ErrorCode = code, Message = message, Diagnostics = transport.Diagnostics };
        }
    }

    private static PowerCommandResult Unsupported() => new() { Status = "Rejected", ErrorCode = "PowerActionNotSupported", Message = "设备没有声明支持所选电源动作。" };

    private async Task<DeviceCapabilities> CapabilitiesAsync(RedfishTransport transport, Resources resources, CancellationToken cancellationToken)
    {
        var power = await PowerCapabilitiesAsync(transport, resources, cancellationToken);
        var telemetry = await TelemetryAsync(transport, resources, cancellationToken);
        return (power.Success ? power.Data : new DeviceCapabilities()) with
        {
            SupportsInventory = true,
            SupportedMetrics = telemetry.Metrics.Where(metric => metric.Availability != "Unsupported").Select(metric => metric.MetricCode).Distinct().ToArray(),
            Warnings = transport.Warnings, Diagnostics = transport.Diagnostics
        };
    }

    private async Task<RedfishResult<DeviceCapabilities>> PowerCapabilitiesAsync(RedfishTransport transport, Resources resources, CancellationToken cancellationToken)
    {
        if (!JsonValue.Has(resources.System, "Actions")) return RedfishResult<DeviceCapabilities>.Ok(new());
        var actions = JsonValue.Get(resources.System, "Actions");
        if (actions?.ValueKind != JsonValueKind.Object)
            return PowerCapabilityFailure<DeviceCapabilities>(transport, "InvalidPowerCapabilities", "设备动作声明结构无效，无法确认电源能力。");
        if (!JsonValue.Has(actions.Value, "#ComputerSystem.Reset")) return RedfishResult<DeviceCapabilities>.Ok(new());
        var actionObject = JsonValue.Get(actions.Value, "#ComputerSystem.Reset");
        if (actionObject?.ValueKind != JsonValueKind.Object)
            return PowerCapabilityFailure<DeviceCapabilities>(transport, "InvalidPowerCapabilities", "设备电源动作声明结构无效。");
        var targetValue = JsonValue.Get(actionObject.Value, "target");
        var target = targetValue?.ValueKind == JsonValueKind.String ? targetValue.Value.GetString() : null;
        var systemLink = JsonValue.Link(resources.System);
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(systemLink))
            return PowerCapabilityFailure<DeviceCapabilities>(transport, "InvalidPowerTarget", "设备电源动作或主机资源地址缺失，无法核验目标。");
        var resolved = transport.Resolve(target);
        var system = transport.Resolve(systemLink);
        if (!resolved.Success || !system.Success) return RedfishResult<DeviceCapabilities>.Fail(resolved.Failure ?? system.Failure!);
        if (!resolved.Data.AbsolutePath.TrimEnd('/').Equals(system.Data.AbsolutePath.TrimEnd('/') + "/Actions/ComputerSystem.Reset", StringComparison.OrdinalIgnoreCase)
            || !system.Data.AbsolutePath.StartsWith("/redfish/v1/Systems/", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(resolved.Data.Query) || !string.IsNullOrEmpty(resolved.Data.Fragment)
            || !string.IsNullOrEmpty(system.Data.Query) || !string.IsNullOrEmpty(system.Data.Fragment))
            return PowerCapabilityFailure<DeviceCapabilities>(transport, "InvalidPowerTarget", "设备电源动作地址不属于已发现的主机，无法确认电源能力。");
        var values = await PowerValuesAsync(transport, actionObject.Value, cancellationToken);
        if (!values.Success) return RedfishResult<DeviceCapabilities>.Fail(values.Failure!);
        return RedfishResult<DeviceCapabilities>.Ok(new()
        {
            PowerActions = Enum.GetValues<PowerAction>().Where(action => values.Data.Contains(action.ToString(), StringComparer.Ordinal)).ToArray(),
            Warnings = transport.Warnings, Diagnostics = transport.Diagnostics
        });
    }

    private static RedfishResult<T> PowerCapabilityFailure<T>(RedfishTransport transport, string code, string message)
    {
        var failure = new DeviceDiagnostic(code, message, "DeviceFailure");
        transport.Diagnostics.Add(failure);
        transport.Warnings.Add(message);
        return RedfishResult<T>.Fail(failure);
    }

    private static async Task<RedfishResult<string[]>> PowerValuesAsync(RedfishTransport transport, JsonElement action, CancellationToken cancellationToken)
    {
        if (JsonValue.Has(action, "ResetType@Redfish.AllowableValues"))
            return ReadPowerValues(transport, JsonValue.Get(action, "ResetType@Redfish.AllowableValues"));
        var infoReference = JsonValue.Get(action, "@Redfish.ActionInfo");
        var infoLink = infoReference?.ValueKind == JsonValueKind.String ? infoReference.Value.GetString() : null;
        if (string.IsNullOrWhiteSpace(infoLink))
            return PowerCapabilityFailure<string[]>(transport, "InvalidPowerCapabilities", "设备未提供有效的电源动作参数声明。");
        // 已声明的动作说明读取失败不等于不支持；保留原诊断并阻断发送，避免误导用户换动作重试。
        var info = await transport.GetAsync(infoLink, cancellationToken);
        if (!info.Success)
        {
            transport.Warnings.Add("电源动作说明：" + info.Failure!.SafeMessage);
            return RedfishResult<string[]>.Fail(info.Failure);
        }
        var parameters = JsonValue.Get(info.Data, "Parameters");
        if (parameters?.ValueKind != JsonValueKind.Array)
            return PowerCapabilityFailure<string[]>(transport, "InvalidPowerCapabilities", "设备电源动作说明未提供有效参数数组。");
        var resetParameters = parameters.Value.EnumerateArray().Where(value => JsonValue.Text(value, "Name") == "ResetType").ToArray();
        if (resetParameters.Length != 1)
            return PowerCapabilityFailure<string[]>(transport, "InvalidPowerCapabilities", "设备电源动作说明缺少唯一的 ResetType 参数。");
        return ReadPowerValues(transport, JsonValue.Get(resetParameters[0], "AllowableValues"));
    }

    private static RedfishResult<string[]> ReadPowerValues(RedfishTransport transport, JsonElement? values)
    {
        // 只有结构正确的空数组才表示已确认无动作；缺字段、错误类型和混入无效元素均是读取失败。
        if (values?.ValueKind != JsonValueKind.Array || values.Value.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
            return PowerCapabilityFailure<string[]>(transport, "InvalidPowerCapabilities", "设备电源动作允许值不是有效字符串数组。");
        return RedfishResult<string[]>.Ok(values.Value.EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    private async Task<RedfishResult<Resources>> DiscoverAsync(RedfishTransport transport, CancellationToken cancellationToken, bool systemOnly = false)
    {
        var fetched = await transport.GetAsync("/redfish/v1/", cancellationToken);
        if (!fetched.Success) return RedfishResult<Resources>.Fail(fetched.Failure!);
        var root = fetched.Data;
        var systemsLink = JsonValue.ResourceLink(root, "Systems");
        if (systemsLink is null) return DiscoveryFailure(transport, "SystemNotFound", "设备没有提供主机资源入口。");
        var diagnosticCount = transport.Diagnostics.Count;
        var systems = await transport.CollectionAsync(systemsLink, "主机信息", cancellationToken, requireCompleteSystemCollection: systemOnly);
        // 主机集合不完整时不能把剩下的一台推断为唯一目标，尤其不能据此发送电源动作。
        var systemsFailure = transport.Diagnostics.Skip(diagnosticCount).FirstOrDefault();
        if (systemsFailure is not null) return RedfishResult<Resources>.Fail(systemsFailure);
        if (systems.Count == 0)
            return DiscoveryFailure(transport, "SystemUnavailable", "设备主机资源暂时无法读取。");
        if (systems.Count > 1) return DiscoveryFailure(transport, "AmbiguousSystem", "设备包含多个主机，请先明确受管理主机，避免对错误主机执行操作。");
        var system = systems[0];
        if (systemOnly && string.IsNullOrWhiteSpace(JsonValue.Link(system)))
            return DiscoveryFailure(transport, "InvalidSystemCollection", "设备未返回完整主机资源，无法确认电源能力。");
        if (systemOnly) return RedfishResult<Resources>.Ok(new(root, system, null, null, null, null));
        var managers = await transport.CollectionAsync(JsonValue.ResourceLink(root, "Managers"), "带外控制器信息", cancellationToken);
        JsonElement? manager = managers.Count > 0 ? managers[0] : null;
        var chassisLink = JsonValue.ResourceLink(system, "Chassis");
        var chassisLinks = JsonValue.Array(system, "Links", "Chassis").Select(JsonValue.Link).Where(link => link is not null).ToArray();
        IReadOnlyList<JsonElement> chassisCollection;
        if (chassisLinks.Length > 0)
        {
            var matched = await transport.TryGetAsync(chassisLinks[0], "机箱信息", cancellationToken);
            chassisCollection = matched is null ? [] : [matched.Value];
        }
        else chassisCollection = await transport.CollectionAsync(chassisLink ?? JsonValue.ResourceLink(root, "Chassis"), "机箱信息", cancellationToken);
        JsonElement? chassis = chassisCollection.Count > 0 ? chassisCollection[0] : null;
        return RedfishResult<Resources>.Ok(new(root, system, manager, chassis,
            chassis is null ? null : JsonValue.ResourceLink(chassis.Value, "Power"),
            chassis is null ? null : JsonValue.ResourceLink(chassis.Value, "Thermal")));
    }

    private static RedfishResult<Resources> DiscoveryFailure(RedfishTransport transport, string code, string message)
    {
        var diagnostic = new DeviceDiagnostic(code, message);
        transport.Diagnostics.Add(diagnostic);
        return RedfishResult<Resources>.Fail(diagnostic);
    }

    private DeviceIdentity Identity(Resources resources)
    {
        var manager = resources.Manager;
        var rootManager = JsonValue.Array(resources.Root, "Oem", "Hp", "Manager").FirstOrDefault();
        return new()
        {
            Manufacturer = JsonValue.Text(resources.System, "Manufacturer"), Model = JsonValue.Text(resources.System, "Model"),
            SerialNumber = JsonValue.Text(resources.System, "SerialNumber") ?? JsonValue.Text(resources.System, "SKU"),
            HostName = JsonValue.Text(resources.System, "HostName"), PowerState = JsonValue.Text(resources.System, "PowerState"),
            ControllerType = ControllerType,
            ControllerHostName = manager is { } value ? JsonValue.Text(value, "HostName") ?? JsonValue.Text(rootManager, "HostName") : JsonValue.Text(rootManager, "HostName"),
            FirmwareVersion = manager is { } managerValue ? JsonValue.Text(managerValue, "FirmwareVersion") ?? JsonValue.Text(rootManager, "ManagerFirmwareVersion") : JsonValue.Text(rootManager, "ManagerFirmwareVersion")
        };
    }

    private static async Task AppendCollectionAsync(RedfishTransport transport, JsonElement owner, string kind, string name, List<HardwareComponent> components, CancellationToken cancellationToken, params string[] linkNames)
    {
        var resources = await transport.CollectionAsync(JsonValue.ResourceLink(owner, linkNames), name, cancellationToken);
        var index = 0;
        foreach (var resource in resources) components.Add(Component(resource, kind, $"{name} {++index}"));
    }

    private static void AppendBatteries(JsonElement owner, List<HardwareComponent> components)
    {
        foreach (var vendor in new[] { "Hp", "Hpe", "Dell" })
        {
            foreach (var battery in JsonValue.Array(owner, "Oem", vendor, "Battery"))
                components.Add(Component(battery, "Battery", $"电池 {components.Count(component => component.Kind == "Battery") + 1}"));
            var batteryObject = JsonValue.Get(owner, "Oem", vendor, "Batteries");
            if (batteryObject?.ValueKind == JsonValueKind.Array)
                foreach (var battery in batteryObject.Value.EnumerateArray()) components.Add(Component(battery, "Battery", "电池"));
        }
    }

    private static HardwareComponent Component(JsonElement resource, string kind, string fallback, IReadOnlyList<HardwareComponent>? children = null)
    {
        var attributes = new Dictionary<string, string?>();
        foreach (var property in resource.EnumerateObject().Where(property => !property.Name.StartsWith('@')
            && !Sensitive(property.Name) && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
            attributes[property.Name] = property.Value.ToString();
        var oem = JsonValue.Get(resource, "Oem");
        if (oem is { } oemValue) FlattenAttributes(oemValue, "Oem", attributes, 0);
        var capacity = JsonValue.Number(resource, "CapacityBytes") ?? JsonValue.Number(resource, "CapacityMiB") * 1024 * 1024
            ?? JsonValue.Number(resource, "SizeMB") * 1024 * 1024 ?? JsonValue.Number(resource, "TotalSystemMemoryGiB") * 1024 * 1024 * 1024
            ?? JsonValue.Number(resource, "CapacityGB") * 1_000_000_000;
        return new()
        {
            Key = JsonValue.Key(resource, kind + ":" + fallback), Kind = kind,
            Name = JsonValue.Text(resource, "Name") ?? JsonValue.Text(resource, "DeviceLocator") ?? JsonValue.Text(resource, "Location") ?? fallback,
            Model = JsonValue.Text(resource, "Model") ?? JsonValue.Text(resource, "ProcessorModel"),
            Manufacturer = JsonValue.Text(resource, "Manufacturer"), SerialNumber = JsonValue.Text(resource, "SerialNumber"),
            Status = JsonValue.Health(resource) ?? JsonValue.Text(resource, "DIMMStatus"), CapacityBytes = capacity is >= 0 and <= long.MaxValue ? (long)capacity : null,
            Attributes = attributes, Children = children ?? []
        };
    }

    private static void FlattenAttributes(JsonElement value, string prefix, Dictionary<string, string?> attributes, int depth)
    {
        if (value.ValueKind != JsonValueKind.Object || depth >= 4) return;
        foreach (var property in value.EnumerateObject().Where(property => !property.Name.StartsWith('@') && !Sensitive(property.Name)))
        {
            if (property.Value.ValueKind == JsonValueKind.Object) FlattenAttributes(property.Value, prefix + "." + property.Name, attributes, depth + 1);
            else if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                attributes[prefix + "." + property.Name] = property.Value.ToString();
        }
    }

    private static bool Sensitive(string name) => new[] { "Password", "Secret", "Token", "Credential", "Community" }
        .Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsKind(IEnumerable<HardwareComponent> components, string kind) => components.Any(component => component.Kind == kind || ContainsKind(component.Children, kind));

    private static async Task AppendStorageAsync(RedfishTransport transport, JsonElement system, List<HardwareComponent> components, CancellationToken cancellationToken)
    {
        var storageLink = JsonValue.ResourceLink(system, "Storage", "SimpleStorage");
        var storage = await transport.CollectionAsync(storageLink, "存储控制器", cancellationToken);
        foreach (var item in storage)
        {
            var drives = new List<HardwareComponent>();
            foreach (var reference in JsonValue.Array(item, "Drives"))
            {
                var drive = JsonValue.HasDetails(reference) ? reference : await transport.TryGetAsync(JsonValue.Link(reference), "物理磁盘", cancellationToken);
                if (drive is not null) drives.Add(Component(drive.Value, "Drive", $"磁盘 {drives.Count + 1}"));
            }
            foreach (var drive in JsonValue.Array(item, "Devices")) drives.Add(Component(drive, "Drive", $"磁盘 {drives.Count + 1}"));
            var controllerArray = JsonValue.Array(item, "StorageControllers").ToArray();
            if (controllerArray.Length > 0)
            {
                // 旧版 Storage 资源通常一项代表一个控制器，磁盘归属在该项的 Drives 中。
                foreach (var controller in controllerArray)
                    components.Add(Component(controller, "StorageController", JsonValue.Text(item, "Name") ?? "存储控制器", drives));
            }
            else components.Add(Component(item, "StorageController", "存储控制器", drives));
        }
        // iLO4 的 SmartStorage 是正式 OEM 资源，沿返回链接读取而不假定 Systems/1。
        var smartLink = JsonValue.ResourceLink(system, "SmartStorage");
        var smart = await transport.TryGetAsync(smartLink, "智能存储", cancellationToken);
        if (smart is not null)
        {
            var controllers = await transport.CollectionAsync(JsonValue.ResourceLink(smart.Value, "ArrayControllers", "HostBusAdapters"), "阵列控制器", cancellationToken);
            foreach (var controller in controllers)
            {
                var children = new List<HardwareComponent>();
                var physical = await transport.CollectionAsync(JsonValue.ResourceLink(controller, "DiskDrives", "PhysicalDrives"), "物理磁盘", cancellationToken);
                foreach (var drive in physical) children.Add(Component(drive, "Drive", $"磁盘 {children.Count + 1}"));
                var backupPower = JsonValue.Text(controller, "BackupPowerSourceStatus");
                if (backupPower is not null)
                    children.Add(new() { Key = JsonValue.Key(controller, "controller") + "/backup-power", Kind = "Battery", Name = "控制器后备电源", Status = backupPower,
                        Attributes = new() { ["BackupPowerSourceStatus"] = backupPower, ["Description"] = "设备返回的电池、电容或后备电源状态" } });
                components.Add(Component(controller, "StorageController", "阵列控制器", children));
            }
        }
        if (!components.Any(component => component.Kind == "StorageController")) transport.Warnings.Add("存储：设备未提供可读取的控制器或磁盘信息。");
    }

    private async Task<TelemetryReading> TelemetryAsync(RedfishTransport transport, Resources resources, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var readings = new List<MetricReading>();
        var power = await transport.TryGetAsync(resources.PowerLink, "实时功率", cancellationToken);
        var thermal = await transport.TryGetAsync(resources.ThermalLink, "温度和风扇", cancellationToken);
        if (power is { } powerValue) AppendPower(powerValue, readings, now);
        else
        {
            readings.Add(new() { SensorKey = "system", Name = "整机功率", MetricCode = "power.watts", Unit = "W", Availability = resources.PowerLink is null ? "Unsupported" : "TemporarilyUnavailable", ObservedAt = now });
            transport.Warnings.Add("实时功率当前不可读取，未用零值代替。");
        }
        if (thermal is { } thermalValue) AppendThermal(thermalValue, readings, now);
        else transport.Warnings.Add("温度和风扇当前不可读取，未用零值代替。");
        await AppendVendorTelemetryAsync(transport, thermal, readings, now, cancellationToken);
        return new() { ObservedAt = now, Metrics = readings, Warnings = transport.Warnings, Diagnostics = transport.Diagnostics };
    }

    /// <summary>厂商补充读取复用当前连接和证书策略，连接测试与后台采集使用同一能力来源。</summary>
    private protected virtual Task AppendVendorTelemetryAsync(RedfishTransport transport, JsonElement? thermal,
        List<MetricReading> readings, DateTimeOffset observedAt, CancellationToken cancellationToken) => Task.CompletedTask;

    private protected static string FanSensorKey(JsonElement fan, int index) => "fan:" +
        (JsonValue.Text(fan, "MemberId") ?? JsonValue.Text(fan, "Id") ?? index.ToString(CultureInfo.InvariantCulture));

    private static void AppendPower(JsonElement power, List<MetricReading> readings, DateTimeOffset now)
    {
        var controls = JsonValue.Array(power, "PowerControl").ToArray();
        var control = controls.Length > 0 ? controls[0] : power;
        var watts = JsonValue.Number(control, "PowerConsumedWatts");
        var capacity = JsonValue.Number(control, "PowerCapacityWatts");
        AddReading(readings, control, "system", "整机功率", "power.watts", watts, "W", now, "PowerConsumedWatts");
        AddReading(readings, control, "system", "可用功率容量", "power.capacity.watts", capacity, "W", now, "PowerCapacityWatts");
        readings.Add(new() { SensorKey = "system", Name = "功率容量占比", MetricCode = "power.capacity.percent", Value = watts is not null && capacity > 0 ? watts / capacity * 100 : null,
            Unit = "%", Source = "Derived", Availability = watts is not null && capacity > 0 ? "Available" : "Unsupported", ObservedAt = now });
        readings.Add(new() { SensorKey = "system", Name = "散热量换算", MetricCode = "power.btuPerHour", Value = watts * 3.412141633,
            Unit = "BTU/h", Source = "Derived", Availability = watts is null ? "TemporarilyUnavailable" : "Available", ObservedAt = now });
        var metrics = JsonValue.Get(control, "PowerMetrics");
        if (metrics is { } powerMetrics)
        {
            AddReading(readings, powerMetrics, "system", "设备窗口平均功率", "power.average.watts", JsonValue.Number(powerMetrics, "AverageConsumedWatts"), "W", now, "AverageConsumedWatts");
            AddReading(readings, powerMetrics, "system", "设备窗口峰值功率", "power.peak.watts", JsonValue.Number(powerMetrics, "MaxConsumedWatts"), "W", now, "MaxConsumedWatts");
            AddReading(readings, powerMetrics, "system", "设备窗口最低功率", "power.minimum.watts", JsonValue.Number(powerMetrics, "MinConsumedWatts"), "W", now, "MinConsumedWatts");
            AddReading(readings, powerMetrics, "system", "设备统计窗口", "power.window.minutes", JsonValue.Number(powerMetrics, "IntervalInMin"), "min", now, "IntervalInMin");
        }
        var index = 0;
        foreach (var supply in JsonValue.Array(power, "PowerSupplies"))
        {
            index++;
            var key = "psu:" + (JsonValue.Text(supply, "MemberId") ?? JsonValue.Text(supply, "Id") ?? index.ToString(CultureInfo.InvariantCulture));
            var name = JsonValue.Text(supply, "Name") ?? $"电源 {index}";
            AddReading(readings, supply, key, name + "输出功率", "psu.output.watts", JsonValue.Number(supply, "PowerOutputWatts") ?? JsonValue.Number(supply, "LastPowerOutputWatts")
                ?? JsonValue.Number(supply, "Oem", "Hp", "AveragePowerOutputWatts"), "W", now, "PowerOutputWatts", "LastPowerOutputWatts");
            AddReading(readings, supply, key, name + "输入功率", "psu.input.watts", JsonValue.Number(supply, "PowerInputWatts"), "W", now, "PowerInputWatts");
            AddReading(readings, supply, key, name + "容量", "psu.capacity.watts", JsonValue.Number(supply, "PowerCapacityWatts"), "W", now, "PowerCapacityWatts");
            AddReading(readings, supply, key, name + "输入电压", "psu.input.volts", JsonValue.Number(supply, "LineInputVoltage"), "V", now, "LineInputVoltage");
        }
        // 累计值仅接收明确带单位的设备字段；缺少设备计数时不伪造累计能耗。
        var energyWh = JsonValue.Number(control, "EnergyConsumedWh") ?? JsonValue.Number(control, "Oem", "Dell", "EnergyConsumedWh");
        var energyKwh = JsonValue.Number(control, "EnergyConsumedKWh") ?? JsonValue.Number(control, "Oem", "Dell", "EnergyConsumedKWh");
        readings.Add(new() { SensorKey = "system", Name = "设备累计能耗", MetricCode = "power.energy.kwh", Value = energyKwh ?? energyWh / 1000,
            Unit = "kWh", Availability = energyKwh is not null || energyWh is not null ? "Available" : "Unsupported", ObservedAt = now });
    }

    private static void AppendThermal(JsonElement thermal, List<MetricReading> readings, DateTimeOffset now)
    {
        var index = 0;
        foreach (var temperature in JsonValue.Array(thermal, "Temperatures"))
        {
            index++;
            var key = "temperature:" + (JsonValue.Text(temperature, "MemberId") ?? JsonValue.Text(temperature, "Id") ?? JsonValue.Text(temperature, "Number") ?? index.ToString(CultureInfo.InvariantCulture));
            AddReading(readings, temperature, key, JsonValue.Text(temperature, "Name") ?? JsonValue.Text(temperature, "SensorNumber") ?? "温度传感器", "temperature.celsius",
                JsonValue.Number(temperature, "ReadingCelsius") ?? JsonValue.Number(temperature, "CurrentReading"), "°C", now, "ReadingCelsius", "CurrentReading");
        }
        index = 0;
        foreach (var fan in JsonValue.Array(thermal, "Fans"))
        {
            index++;
            var key = FanSensorKey(fan, index);
            var name = JsonValue.Text(fan, "Name") ?? JsonValue.Text(fan, "FanName") ?? "风扇";
            var unit = JsonValue.Text(fan, "ReadingUnits") ?? JsonValue.Text(fan, "Units");
            var reading = JsonValue.Number(fan, "Reading") ?? JsonValue.Number(fan, "CurrentReading");
            var rpm = JsonValue.Number(fan, "ReadingRPM") ?? (unit?.Equals("RPM", StringComparison.OrdinalIgnoreCase) == true ? reading : null);
            var rpmFields = unit?.Equals("RPM", StringComparison.OrdinalIgnoreCase) == true ? new[] { "ReadingRPM", "Reading", "CurrentReading" } : ["ReadingRPM"];
            AddReading(readings, fan, key, name + "转速", "fan.rpm", rpm, "RPM", now, rpmFields);
            // 业务百分比统一为一个指标，优先原生 PWM，再取原生转速百分比；不从 RPM 换算或平均两个来源。
            var percentageUnit = unit?.Equals("Percent", StringComparison.OrdinalIgnoreCase) == true || unit == "%";
            var percent = NativeFanPwmPercentage(fan) ?? new[]
            {
                JsonValue.Number(fan, "SpeedPercent"), percentageUnit ? JsonValue.Number(fan, "Reading") : null,
                percentageUnit ? JsonValue.Number(fan, "CurrentReading") : null
            }.FirstOrDefault(ValidFanPercentage);
            var hasPercentageField = JsonValue.Has(fan, "SpeedPercent") || percentageUnit && (JsonValue.Has(fan, "Reading") || JsonValue.Has(fan, "CurrentReading"))
                || JsonValue.Has(fan, "PwmPercent") || new[] { "Hp", "Hpe", "Dell" }.Any(vendor => JsonValue.Has(fan, "Oem", vendor, "PwmPercent"));
            AddReading(readings, fan, key, FanPercentageName(name), "fan.percent", percent, "%", now, hasPercentageField);
        }
    }

    private protected static bool ValidFanPercentage(double? value) => value is >= 0 and <= 100 && double.IsFinite(value.Value);

    /// <summary>原生 PWM 字段只用于驱动选择来源，公开模型统一返回 fan.percent。</summary>
    private protected static double? NativeFanPwmPercentage(JsonElement fan) => new[]
    {
        JsonValue.Number(fan, "PwmPercent"), JsonValue.Number(fan, "Oem", "Hp", "PwmPercent"),
        JsonValue.Number(fan, "Oem", "Hpe", "PwmPercent"), JsonValue.Number(fan, "Oem", "Dell", "PwmPercent")
    }.FirstOrDefault(ValidFanPercentage);

    private protected static string FanPercentageName(string name) => name + "转速百分比（%）";

    private static void AddReading(List<MetricReading> readings, JsonElement resource, string sensorKey, string name, string code, double? value, string unit, DateTimeOffset now, params string[] fields)
        => AddReading(readings, resource, sensorKey, name, code, value, unit, now, fields.Any(field => JsonValue.Has(resource, field)));

    private static void AddReading(List<MetricReading> readings, JsonElement resource, string sensorKey, string name, string code, double? value, string unit, DateTimeOffset now, bool hasField)
    {
        var state = JsonValue.Text(resource, "Status", "State");
        var availability = state?.Equals("Absent", StringComparison.OrdinalIgnoreCase) == true ? "NotInstalled"
            : value is not null ? "Available" : hasField ? "TemporarilyUnavailable" : "Unsupported";
        readings.Add(new() { SensorKey = sensorKey, Name = name, MetricCode = code, Value = availability == "NotInstalled" ? null : value,
            Unit = unit, Availability = availability, Health = JsonValue.Health(resource), ObservedAt = now });
    }

    private sealed record Resources(JsonElement Root, JsonElement System, JsonElement? Manager, JsonElement? Chassis, string? PowerLink, string? ThermalLink);
}
