using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>仅管理员使用的邮件配置与明确发件入口；每次操作重新验证实际身份和安全版本。</summary>
public interface IMailService
{
    Task<ServiceResult<MailConfigurationDto>> ConfigurationAsync(UserSessionContext actor, CancellationToken ct);
    Task<ServiceResult<MailConfigurationDto>> SaveConfigurationAsync(UserSessionContext actor, MailConfigurationRequest request, CancellationToken ct);
    Task<ServiceResult<MailProbeDto>> ProbeAsync(UserSessionContext actor, CancellationToken ct);
    Task<ServiceResult<MailMessageDto>> SendTestAsync(UserSessionContext actor, MailSendRequest request, CancellationToken ct);
    Task<ServiceResult<MailMessageDto>> SendManualAsync(UserSessionContext actor, MailSendRequest request, CancellationToken ct);
    Task<ServiceResult<PageData<MailMessageDto>>> ListAsync(UserSessionContext actor, MailMessageQuery query, CancellationToken ct);
    Task<ServiceResult<MailMessageDto>> GetAsync(UserSessionContext actor, long id, CancellationToken ct);
    Task<ServiceResult<MailMessageDto>> GetByRequestAsync(UserSessionContext actor, string requestId, CancellationToken ct);
}
