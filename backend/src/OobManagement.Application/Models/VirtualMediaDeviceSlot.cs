namespace OobManagement.Application.Models;

/// <summary>内部驱动资源；上游地址和原镜像 URL 只在后端用于所属关系核对。</summary>
public sealed record VirtualMediaDeviceSlot(string Id, string Name, string MediaType, bool Inserted,
    bool WriteProtected, string? ImageUri, string? InsertTarget, string? EjectTarget,
    bool UsesIloOem = false);
