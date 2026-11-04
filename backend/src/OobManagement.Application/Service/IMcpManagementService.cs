using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>管理员配置与审计入口，所有方法再次核验实际用户身份。</summary>
public interface IMcpManagementService
{
    Task<ServiceResult<McpConfigurationDto>> ConfigurationAsync(UserSessionContext actor, CancellationToken ct);
    Task<ServiceResult<McpConfigurationDto>> SaveConfigurationAsync(UserSessionContext actor, McpConfigurationRequest request, CancellationToken ct);
    Task<ServiceResult<McpConfigurationDto>> SetEnabledAsync(UserSessionContext actor, bool enabled, McpVersionRequest request, CancellationToken ct);
    Task<ServiceResult<IReadOnlyList<McpToolDto>>> ToolsAsync(UserSessionContext actor, CancellationToken ct);
    Task<ServiceResult<McpOptionsDto>> OptionsAsync(UserSessionContext actor, CancellationToken ct);
    Task<ServiceResult<PageData<McpClientDto>>> ListClientsAsync(UserSessionContext actor, McpClientQuery query, CancellationToken ct);
    Task<ServiceResult<McpClientDto>> GetClientAsync(UserSessionContext actor, long id, CancellationToken ct);
    Task<ServiceResult<McpClientDto>> SaveClientAsync(UserSessionContext actor, long? id, McpClientRequest request, CancellationToken ct);
    Task<ServiceResult<McpClientDto>> SetClientEnabledAsync(UserSessionContext actor, long id, bool enabled, McpVersionRequest request, CancellationToken ct);
    Task<ServiceResult<McpClientDto>> RevokeClientAsync(UserSessionContext actor, long id, McpVersionRequest request, CancellationToken ct);
    Task<ServiceResult<McpSecretDto>> RotateSecretAsync(UserSessionContext actor, long id, McpVersionRequest request, CancellationToken ct);
    Task<ServiceResult<PageData<McpCallLogDto>>> ListLogsAsync(UserSessionContext actor, McpLogQuery query, CancellationToken ct);
    Task<ServiceResult<McpCallLogDto>> GetLogAsync(UserSessionContext actor, long id, CancellationToken ct);
    Task<ServiceResult<PageData<McpConfigAuditDto>>> ListConfigurationAuditsAsync(UserSessionContext actor, int current, int pageSize, CancellationToken ct);
}
