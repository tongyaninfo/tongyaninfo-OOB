using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>管理员报表计划及只读预览；后台发送使用已明确启用的计划。</summary>
public interface IMailReportService
{
    Task<ServiceResult<MailReportConfigurationDto>> ConfigurationAsync(UserSessionContext actor, CancellationToken ct);
    Task<ServiceResult<MailReportConfigurationDto>> SaveAsync(UserSessionContext actor, MailReportConfigurationRequest request, CancellationToken ct);
    Task<ServiceResult<MailReportPreviewDto>> PreviewAsync(UserSessionContext actor, MailReportPreviewRequest request, CancellationToken ct);
    Task DispatchDueAsync(CancellationToken ct);
}
