using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Desktop.Tests;

/// <summary>无 HTTP、Socket 或原生设备依赖的替身；电源测试只能增加内存计数。</summary>
public sealed class SafeDevice(string controllerType) : IOutOfBandService
{
    public string ControllerType => controllerType;
    public int PowerWrites { get; private set; }
    public int Reads { get; private set; }
    public TelemetryReading? NextReading { get; set; }
    private static DeviceCapabilities Capabilities => new() { SupportsInventory = true,
        PowerActions = [PowerAction.On, PowerAction.ForceOff, PowerAction.ForceRestart] };
    public Task<ConnectionTestResult> TestConnectionAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConnectionTestResult { Success = true, Identity = new() { Model = "SQLite fixture", PowerState = "On" }, Capabilities = Capabilities });
    public Task<DeviceCapabilities> GetCapabilitiesAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default) => Task.FromResult(Capabilities);
    public Task<ConnectionTestResult> ReadPowerCapabilitiesAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default) => TestConnectionAsync(connection, cancellationToken);
    public Task<DeviceInventory> ReadInventoryAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeviceInventory { Identity = new() { Model = "SQLite fixture" } });
    public Task<TelemetryReading> ReadTelemetryAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult(NextReading ?? new TelemetryReading { Identity = new() { PowerState = "On" },
            Metrics = [new() { SensorKey = "system", Name = "整机功率", MetricCode = "power.watts", Unit = "W", Value = 123 }] });
    }
    public Task<UidDeviceResult> ReadUidAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default) =>
        Task.FromResult(new UidDeviceResult { Status = new(UidState.Unknown, false, [], DateTimeOffset.UtcNow),
            ErrorCode = "UID_UNSUPPORTED", ErrorMessage = "夹具不提供UID" });
    public Task<UidDeviceResult> SetUidAsync(OutOfBandConnection connection, UidState state, CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeBeforeSend = null) => ReadUidAsync(connection, cancellationToken);
    public async Task<PowerCommandResult> ExecutePowerActionAsync(OutOfBandConnection connection, PowerAction action,
        CancellationToken cancellationToken = default, Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeBeforeSend = null)
    {
        if (authorizeBeforeSend is not null)
        {
            var authorization = await authorizeBeforeSend(cancellationToken);
            if (!authorization.Success) return new() { Status = "NotSent", ErrorCode = authorization.ErrorCode, Message = authorization.ErrorMessage! };
        }
        PowerWrites++;
        return new() { Status = "Unknown", Message = "测试夹具保持结果未知" };
    }
}
