using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>统一带外读写边界，厂商差异仅留在对应驱动实现。</summary>
public interface IOutOfBandService
{
    string ControllerType { get; }
    Task<ConnectionTestResult> TestConnectionAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default);
    Task<DeviceCapabilities> GetCapabilitiesAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default);
    /// <summary>仅只读发现唯一主机及其电源动作，明确区分读取失败与设备未声明动作。</summary>
    Task<ConnectionTestResult> ReadPowerCapabilitiesAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default);
    Task<DeviceInventory> ReadInventoryAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default);
    Task<TelemetryReading> ReadTelemetryAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default);
    Task<UidDeviceResult> ReadUidAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default);
    /// <summary>交互应用传入发送前授权复核；驱动发现资源后、实际写请求前只调用一次，不影响发送后的结果记录。</summary>
    Task<UidDeviceResult> SetUidAsync(OutOfBandConnection connection, UidState state, CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeBeforeSend = null);
    Task<PowerCommandResult> ExecutePowerActionAsync(OutOfBandConnection connection, PowerAction action, CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeBeforeSend = null);
}
