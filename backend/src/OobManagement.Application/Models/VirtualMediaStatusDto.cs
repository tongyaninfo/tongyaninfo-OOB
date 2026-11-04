namespace OobManagement.Application.Models;

/// <summary>查询能力与介质状态不创建挂载；未配置可达镜像地址时明确阻断。</summary>
public sealed record VirtualMediaStatusDto(string ServerId, bool Supported, bool CanMount,
    bool MediaBaseUrlConfigured, IReadOnlyList<VirtualMediaSlotDto> Slots,
    IReadOnlyList<VirtualMediaOperationDto> Operations, IReadOnlyList<string> Warnings);
