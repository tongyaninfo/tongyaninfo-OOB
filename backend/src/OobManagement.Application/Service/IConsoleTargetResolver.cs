using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>统一从已登记服务器读取目标与凭据，测试可完全替换，不触达真机。</summary>
public interface IConsoleTargetResolver
{
    Task<ServiceResult<ConsoleTarget>> ResolveAsync(long serverId, long userId, CancellationToken ct);
    /// <summary>已有独立挂载按账号状态和设备身份供给，不让控制台撤权或改密隐式拔盘。</summary>
    Task<ServiceResult<ConsoleTarget>> ResolveMediaContentAsync(long serverId, long userId, CancellationToken ct);
}
