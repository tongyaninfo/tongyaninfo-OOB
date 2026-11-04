using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Integration.Tests;

/// <summary>真实 HTTP/存储测试的介质替身，不含 Socket 或真实设备依赖。</summary>
public sealed class FakeVirtualMediaDriver(string type) : IVirtualMediaDriver
{
    public string ControllerType => type;
    public bool Inserted { get; set; }
    public string? Image { get; set; }
    public string Result { get; set; } = "Submitted";
    public int Mounts { get; private set; }
    public int Ejects { get; private set; }
    public Func<Task>? BeforeRead { get; set; }
    public void Reset() { Inserted = false; Image = null; Result = "Submitted"; Mounts = 0; Ejects = 0; BeforeRead = null; }
    public async Task<ServiceResult<IReadOnlyList<VirtualMediaDeviceSlot>>> ReadAsync(OutOfBandConnection connection, CancellationToken ct)
    {
        if (BeforeRead is not null) await BeforeRead();
        return ServiceResult<IReadOnlyList<VirtualMediaDeviceSlot>>.Ok([
            new("CD", "虚拟光驱", "CD", Inserted, true, Image, "fixture:insert", "fixture:eject")
        ]);
    }
    public Task<ServiceResult<string>> MountAsync(OutOfBandConnection connection, VirtualMediaDeviceSlot slot, Uri image, CancellationToken ct)
    { Mounts++; if (Result != "Unknown") { Inserted = true; Image = image.AbsoluteUri; } return Task.FromResult(ServiceResult<string>.Ok(Result)); }
    public Task<ServiceResult<string>> EjectAsync(OutOfBandConnection connection, VirtualMediaDeviceSlot slot, CancellationToken ct)
    { Ejects++; if (Result != "Unknown") { Inserted = false; Image = null; } return Task.FromResult(ServiceResult<string>.Ok(Result)); }
}
