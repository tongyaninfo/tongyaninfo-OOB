using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

public interface IMcpPolicyService
{
    Task<ServiceResult<McpAccessContext>> AuthorizeAsync(McpAccessRequest request, CancellationToken ct);
    /// <summary>宿主内部读取运行限制；不提供浏览器匿名管理接口。</summary>
    Task<ServiceResult<McpConfigurationDto>> RuntimeConfigurationAsync(CancellationToken ct);
    Task<ServiceResult<McpClientDto>> ConsentAsync(string oauthClientId, UserSessionContext actor, CancellationToken ct);
    Task<bool> IsOriginRegisteredAsync(string origin, CancellationToken ct);
}
