namespace OobManagement.Application.Models;

/// <summary>电源页实时只读能力与运行开关；不依赖库存缓存，也不创建操作意图。</summary>
public sealed record PowerCapabilitiesDto(bool Enabled, DeviceCapabilities Capabilities, string? PowerState);
