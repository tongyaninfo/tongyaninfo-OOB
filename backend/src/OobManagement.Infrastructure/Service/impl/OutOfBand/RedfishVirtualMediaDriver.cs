using System.Text.Json;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Infrastructure.Service.impl.OutOfBand;

/// <summary>按资源链接发现旧代设备的介质槽；Dell 标准动作与 iLO4 HP OEM 动作分别映射。</summary>
public sealed class RedfishVirtualMediaDriver(string controllerType, Func<OutOfBandConnection, HttpClient>? clientFactory = null) : IVirtualMediaDriver
{
    public string ControllerType => controllerType;

    public async Task<ServiceResult<IReadOnlyList<VirtualMediaDeviceSlot>>> ReadAsync(OutOfBandConnection connection, CancellationToken ct)
    {
        var created = RedfishTransport.Create(connection, clientFactory, compatibilityRead: ControllerType == "iLO4");
        if (!created.Success) return ServiceResult<IReadOnlyList<VirtualMediaDeviceSlot>>.Fail("MEDIA_CONNECTION_INVALID", created.Failure!.SafeMessage);
        await using var transport = created.Data;
        var root = await transport.GetAsync("/redfish/v1/", ct);
        if (!root.Success) return ServiceResult<IReadOnlyList<VirtualMediaDeviceSlot>>.Fail("MEDIA_READ_FAILED", root.Failure!.SafeMessage, 502);
        var managers = await transport.CollectionAsync(JsonValue.ResourceLink(root.Data, "Managers"), "管理控制器", ct);
        var slots = new List<VirtualMediaDeviceSlot>();
        foreach (var manager in managers)
        {
            var link = JsonValue.ResourceLink(manager, "VirtualMedia");
            if (link is null) continue;
            foreach (var item in await transport.CollectionAsync(link, "虚拟介质", ct))
            {
                var id = JsonValue.Link(item);
                if (id is null || !transport.Resolve(id).Success) continue;
                var types = JsonValue.Array(item, "MediaTypes").Select(x => x.GetString()).ToArray();
                var type = types.Any(x => x is "CD" or "DVD") ? "CD" : types.Any(x => x == "Floppy") ? "Floppy" : "Unsupported";
                var insert = JsonValue.Text(item, "Actions", "#VirtualMedia.InsertMedia", "target");
                var eject = JsonValue.Text(item, "Actions", "#VirtualMedia.EjectMedia", "target");
                var oem = false;
                if (ControllerType == "iLO4")
                {
                    insert = JsonValue.Text(item, "Oem", "Hp", "Actions", "#HpiLOVirtualMedia.InsertVirtualMedia", "target");
                    eject = JsonValue.Text(item, "Oem", "Hp", "Actions", "#HpiLOVirtualMedia.EjectVirtualMedia", "target");
                    oem = true;
                }
                var image = JsonValue.Text(item, "Image");
                var inserted = JsonValue.Get(item, "Inserted")?.ValueKind == JsonValueKind.True
                    || JsonValue.Text(item, "ConnectedVia") is { } via && !via.Equals("NotConnected", StringComparison.OrdinalIgnoreCase);
                slots.Add(new(id, JsonValue.Text(item, "Name") ?? type, type, inserted,
                    JsonValue.Get(item, "WriteProtected")?.ValueKind == JsonValueKind.True, image, insert, eject, oem));
            }
        }
        if (transport.Diagnostics.Any(x => x.Code is not "NotSupported"))
            return ServiceResult<IReadOnlyList<VirtualMediaDeviceSlot>>.Fail("MEDIA_READ_INCOMPLETE", "介质状态读取不完整，已阻断写操作", 502);
        return ServiceResult<IReadOnlyList<VirtualMediaDeviceSlot>>.Ok(slots);
    }

    public Task<ServiceResult<string>> MountAsync(OutOfBandConnection connection, VirtualMediaDeviceSlot slot, Uri image, CancellationToken ct)
        => SendAsync(connection, slot, image, ct);

    public Task<ServiceResult<string>> EjectAsync(OutOfBandConnection connection, VirtualMediaDeviceSlot slot, CancellationToken ct)
        => SendAsync(connection, slot, null, ct);

    private async Task<ServiceResult<string>> SendAsync(OutOfBandConnection connection, VirtualMediaDeviceSlot slot, Uri? image, CancellationToken ct)
    {
        var created = RedfishTransport.Create(connection, clientFactory, compatibilityRead: ControllerType == "iLO4");
        if (!created.Success) return ServiceResult<string>.Fail("MEDIA_CONNECTION_INVALID", created.Failure!.SafeMessage);
        await using var transport = created.Data;
        return await transport.SendVirtualMediaAsync(slot, image, ct);
    }
}
