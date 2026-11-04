namespace OobManagement.Application.Models;

/// <summary>保留设备介质槽的真实插入与写保护状态，外部挂载不冒充平台所属。</summary>
public sealed record VirtualMediaSlotDto(string Id, string Name, string MediaType, bool Inserted,
    bool WriteProtected, string? ImageName, string? MountedImageId, bool CanMount, bool CanEject);
