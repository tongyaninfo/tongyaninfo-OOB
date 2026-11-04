using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>OAuth 组件由宿主实现；同步以固定 clientId 幂等，不存业务秘密。</summary>
public interface IMcpOAuthRegistrationService
{
    Task<ServiceResult<bool>> SyncAsync(McpOAuthClientRegistration request, CancellationToken ct);
    Task<ServiceResult<bool>> RevokeAsync(string clientId, CancellationToken ct);
    Task<ServiceResult<McpSecretDto>> RotateSecretAsync(string clientId, CancellationToken ct);
}
