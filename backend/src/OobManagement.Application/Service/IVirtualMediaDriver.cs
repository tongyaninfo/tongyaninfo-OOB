using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>介质通道独立于画面与电源，只提供固定的读取、只读挂载和卸载。</summary>
public interface IVirtualMediaDriver
{
    string ControllerType { get; }
    Task<ServiceResult<IReadOnlyList<VirtualMediaDeviceSlot>>> ReadAsync(OutOfBandConnection connection, CancellationToken ct);
    Task<ServiceResult<string>> MountAsync(OutOfBandConnection connection, VirtualMediaDeviceSlot slot, Uri image, CancellationToken ct);
    Task<ServiceResult<string>> EjectAsync(OutOfBandConnection connection, VirtualMediaDeviceSlot slot, CancellationToken ct);
}
